using System;
using FluentAssertions;
using Jellyfin.Plugin.LocalRecs.VirtualLibrary;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Users;
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
    }
}
