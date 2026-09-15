using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.LocalRecs.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using BaseItemKind = Jellyfin.Data.Enums.BaseItemKind;

namespace Jellyfin.Plugin.LocalRecs.VirtualLibrary
{
    /// <summary>
    /// Manages virtual library symlinks for per-user recommendations.
    /// Creates filesystem symlinks in per-user directories that point to original media files.
    /// Symlinks replace the prior .strm approach, which broke in Jellyfin 10.11.7 when
    /// the server stopped accepting local paths in .strm files (security fix GHSA-j2hf-x4q5-47j3).
    /// </summary>
    public class VirtualLibraryManager
    {
        private static readonly string[] TrailerSuffixes = { "-trailer", ".trailer", "_trailer" };

        private readonly ILogger<VirtualLibraryManager> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly string _virtualLibraryBasePath;

        private readonly ConcurrentDictionary<Guid, object> _userLocks = new ConcurrentDictionary<Guid, object>();

        /// <summary>
        /// Initializes a new instance of the <see cref="VirtualLibraryManager"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="libraryManager">Library manager for media access.</param>
        /// <param name="virtualLibraryBasePath">Base path for virtual libraries.</param>
        public VirtualLibraryManager(
            ILogger<VirtualLibraryManager> logger,
            ILibraryManager libraryManager,
            string virtualLibraryBasePath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _virtualLibraryBasePath = virtualLibraryBasePath ?? throw new ArgumentNullException(nameof(virtualLibraryBasePath));
        }

        /// <summary>
        /// Gets the virtual library path for a specific user and media type.
        /// </summary>
        /// <param name="userId">User ID.</param>
        /// <param name="mediaType">Media type (Movie or Series).</param>
        /// <returns>Full path to the user's virtual library directory.</returns>
        public string GetUserLibraryPath(Guid userId, MediaType mediaType)
        {
            var subfolder = mediaType == MediaType.Movie ? "movies" : "tv";
            return Path.Combine(_virtualLibraryBasePath, userId.ToString(), subfolder);
        }

        /// <summary>
        /// Ensures the virtual library directories exist for a user.
        /// </summary>
        /// <param name="userId">User ID.</param>
        /// <param name="username">Username for logging purposes.</param>
        /// <returns>True if directories were created successfully, false otherwise.</returns>
        public bool EnsureUserDirectoriesExist(Guid userId, string? username = null)
        {
            var displayName = username ?? userId.ToString();

            try
            {
                Directory.CreateDirectory(GetUserLibraryPath(userId, MediaType.Movie));
                Directory.CreateDirectory(GetUserLibraryPath(userId, MediaType.Series));

                _logger.LogDebug(
                    "Ensured virtual library directories exist for user {Username} ({UserId})",
                    displayName,
                    userId);

                return true;
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Failed to create directories for user {Username} ({UserId})", displayName, userId);
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "Access denied creating directories for user {Username} ({UserId})", displayName, userId);
                return false;
            }
        }

        /// <summary>
        /// Deletes all virtual library directories for a user.
        /// </summary>
        /// <param name="userId">User ID.</param>
        /// <param name="username">Username for logging purposes.</param>
        /// <returns>True if directories were deleted or didn't exist, false on error.</returns>
        public bool DeleteUserDirectories(Guid userId, string? username = null)
        {
            var displayName = username ?? userId.ToString();
            var userPath = Path.Combine(_virtualLibraryBasePath, userId.ToString());

            if (!Directory.Exists(userPath))
            {
                return true;
            }

            try
            {
                Directory.Delete(userPath, recursive: true);
                _logger.LogDebug(
                    "Deleted virtual library directories for user {Username} ({UserId})",
                    displayName,
                    userId);
                return true;
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Failed to delete directories for user {Username} ({UserId})", displayName, userId);
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "Access denied deleting directories for user {Username} ({UserId})", displayName, userId);
                return false;
            }
        }

        /// <summary>
        /// Clears and recreates recommendations for a user. Thread-safe per user.
        /// </summary>
        /// <param name="userId">User ID.</param>
        /// <param name="recommendations">List of recommended items.</param>
        /// <param name="mediaType">Media type (Movie or Series).</param>
        /// <returns>Number of items created.</returns>
        /// <exception cref="ArgumentNullException">Thrown when recommendations is null.</exception>
        public int SyncRecommendations(
            Guid userId,
            IReadOnlyList<ScoredRecommendation> recommendations,
            MediaType mediaType)
        {
            if (recommendations == null)
            {
                throw new ArgumentNullException(nameof(recommendations));
            }

            var userLock = _userLocks.GetOrAdd(userId, _ => new object());
            lock (userLock)
            {
                return SyncRecommendationsInternal(userId, recommendations, mediaType);
            }
        }

        private int SyncRecommendationsInternal(
            Guid userId,
            IReadOnlyList<ScoredRecommendation> recommendations,
            MediaType mediaType)
        {
            var libraryPath = GetUserLibraryPath(userId, mediaType);
            Directory.CreateDirectory(libraryPath);

            // Folders that stay recommended are left in place and only changed files are rewritten, so
            // Jellyfin doesn't re-read every item on each refresh. Everything else is removed below.
            var keptFolders = new HashSet<string>(StringComparer.Ordinal);
            var createdCount = 0;
            foreach (var rec in recommendations)
            {
                try
                {
                    var item = _libraryManager.GetItemById(rec.ItemId);
                    if (item == null)
                    {
                        _logger.LogWarning("Item {ItemId} not found in library", rec.ItemId);
                        continue;
                    }

                    if (string.IsNullOrEmpty(item.Path))
                    {
                        _logger.LogDebug("Item {ItemId} ({ItemName}) has no path, skipping", rec.ItemId, item.Name);
                        continue;
                    }

                    if (item is Series series)
                    {
                        CreateSeriesStructure(libraryPath, series);
                    }
                    else
                    {
                        CreateMovieFolderStructure(libraryPath, item);
                    }

                    var folder = Path.Combine(libraryPath, GenerateFolderName(item));
                    DeleteDanglingLinks(folder);
                    keptFolders.Add(folder);

                    createdCount++;
                }
                catch (UnauthorizedAccessException ex)
                {
                    LogSymlinkPermissionError(ex, rec.ItemId);
                }
                catch (IOException ex)
                {
                    _logger.LogError(ex, "Failed to create virtual library entry for item {ItemId} (IO error)", rec.ItemId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to create virtual library entry for item {ItemId}", rec.ItemId);
                }
            }

            foreach (var dir in Directory.GetDirectories(libraryPath))
            {
                if (!keptFolders.Contains(dir))
                {
                    DeleteFolder(dir);
                }
            }

            _logger.LogDebug(
                "Updated {MediaType} recommendations for user {UserId}: {Created} items created",
                mediaType,
                userId,
                createdCount);

            return createdCount;
        }

        /// <summary>
        /// Removes links whose source file is gone (e.g. a deleted episode) from a folder that is kept.
        /// </summary>
        private void DeleteDanglingLinks(string folder)
        {
            if (!Directory.Exists(folder))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                if (info.LinkTarget is not null && !File.Exists(file))
                {
                    info.Delete();
                }
            }
        }

        private void DeleteFolder(string path)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Failed to delete virtual library folder (IO error): {Path}", path);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "Failed to delete virtual library folder (access denied): {Path}", path);
            }
        }

        private void CreateMovieFolderStructure(string libraryPath, BaseItem item)
        {
            if (string.IsNullOrEmpty(item.Path))
            {
                return;
            }

            var folderName = GenerateFolderName(item);
            var movieFolderPath = Path.Combine(libraryPath, folderName);
            Directory.CreateDirectory(movieFolderPath);
            WriteNfo(Path.Combine(movieFolderPath, folderName + ".nfo"), "movie", item);

            var extension = Path.GetExtension(item.Path);
            var mediaLinkPath = Path.Combine(movieFolderPath, folderName + extension);
            CreateSymlink(mediaLinkPath, item.Path);

            var sourceDir = Path.GetDirectoryName(item.Path);
            var trailerCount = LinkTrailers(movieFolderPath, folderName, sourceDir);
            var artworkCount = LinkItemArtwork(movieFolderPath, item);

            _logger.LogDebug(
                "Created movie folder: {FolderName} with symlink, {TrailerCount} trailer(s), {ArtworkCount} artwork file(s)",
                folderName,
                trailerCount,
                artworkCount);
        }

        private void CreateSeriesStructure(string libraryPath, Series series)
        {
            var seriesFolderName = GenerateFolderName(series);
            var seriesPath = Path.Combine(libraryPath, seriesFolderName);
            Directory.CreateDirectory(seriesPath);

            // Series-level artwork: use ImageInfos (images may live in metadata cache, not on disk
            // next to episodes) so Jellyfin can render series tiles. Also write a minimal
            // tvshow.nfo so the library scanner reliably identifies this folder as a series
            // instead of treating its episodes as standalone items.
            var seriesSourceDir = series.Path;
            var trailerCount = LinkTrailers(seriesPath, seriesFolderName, seriesSourceDir);
            var artworkCount = LinkItemArtwork(seriesPath, series);
            WriteNfo(Path.Combine(seriesPath, "tvshow.nfo"), "tvshow", series);

            var episodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                ParentId = series.Id,
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                Recursive = true
            })
            .OfType<Episode>()
            .OrderBy(e => e.ParentIndexNumber ?? 0)
            .ThenBy(e => e.IndexNumber ?? 0)
            .ToList();

            if (episodes.Count == 0)
            {
                _logger.LogDebug("Series {SeriesName} has no episodes, skipping", series.Name);
                return;
            }

            var episodeCount = 0;
            foreach (var seasonGroup in episodes.GroupBy(e => e.ParentIndexNumber ?? 0).OrderBy(g => g.Key))
            {
                var seasonNumber = seasonGroup.Key;
                var seasonFolder = seasonNumber == 0 ? "Specials" : $"Season {seasonNumber:D2}";
                var seasonPath = Path.Combine(seriesPath, seasonFolder);
                Directory.CreateDirectory(seasonPath);

                foreach (var episode in seasonGroup)
                {
                    if (string.IsNullOrEmpty(episode.Path))
                    {
                        continue;
                    }

                    var baseFilename = GenerateEpisodeBaseFilename(episode);
                    var extension = Path.GetExtension(episode.Path);
                    var linkPath = Path.Combine(seasonPath, baseFilename + extension);

                    try
                    {
                        CreateSymlink(linkPath, episode.Path);
                        episodeCount++;
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        LogSymlinkPermissionError(ex, episode.Id);
                    }
                    catch (IOException ex)
                    {
                        _logger.LogError(ex, "Failed to symlink episode {EpisodeName}", episode.Name);
                    }
                }
            }

            _logger.LogDebug(
                "Created series folder: {SeriesFolder} with {EpisodeCount} episodes, {TrailerCount} trailer(s), {ArtworkCount} artwork file(s)",
                seriesFolderName,
                episodeCount,
                trailerCount,
                artworkCount);
        }

        /// <summary>
        /// Creates a symbolic link at <paramref name="linkPath"/> pointing to <paramref name="targetPath"/>.
        /// On Windows, this requires Administrator privileges or Developer Mode enabled.
        /// </summary>
        private void CreateSymlink(string linkPath, string targetPath)
        {
            var existing = new FileInfo(linkPath);
            if (existing.LinkTarget == targetPath)
            {
                return;
            }

            // LinkTarget also catches a dangling link, which FileInfo.Exists reports as missing.
            if (existing.Exists || existing.LinkTarget is not null)
            {
                existing.Delete();
            }

            File.CreateSymbolicLink(linkPath, targetPath);
        }

        private void LogSymlinkPermissionError(UnauthorizedAccessException ex, Guid itemId)
        {
            const string Msg = "Access denied creating symlink for item {ItemId}. On Windows, Jellyfin must run as Administrator or the host must have Developer Mode enabled (Settings > Privacy & security > For developers). See README troubleshooting section.";
            _logger.LogError(ex, Msg, itemId);
        }

        /// <summary>
        /// Symlinks trailer files from the source directory. Jellyfin discovers trailers via the
        /// <c>trailers/</c> subfolder or files with a <c>-trailer</c> suffix; we mirror those.
        /// </summary>
        private int LinkTrailers(string targetFolder, string baseFilename, string? sourceDir)
        {
            if (string.IsNullOrEmpty(sourceDir) || !Directory.Exists(sourceDir))
            {
                return 0;
            }

            var count = 0;

            try
            {
                // trailers/ subfolder: mirror it by name
                var sourceTrailersDir = Path.Combine(sourceDir, "trailers");
                if (Directory.Exists(sourceTrailersDir))
                {
                    var targetTrailersDir = Path.Combine(targetFolder, "trailers");
                    Directory.CreateDirectory(targetTrailersDir);
                    foreach (var trailer in Directory.GetFiles(sourceTrailersDir))
                    {
                        var linkPath = Path.Combine(targetTrailersDir, Path.GetFileName(trailer));
                        TryCreateSymlink(linkPath, trailer);
                        count++;
                    }
                }

                // -trailer suffix siblings: symlink with the movie's baseFilename as prefix
                var siblingTrailers = Directory.GetFiles(sourceDir)
                    .Where(f =>
                    {
                        var name = Path.GetFileNameWithoutExtension(f);
                        return TrailerSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase))
                               || name.Equals("trailer", StringComparison.OrdinalIgnoreCase);
                    })
                    .ToList();

                for (var i = 0; i < siblingTrailers.Count; i++)
                {
                    var trailer = siblingTrailers[i];
                    var ext = Path.GetExtension(trailer);
                    var linkName = siblingTrailers.Count == 1
                        ? $"{baseFilename}-trailer{ext}"
                        : $"{baseFilename}-trailer{i + 1}{ext}";
                    var linkPath = Path.Combine(targetFolder, linkName);
                    TryCreateSymlink(linkPath, trailer);
                    count++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to link trailers from {SourceDir}", sourceDir);
            }

            return count;
        }

        /// <summary>
        /// Symlinks artwork from <see cref="BaseItem.ImageInfos"/> into the target folder using
        /// standard filenames Jellyfin auto-discovers. Images may be stored either adjacent to
        /// the media file or in Jellyfin's metadata cache; this uses the item's resolved paths so
        /// both cases work.
        /// </summary>
        private int LinkItemArtwork(string targetFolder, BaseItem item)
        {
            // Multiple filenames per image type: Jellyfin's scanner probes conventional names
            // (folder.jpg, backdrop.jpg) and warns when missing, so we emit those aliases too.
            var mappings = new (ImageType Type, string Filename)[]
            {
                (ImageType.Primary, "poster.jpg"),
                (ImageType.Primary, "folder.jpg"),
                (ImageType.Backdrop, "fanart.jpg"),
                (ImageType.Backdrop, "backdrop.jpg"),
                (ImageType.Logo, "clearlogo.png"),
                (ImageType.Thumb, "landscape.jpg"),
                (ImageType.Banner, "banner.jpg"),
                (ImageType.Art, "clearart.png"),
                (ImageType.Disc, "disc.png")
            };

            var count = 0;

            foreach (var (imageType, filename) in mappings)
            {
                string? sourcePath;
                try
                {
                    sourcePath = item.GetImagePath(imageType, 0);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to resolve {ImageType} for {ItemName}", imageType, item.Name);
                    continue;
                }

                if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                {
                    continue;
                }

                // Preserve source extension for primary types where it matters
                var ext = Path.GetExtension(sourcePath);
                var linkName = string.IsNullOrEmpty(ext)
                    ? filename
                    : Path.ChangeExtension(filename, ext.TrimStart('.'));

                var linkPath = Path.Combine(targetFolder, linkName);
                if (TryCreateSymlink(linkPath, sourcePath))
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Writes a minimal NFO with the metadata the recommendation libraries display, since they never
        /// fetch metadata online. For series, tvshow.nfo also makes Jellyfin's scanner reliably identify
        /// the folder as a Series rather than treating its episodes as standalone items.
        /// </summary>
        private void WriteNfo(string nfoPath, string rootElement, BaseItem item)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append('<').Append(rootElement).AppendLine(">");
            sb.Append("  <title>").Append(XmlEscape(item.Name ?? "Unknown")).AppendLine("</title>");
            if (item.ProductionYear.HasValue)
            {
                sb.Append("  <year>").Append(item.ProductionYear.Value).AppendLine("</year>");
            }

            AppendElement(sb, "plot", item.Overview);
            AppendElement(sb, "tagline", item.Tagline);
            AppendElement(sb, "mpaa", item.OfficialRating);
            AppendElement(sb, "rating", item.CommunityRating?.ToString(CultureInfo.InvariantCulture));
            AppendElement(sb, "criticrating", item.CriticRating?.ToString(CultureInfo.InvariantCulture));
            AppendElement(sb, "premiered", item.PremiereDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            foreach (var genre in item.Genres ?? Array.Empty<string>())
            {
                AppendElement(sb, "genre", genre);
            }

            foreach (var studio in item.Studios ?? Array.Empty<string>())
            {
                AppendElement(sb, "studio", studio);
            }

            var providerIds = item.ProviderIds ?? new Dictionary<string, string>();
            if (providerIds.TryGetValue("Tmdb", out var tmdb) && !string.IsNullOrEmpty(tmdb))
            {
                sb.Append("  <tmdbid>").Append(tmdb).AppendLine("</tmdbid>");
                sb.Append("  <uniqueid type=\"tmdb\">").Append(tmdb).AppendLine("</uniqueid>");
            }

            if (providerIds.TryGetValue("Tvdb", out var tvdb) && !string.IsNullOrEmpty(tvdb))
            {
                sb.Append("  <tvdbid>").Append(tvdb).AppendLine("</tvdbid>");
                sb.Append("  <uniqueid type=\"tvdb\">").Append(tvdb).AppendLine("</uniqueid>");
            }

            if (providerIds.TryGetValue("Imdb", out var imdb) && !string.IsNullOrEmpty(imdb))
            {
                sb.Append("  <imdbid>").Append(imdb).AppendLine("</imdbid>");
                sb.Append("  <uniqueid type=\"imdb\">").Append(imdb).AppendLine("</uniqueid>");
            }

            sb.Append("</").Append(rootElement).AppendLine(">");

            // Only rewrite on change: a newer NFO makes Jellyfin re-read the item on the next scan.
            var content = sb.ToString();
            try
            {
                if (!File.Exists(nfoPath) || File.ReadAllText(nfoPath) != content)
                {
                    File.WriteAllText(nfoPath, content, new UTF8Encoding(false));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write {NfoPath}", nfoPath);
            }
        }

        private string XmlEscape(string value)
            => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

        private void AppendElement(StringBuilder sb, string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                sb.Append("  <").Append(name).Append('>').Append(XmlEscape(value)).Append("</").Append(name).AppendLine(">");
            }
        }

        private bool TryCreateSymlink(string linkPath, string targetPath)
        {
            try
            {
                CreateSymlink(linkPath, targetPath);
                return true;
            }
            catch (UnauthorizedAccessException ex)
            {
                const string Msg = "Access denied creating symlink {LinkPath} -> {TargetPath}. On Windows, Jellyfin must run as Administrator or have Developer Mode enabled.";
                _logger.LogWarning(ex, Msg, linkPath, targetPath);
                return false;
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "IO error creating symlink {LinkPath} -> {TargetPath}", linkPath, targetPath);
                return false;
            }
        }

        private string GenerateFolderName(BaseItem item)
        {
            var title = SanitizeFilename(item.Name ?? "Unknown");
            var year = item.ProductionYear ?? 0;
            var providerId = GetProviderId(item);

            return year > 0
                ? $"{title} ({year}) [{providerId}]"
                : $"{title} [{providerId}]";
        }

        private string GenerateEpisodeBaseFilename(Episode episode)
        {
            var seriesName = SanitizeFilename(episode.SeriesName ?? "Unknown");
            var seasonNum = episode.ParentIndexNumber ?? 0;
            var episodeNum = episode.IndexNumber ?? 0;
            var episodeName = SanitizeFilename(episode.Name ?? "Episode");

            return $"{seriesName} - S{seasonNum:D2}E{episodeNum:D2} - {episodeName}";
        }

        private string GetProviderId(BaseItem item)
        {
            var providerIds = item.ProviderIds ?? new Dictionary<string, string>();

            if (providerIds.TryGetValue("Tmdb", out var tmdbId) && !string.IsNullOrEmpty(tmdbId))
            {
                return $"tmdbid-{tmdbId}";
            }

            if (providerIds.TryGetValue("Tvdb", out var tvdbId) && !string.IsNullOrEmpty(tvdbId))
            {
                return $"tvdbid-{tvdbId}";
            }

            return $"jellyfinid-{item.Id}";
        }

        private string SanitizeFilename(string filename)
        {
            if (string.IsNullOrEmpty(filename))
            {
                return "Unknown";
            }

            var invalidChars = new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };
            var sanitized = string.Join("_", filename.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));

            sanitized = sanitized.Replace("..", "_").Replace("/", "_").Replace("\\", "_");
            sanitized = sanitized.TrimStart('.', '-').TrimEnd();

            if (sanitized.Length > 200)
            {
                sanitized = sanitized.Substring(0, 200);
            }

            return string.IsNullOrEmpty(sanitized) ? "Unknown" : sanitized;
        }
    }
}
