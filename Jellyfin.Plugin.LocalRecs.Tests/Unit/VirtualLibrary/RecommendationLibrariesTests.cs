using System;
using System.Collections.Generic;
using FluentAssertions;
using Jellyfin.Plugin.LocalRecs.VirtualLibrary;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.LocalRecs.Tests.Unit.VirtualLibrary
{
    /// <summary>
    /// Tests for the query scope that keeps recommendation copies out of the engine's view.
    /// A wrong scope here is silent and total: the queries return nothing, every user's profile is
    /// empty, and the plugin produces no recommendations at all while still logging success.
    /// </summary>
    public class RecommendationLibrariesTests
    {
        private const string RecommendationPath = "/config/plugins/LocalRecs/virtual-libraries/8b1f/Recommended Movies";

        private readonly Mock<ILibraryManager> _libraryManager = new Mock<ILibraryManager>();
        private readonly List<VirtualFolderInfo> _folders = new List<VirtualFolderInfo>();

        public RecommendationLibrariesTests()
        {
            _libraryManager.Setup(m => m.GetVirtualFolders()).Returns(() => _folders);
        }

        [Fact]
        public void ReturnsTheMediaFolderIdRatherThanTheLibraryItemId()
        {
            // A library's ItemId is its CollectionFolder, but an item's TopParentId is the folder its
            // file sits under. Scoping a query by the CollectionFolder id matches no rows whatsoever.
            var collectionFolderId = Guid.NewGuid();
            var mediaFolderId = Guid.NewGuid();
            AddLibrary("/media/movies", collectionFolderId, mediaFolderId);

            var ids = RecommendationLibraries.GetRealLibraryIds(_libraryManager.Object);

            ids.Should().Equal(mediaFolderId);
            ids.Should().NotContain(collectionFolderId, "the CollectionFolder id matches no items");
        }

        [Fact]
        public void ExcludesRecommendationLibraries()
        {
            var realMediaFolderId = Guid.NewGuid();
            AddLibrary("/media/movies", Guid.NewGuid(), realMediaFolderId);
            AddLibrary(RecommendationPath, Guid.NewGuid(), Guid.NewGuid());

            var ids = RecommendationLibraries.GetRealLibraryIds(_libraryManager.Object);

            ids.Should().Equal(realMediaFolderId);
        }

        [Fact]
        public void ReturnsEmptyWhenAnyRealFolderCannotBeResolved()
        {
            // Half a scope would hide a whole library's items. Unscoped is slower; empty is wrong.
            AddLibrary("/media/movies", Guid.NewGuid(), Guid.NewGuid());
            AddLibrary("/media/shows", Guid.NewGuid(), mediaFolderId: null);

            var ids = RecommendationLibraries.GetRealLibraryIds(_libraryManager.Object);

            ids.Should().BeEmpty("an unscoped query is slower, but a partial scope silently drops items");
        }

        [Fact]
        public void ReturnsEmptyWhenThereAreNoLibraries()
        {
            var ids = RecommendationLibraries.GetRealLibraryIds(_libraryManager.Object);

            ids.Should().BeEmpty();
        }

        [Theory]
        [InlineData("/config/plugins/LocalRecs/virtual-libraries/8b1f/Recommended Movies", true)]
        [InlineData("C:\\ProgramData\\Jellyfin\\plugins\\LocalRecs\\virtual-libraries\\8b1f", true)]
        [InlineData("/media/movies", false)]
        [InlineData(null, false)]
        public void IsRecommendationPathRecognisesThePluginsOwnFolders(string? path, bool expected)
        {
            RecommendationLibraries.IsRecommendationPath(path).Should().Be(expected);
        }

        private void AddLibrary(string location, Guid collectionFolderId, Guid? mediaFolderId)
        {
            _folders.Add(new VirtualFolderInfo
            {
                Name = location,
                ItemId = collectionFolderId.ToString(),
                Locations = new[] { location }
            });

            _libraryManager
                .Setup(m => m.FindByPath(location, true))
                .Returns(mediaFolderId is Guid id ? new Folder { Id = id } : null);
        }
    }
}
