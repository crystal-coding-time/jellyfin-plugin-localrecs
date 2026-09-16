using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LocalRecs.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalRecs.VirtualLibrary
{
    /// <summary>
    /// Gives every user their own "Recommended Movies" and "Recommended Shows" libraries and uses Jellyfin's
    /// library access (each user's list of enabled libraries) so that only that user can see them. A library
    /// is hidden from anyone whose list doesn't include it, so a failure hides recommendations rather than
    /// exposing them.
    /// </summary>
    public sealed class RecommendationLibraryService : IDisposable
    {
        private const string OldOwnerTagPrefix = "localrecs-";

        private static readonly string[] ItemTypes = { "Movie", "Series", "Season", "Episode" };

        private readonly ILogger<RecommendationLibraryService> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly IFileSystem _fileSystem;
        private readonly IProviderManager _providerManager;
        private readonly VirtualLibraryManager _virtualLibraryManager;
        private readonly string _basePath;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Initializes a new instance of the <see cref="RecommendationLibraryService"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="libraryManager">Library manager.</param>
        /// <param name="userManager">User manager.</param>
        /// <param name="fileSystem">File system, used for library scans.</param>
        /// <param name="providerManager">Provider manager, used for library scans.</param>
        /// <param name="virtualLibraryManager">Virtual library manager for per-user folders.</param>
        /// <param name="virtualLibraryBasePath">Base path for virtual libraries.</param>
        public RecommendationLibraryService(
            ILogger<RecommendationLibraryService> logger,
            ILibraryManager libraryManager,
            IUserManager userManager,
            IFileSystem fileSystem,
            IProviderManager providerManager,
            VirtualLibraryManager virtualLibraryManager,
            string virtualLibraryBasePath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _providerManager = providerManager ?? throw new ArgumentNullException(nameof(providerManager));
            _virtualLibraryManager = virtualLibraryManager ?? throw new ArgumentNullException(nameof(virtualLibraryManager));
            _basePath = Normalize(virtualLibraryBasePath ?? throw new ArgumentNullException(nameof(virtualLibraryBasePath))) + "/";
        }

        /// <summary>
        /// Gets the name of a user's recommendation library, as shown on their home screen.
        /// </summary>
        /// <param name="mediaType">Movie or Series.</param>
        /// <param name="username">The user's name.</param>
        /// <returns>The library name.</returns>
        public static string GetLibraryName(MediaType mediaType, string username)
            => $"{(mediaType == MediaType.Movie ? "Recommended Movies" : "Recommended Shows")} ({username})";

        /// <summary>
        /// Sets a user's library access so they see their own recommendation libraries and no one else's.
        /// "Access all libraries" can't hide anything, so it becomes an explicit list of every server library;
        /// such users are remembered, and libraries added to the server later are enabled for them too.
        /// A library an admin removed from a user's list stays removed.
        /// </summary>
        /// <param name="policy">The user's policy, modified in place.</param>
        /// <param name="rememberedAllLibraries">Whether the user was already remembered as having all libraries.</param>
        /// <param name="serverLibraries">All libraries on the server except recommendation libraries.</param>
        /// <param name="newServerLibraries">Server libraries added since the last sync.</param>
        /// <param name="ownLibraries">The user's own recommendation libraries.</param>
        /// <param name="otherRecommendationLibraries">Every other user's recommendation libraries.</param>
        /// <param name="allLibraries">Whether the user should be remembered as having all libraries.</param>
        /// <returns>True if the policy changed and needs saving.</returns>
        public static bool ApplyLibraryAccess(
            UserPolicy policy,
            bool rememberedAllLibraries,
            IReadOnlyCollection<Guid> serverLibraries,
            IReadOnlyCollection<Guid> newServerLibraries,
            IReadOnlyCollection<Guid> ownLibraries,
            IReadOnlyCollection<Guid> otherRecommendationLibraries,
            out bool allLibraries)
        {
            ArgumentNullException.ThrowIfNull(policy);

            allLibraries = rememberedAllLibraries || policy.EnableAllFolders;
            var oldList = policy.EnabledFolders ?? Array.Empty<Guid>();

            IEnumerable<Guid> enabled = policy.EnableAllFolders
                ? serverLibraries
                : oldList.Concat(allLibraries ? newServerLibraries : Array.Empty<Guid>());
            var newList = enabled.Except(otherRecommendationLibraries).Concat(ownLibraries).Distinct().ToArray();

            var changed = policy.EnableAllFolders || oldList.Length != newList.Length || oldList.Except(newList).Any();
            policy.EnableAllFolders = false;
            policy.EnabledFolders = newList;
            return changed;
        }

        /// <summary>
        /// Removes the per-user tags the 0.8.0 pre-release added to users' blocked and allowed tags.
        /// </summary>
        /// <param name="policy">The user's policy, modified in place.</param>
        /// <returns>True if anything was removed.</returns>
        public static bool RemoveOldOwnerTags(UserPolicy policy)
        {
            ArgumentNullException.ThrowIfNull(policy);

            var blocked = (policy.BlockedTags ?? Array.Empty<string>()).Where(t => !IsOldOwnerTag(t)).ToArray();
            var allowed = (policy.AllowedTags ?? Array.Empty<string>()).Where(t => !IsOldOwnerTag(t)).ToArray();
            if (blocked.Length == (policy.BlockedTags?.Length ?? 0) && allowed.Length == (policy.AllowedTags?.Length ?? 0))
            {
                return false;
            }

            policy.BlockedTags = blocked;
            policy.AllowedTags = allowed;
            return true;
        }

        /// <summary>
        /// Configures a recommendation library to use only the plugin's NFO files and linked artwork.
        /// Recommendations are copies of items that already have metadata, so they are never looked up
        /// online (third-party providers mis-matched them and slowed scans down), never have files written
        /// into their folders (which hold links to real artwork), and never have images extracted.
        /// </summary>
        /// <param name="options">Library options, modified in place.</param>
        /// <returns>True if anything changed.</returns>
        public static bool UseLocalMetadataOnly(LibraryOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            var alreadyLocal = !options.EnableRealtimeMonitor
                && options.MetadataSavers is { Length: 0 }
                && !options.SaveLocalMetadata
                && !options.EnableChapterImageExtraction
                && !options.EnableTrickplayImageExtraction
                && ItemTypes.All(t => options.GetTypeOptions(t) is { MetadataFetchers.Length: 0, ImageFetchers.Length: 0 });
            if (alreadyLocal)
            {
                return false;
            }

            // The refresh task writes these folders and then scans them itself, so no folder watching.
            options.EnableRealtimeMonitor = false;
            options.MetadataSavers = Array.Empty<string>();
            options.SaveLocalMetadata = false;
            options.EnableChapterImageExtraction = false;
            options.ExtractChapterImagesDuringLibraryScan = false;
            options.EnableTrickplayImageExtraction = false;
            options.ExtractTrickplayImagesDuringLibraryScan = false;

            // Empty fetcher lists turn off online providers only; local NFO and image readers still run.
            options.TypeOptions = ItemTypes
                .Select(t => new TypeOptions { Type = t, MetadataFetchers = Array.Empty<string>(), ImageFetchers = Array.Empty<string>() })
                .ToArray();
            return true;
        }

        /// <summary>
        /// Makes sure every user has their own two recommendation libraries and can see only those. Idempotent.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the operation.</returns>
        public async Task EnsureAsync(CancellationToken cancellationToken)
        {
            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var users = _userManager.GetUsers().ToList();

                // Access first: a user still on "access all libraries" would see a new library the moment it
                // is created. After this pass nobody has that, so new libraries start out visible to no one,
                // and the second pass then gives each user their own.
                await EnsureAccessAsync(users).ConfigureAwait(false);
                var created = await EnsureLibrariesAsync(users).ConfigureAwait(false);
                await EnsureAccessAsync(users).ConfigureAwait(false);

                if (created >= 10)
                {
                    _logger.LogWarning(
                        "Created {Count} libraries. Jellyfin restarts its folder watchers for each one, which on Linux can exhaust fs.inotify.max_user_watches and stop file watching on your real libraries until Jellyfin is restarted",
                        created);
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Scans the recommendation libraries of the given users. Scanning every user's libraries on every
        /// refresh is what made refreshes take longer the more users a server has, so users whose folders
        /// didn't change are skipped.
        /// </summary>
        /// <param name="userIds">The users whose recommendations changed.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the scans.</returns>
        public async Task ScanAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(userIds);

            if (userIds.Count == 0)
            {
                _logger.LogInformation("No recommendations changed, skipping library scans");
                return;
            }

            // A library's folders are registered when the library is created, and at that point they are
            // still empty, so Jellyfin skips them ("inaccessible or empty") and a later scan of that library
            // finds nothing. This re-registers them now that the recommendations have been written.
            await _libraryManager.ValidateTopLibraryFolders(cancellationToken).ConfigureAwait(false);

            var scanned = 0;
            foreach (var library in GetOwnedLibraries())
            {
                if (library.Locations.Length != 1
                    || GetOwner(Normalize(library.Locations[0])) is not Guid owner
                    || !userIds.Contains(owner))
                {
                    continue;
                }

                if (!Guid.TryParse(library.ItemId, out var itemId)
                    || _libraryManager.GetItemById(itemId) is not CollectionFolder folder)
                {
                    continue;
                }

                scanned++;

                // Same path as the dashboard's "Scan library": it refreshes the library itself first, which
                // picks up a folder that was still empty when the library was created, then scans it.
                _logger.LogDebug("Scanning {LibraryName}", library.Name);
                await _providerManager.RefreshFullItem(
                    folder,
                    new MetadataRefreshOptions(new DirectoryService(_fileSystem)),
                    cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation("Scanned {Count} recommendation libraries", scanned);
        }

        /// <summary>
        /// Starts watching for libraries an admin adds, so users on a managed library list see them right away.
        /// </summary>
        public void StartWatchingLibraries() => _libraryManager.ItemAdded += OnItemAdded;

        /// <inheritdoc />
        public void Dispose()
        {
            _libraryManager.ItemAdded -= OnItemAdded;
            _lock.Dispose();
        }

        private static bool IsOldOwnerTag(string tag)
            => tag.StartsWith(OldOwnerTagPrefix, StringComparison.OrdinalIgnoreCase);

        private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');

        private void OnItemAdded(object? sender, ItemChangeEventArgs e)
        {
            if (e.Item is not CollectionFolder folder
                || (folder.PhysicalLocations.Length > 0 && folder.PhysicalLocations.All(IsUnderBasePath)))
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    // The event fires while Jellyfin is still registering the library, before it has the ID the
                    // access sync reads, so wait for that to finish first.
                    // ponytail: fixed delay; if a slow server needs longer, the next sync (startup, daily refresh,
                    // any user change) still adds the library.
                    await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                    _logger.LogInformation("Library {LibraryName} was added; updating library access", folder.Name);
                    await EnsureAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to update library access after library {LibraryName} was added", folder.Name);
                }
            });
        }

        private async Task<int> EnsureLibrariesAsync(List<Jellyfin.Database.Implementations.Entities.User> users)
        {
            var userIds = users.Select(u => u.Id).ToHashSet();
            var byPath = new Dictionary<string, VirtualFolderInfo>(StringComparer.OrdinalIgnoreCase);

            // Keep only libraries holding exactly one current user's folder. Anything else under the plugin's
            // folders (the 0.8.0 pre-release's shared libraries, a deleted user's library, a duplicate) goes.
            foreach (var library in GetOwnedLibraries())
            {
                var path = library.Locations.Length == 1 ? Normalize(library.Locations[0]) : null;
                if (path is not null && GetOwner(path) is Guid owner && userIds.Contains(owner) && byPath.TryAdd(path, library))
                {
                    continue;
                }

                _logger.LogInformation("Removing recommendation library {LibraryName}", library.Name);
                await _libraryManager.RemoveVirtualFolder(library.Name, false).ConfigureAwait(false);
            }

            var created = 0;
            foreach (var user in users)
            {
                if (!_virtualLibraryManager.EnsureUserDirectoriesExist(user.Id, user.Username))
                {
                    continue;
                }

                foreach (var (mediaType, collectionType) in new[] { (MediaType.Movie, CollectionTypeOptions.movies), (MediaType.Series, CollectionTypeOptions.tvshows) })
                {
                    var path = _virtualLibraryManager.GetUserLibraryPath(user.Id, mediaType);
                    if (byPath.TryGetValue(Normalize(path), out var existing))
                    {
                        // Also adopts libraries an admin created by hand for 0.7 and earlier.
                        if (existing.LibraryOptions is not null
                            && UseLocalMetadataOnly(existing.LibraryOptions)
                            && Guid.TryParse(existing.ItemId, out var itemId)
                            && _libraryManager.GetItemById(itemId) is CollectionFolder folder)
                        {
                            folder.UpdateLibraryOptions(existing.LibraryOptions);
                            _logger.LogInformation("Switched {LibraryName} to local metadata only", existing.Name);
                        }

                        continue;
                    }

                    var options = new LibraryOptions { PathInfos = new[] { new MediaPathInfo(path) } };
                    UseLocalMetadataOnly(options);
                    var name = GetLibraryName(mediaType, user.Username);
                    await _libraryManager.AddVirtualFolder(name, collectionType, options, false).ConfigureAwait(false);
                    _logger.LogInformation("Created library {LibraryName}", name);
                    created++;
                }
            }

            return created;
        }

        private async Task EnsureAccessAsync(List<Jellyfin.Database.Implementations.Entities.User> users)
        {
            // Never create or expose libraries without access control in place.
            var plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin is not initialized");
            var config = plugin.Configuration;

            var libraryOwners = new Dictionary<Guid, Guid>();
            var serverLibraries = new List<Guid>();
            foreach (var folder in _libraryManager.GetVirtualFolders())
            {
                if (!Guid.TryParse(folder.ItemId, out var libraryId))
                {
                    continue;
                }

                if (folder.Locations.Length == 0 || !folder.Locations.All(IsUnderBasePath))
                {
                    serverLibraries.Add(libraryId);
                }
                else if (folder.Locations.Length == 1 && GetOwner(Normalize(folder.Locations[0])) is Guid owner)
                {
                    libraryOwners[libraryId] = owner;
                }
            }

            var newServerLibraries = serverLibraries.Except(config.KnownLibraryIds ?? Array.Empty<Guid>()).ToList();
            var allLibrariesUsers = new List<Guid>();
            foreach (var user in users)
            {
                var policy = _userManager.GetUserDto(user).Policy;
                if (policy is null)
                {
                    continue;
                }

                var own = libraryOwners.Where(p => p.Value == user.Id).Select(p => p.Key).ToList();
                var others = libraryOwners.Where(p => p.Value != user.Id).Select(p => p.Key).ToList();
                var remembered = (config.AllLibrariesUserIds ?? Array.Empty<Guid>()).Contains(user.Id);

                var changed = ApplyLibraryAccess(policy, remembered, serverLibraries, newServerLibraries, own, others, out var allLibraries);
                changed |= RemoveOldOwnerTags(policy);
                if (allLibraries)
                {
                    allLibrariesUsers.Add(user.Id);
                }

                if (changed)
                {
                    await _userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
                    _logger.LogInformation("Updated library access for user {Username}", user.Username);
                }
            }

            config.AllLibrariesUserIds = allLibrariesUsers.ToArray();
            config.KnownLibraryIds = serverLibraries.ToArray();
            plugin.SaveConfiguration();
        }

        /// <summary>
        /// Libraries whose every folder lives under the plugin's virtual library directory.
        /// </summary>
        private List<VirtualFolderInfo> GetOwnedLibraries()
            => _libraryManager.GetVirtualFolders()
                .Where(f => f.Locations.Length > 0 && f.Locations.All(IsUnderBasePath))
                .ToList();

        private bool IsUnderBasePath(string path)
            => Normalize(path).StartsWith(_basePath, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Gets the user a folder under the plugin's directory belongs to (&lt;base&gt;/&lt;userId&gt;/...).
        /// </summary>
        private Guid? GetOwner(string normalizedPath)
            => normalizedPath.StartsWith(_basePath, StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(normalizedPath.Substring(_basePath.Length).Split('/')[0], out var owner)
                ? owner
                : null;
    }
}
