using System;
using System.Collections.Generic;
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
        /// Gets the ids of the folders the server's real media lives in, to scope a query with
        /// <c>InternalItemsQuery.TopParentIds</c>.
        /// </summary>
        /// <remarks>
        /// These are the media folders themselves, not the library ids reported by
        /// <c>GetVirtualFolders</c>. An item's <c>TopParentId</c> points at the folder its file sits
        /// under (the library's path), while a library's own <c>ItemId</c> is its CollectionFolder,
        /// which matches no rows at all: scoping by it returned an empty library and the plugin
        /// produced no recommendations whatsoever.
        /// Returns an empty array when the folders can't be resolved, which leaves a query unscoped
        /// (slower) rather than empty (wrong).
        /// </remarks>
        /// <param name="libraryManager">The library manager.</param>
        /// <returns>The media folder ids backing the real libraries.</returns>
        public static Guid[] GetRealLibraryIds(ILibraryManager libraryManager)
        {
            ArgumentNullException.ThrowIfNull(libraryManager);

            // No libraries reported (or none resolvable) leaves queries unscoped: slower, but never empty.
            var folders = libraryManager.GetVirtualFolders();
            if (folders is null)
            {
                return Array.Empty<Guid>();
            }

            var ids = new List<Guid>();
            foreach (var folder in folders)
            {
                var locations = folder.Locations;
                if (locations is null || locations.Length == 0 || locations.Any(IsRecommendationPath))
                {
                    continue;
                }

                foreach (var location in locations)
                {
                    var id = libraryManager.FindByPath(location, isFolder: true)?.Id ?? Guid.Empty;
                    if (id == Guid.Empty)
                    {
                        // A scope missing one real folder would silently hide that folder's items,
                        // which is worse than not scoping at all.
                        return Array.Empty<Guid>();
                    }

                    ids.Add(id);
                }
            }

            return ids.ToArray();
        }
    }
}
