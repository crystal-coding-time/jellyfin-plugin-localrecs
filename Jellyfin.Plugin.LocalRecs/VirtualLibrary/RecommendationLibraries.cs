using System;
using System.Linq;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.LocalRecs.VirtualLibrary
{
    /// <summary>
    /// Tells the plugin's own recommendation libraries apart from the server's real libraries.
    /// The recommendation engine must only look at real libraries: recommendations are copies of items
    /// that are already in the library, and once every user has a set of them they outnumber the
    /// originals several times over, so a query that includes them gets slower with every refresh.
    /// </summary>
    public static class RecommendationLibraries
    {
        /// <summary>
        /// The path fragment every recommendation folder contains
        /// (&lt;plugins&gt;/LocalRecs/virtual-libraries/&lt;userId&gt;/…).
        /// </summary>
        public const string PathMarker = "/LocalRecs/virtual-libraries/";

        /// <summary>
        /// Gets a value indicating whether a path belongs to a recommendation library.
        /// </summary>
        /// <param name="path">The path to check.</param>
        /// <returns>True when the path is inside the plugin's virtual library directory.</returns>
        public static bool IsRecommendationPath(string? path)
            => !string.IsNullOrEmpty(path)
                && path.Replace('\\', '/').Contains(PathMarker, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Gets the ids of the server's real libraries, to scope a query with
        /// <c>InternalItemsQuery.TopParentIds</c> the way Jellyfin scopes its own library queries.
        /// Returns an empty array when they can't be determined, which leaves a query unscoped
        /// (slower) rather than empty (wrong).
        /// </summary>
        /// <param name="libraryManager">The library manager.</param>
        /// <returns>The real libraries' item ids.</returns>
        public static Guid[] GetRealLibraryIds(ILibraryManager libraryManager)
        {
            ArgumentNullException.ThrowIfNull(libraryManager);

            // No libraries reported (or none resolvable) leaves queries unscoped: slower, but never empty.
            var folders = libraryManager.GetVirtualFolders();
            if (folders is null)
            {
                return Array.Empty<Guid>();
            }

            return folders
                .Where(f => f.Locations is null || !f.Locations.Any(IsRecommendationPath))
                .Select(f => Guid.TryParse(f.ItemId, out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .ToArray();
        }
    }
}
