using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Plugin.LocalRecs.VirtualLibrary;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.LocalRecs.Tests.Unit.VirtualLibrary
{
    /// <summary>
    /// Tests for the library access rules that keep each user's recommendation libraries private.
    /// </summary>
    public class RecommendationLibraryServiceTests
    {
        private static readonly Guid Movies = Guid.NewGuid();
        private static readonly Guid Shows = Guid.NewGuid();
        private static readonly Guid Anime = Guid.NewGuid();
        private static readonly Guid AliceRecs = Guid.NewGuid();
        private static readonly Guid BobRecs = Guid.NewGuid();
        private static readonly Guid[] ServerLibraries = { Movies, Shows };
        private static readonly Guid[] None = Array.Empty<Guid>();

        [Fact]
        public void AllLibrariesUserGetsEveryServerLibraryAndOnlyTheirOwnRecommendations()
        {
            var policy = new UserPolicy { EnableAllFolders = true };

            var changed = RecommendationLibraryService.ApplyLibraryAccess(
                policy, false, ServerLibraries, ServerLibraries, new[] { AliceRecs }, new[] { BobRecs }, out var allLibraries);

            changed.Should().BeTrue();
            allLibraries.Should().BeTrue();
            policy.EnableAllFolders.Should().BeFalse();
            policy.EnabledFolders.Should().BeEquivalentTo(new[] { Movies, Shows, AliceRecs });
        }

        [Fact]
        public void ExplicitListUserKeepsTheirListAndDoesNotGetNewLibraries()
        {
            var policy = new UserPolicy { EnableAllFolders = false, EnabledFolders = new[] { Movies, BobRecs } };

            RecommendationLibraryService.ApplyLibraryAccess(
                policy, false, new[] { Movies, Shows, Anime }, new[] { Anime }, new[] { AliceRecs }, new[] { BobRecs }, out var allLibraries);

            allLibraries.Should().BeFalse();
            policy.EnabledFolders.Should().BeEquivalentTo(new[] { Movies, AliceRecs });
        }

        [Fact]
        public void RememberedAllLibrariesUserGetsNewLibrariesButNotOnesAnAdminRemoved()
        {
            // Shows was removed from this user by an admin; Anime was just added to the server.
            var policy = new UserPolicy { EnableAllFolders = false, EnabledFolders = new[] { Movies, AliceRecs } };

            RecommendationLibraryService.ApplyLibraryAccess(
                policy, true, new[] { Movies, Shows, Anime }, new[] { Anime }, new[] { AliceRecs }, new[] { BobRecs }, out var allLibraries);

            allLibraries.Should().BeTrue();
            policy.EnabledFolders.Should().BeEquivalentTo(new[] { Movies, Anime, AliceRecs });
        }

        [Fact]
        public void SecondRunChangesNothing()
        {
            var policy = new UserPolicy { EnableAllFolders = true };
            RecommendationLibraryService.ApplyLibraryAccess(
                policy, false, ServerLibraries, ServerLibraries, new[] { AliceRecs }, new[] { BobRecs }, out _);

            RecommendationLibraryService.ApplyLibraryAccess(
                policy, true, ServerLibraries, None, new[] { AliceRecs }, new[] { BobRecs }, out _).Should().BeFalse();
        }

        [Fact]
        public void OldOwnerTagsAreRemovedAndAdminTagsKept()
        {
            var policy = new UserPolicy
            {
                BlockedTags = new[] { "horror", "localrecs-0123456789abcdef0123456789abcdef" },
                AllowedTags = new[] { "kids", "localrecs-fedcba9876543210fedcba9876543210" }
            };

            RecommendationLibraryService.RemoveOldOwnerTags(policy).Should().BeTrue();

            policy.BlockedTags.Should().Equal("horror");
            policy.AllowedTags.Should().Equal("kids");
            RecommendationLibraryService.RemoveOldOwnerTags(policy).Should().BeFalse();
        }

        [Fact]
        public void LocalMetadataOnlyTurnsOffOnlineFetchersOnce()
        {
            var options = new LibraryOptions { EnableRealtimeMonitor = true };

            RecommendationLibraryService.UseLocalMetadataOnly(options).Should().BeTrue();

            options.EnableRealtimeMonitor.Should().BeFalse();
            options.MetadataSavers.Should().BeEmpty();
            options.GetTypeOptions("Movie")!.MetadataFetchers.Should().BeEmpty();
            options.GetTypeOptions("Episode")!.ImageFetchers.Should().BeEmpty();
            RecommendationLibraryService.UseLocalMetadataOnly(options).Should().BeFalse();
        }

        /// <summary>
        /// An item Jellyfin created but never refreshed shows the raw folder name and no poster, and its
        /// files are already correct, so nothing will ever mark that user changed again. Skipping the scan
        /// on "nothing changed" alone left such a user broken permanently.
        /// </summary>
        [Fact]
        public async Task LibraryWithNeverRefreshedItemsIsScannedEvenWhenNothingChanged()
        {
            var basePath = Path.Combine(Path.GetTempPath(), "localrecs-scan-" + Guid.NewGuid());
            var staleLibraryId = Guid.NewGuid();
            var freshLibraryId = Guid.NewGuid();
            var stale = new CollectionFolder { Id = staleLibraryId };
            var fresh = new CollectionFolder { Id = freshLibraryId };

            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(m => m.GetVirtualFolders()).Returns(new List<VirtualFolderInfo>
            {
                RecommendationLibrary(staleLibraryId, basePath),
                RecommendationLibrary(freshLibraryId, basePath)
            });
            libraryManager.Setup(m => m.GetItemById(staleLibraryId)).Returns(stale);
            libraryManager.Setup(m => m.GetItemById(freshLibraryId)).Returns(fresh);
            libraryManager.Setup(m => m.ValidateTopLibraryFolders(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            libraryManager
                .Setup(m => m.GetItemList(It.Is<InternalItemsQuery>(q => q.AncestorIds.Contains(staleLibraryId))))
                .Returns(new List<BaseItem> { new Movie() });
            libraryManager
                .Setup(m => m.GetItemList(It.Is<InternalItemsQuery>(q => q.AncestorIds.Contains(freshLibraryId))))
                .Returns(new List<BaseItem> { new Movie { DateLastRefreshed = DateTime.UtcNow } });

            var providerManager = new Mock<IProviderManager>();
            providerManager
                .Setup(p => p.RefreshFullItem(It.IsAny<BaseItem>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var service = new RecommendationLibraryService(
                NullLogger<RecommendationLibraryService>.Instance,
                libraryManager.Object,
                new Mock<IUserManager>().Object,
                new Mock<IFileSystem>().Object,
                providerManager.Object,
                new VirtualLibraryManager(NullLogger<VirtualLibraryManager>.Instance, libraryManager.Object, basePath),
                basePath);

            // Nobody's folders changed this run.
            await service.ScanAsync(Array.Empty<Guid>(), CancellationToken.None);

            providerManager.Verify(
                p => p.RefreshFullItem(stale, It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>()),
                Times.Once);
            providerManager.Verify(
                p => p.RefreshFullItem(fresh, It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        private static VirtualFolderInfo RecommendationLibrary(Guid libraryId, string basePath)
            => new VirtualFolderInfo
            {
                Name = "Recommended Movies (" + libraryId + ")",
                ItemId = libraryId.ToString(),
                Locations = new[] { Path.Combine(basePath, Guid.NewGuid().ToString(), "movies").Replace('\\', '/') }
            };
    }
}
