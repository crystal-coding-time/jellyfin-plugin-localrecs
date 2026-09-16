using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LocalRecs.Configuration;
using Jellyfin.Plugin.LocalRecs.Models;
using Jellyfin.Plugin.LocalRecs.Utilities;
using Jellyfin.Plugin.LocalRecs.VirtualLibrary;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

using LocalMediaType = Jellyfin.Plugin.LocalRecs.Models.MediaType;

namespace Jellyfin.Plugin.LocalRecs.Services
{
    /// <summary>
    /// Service for generating personalized recommendations.
    /// Scores candidates using cosine similarity between user taste vectors and item embeddings.
    /// </summary>
    public class RecommendationEngine
    {
        private readonly IUserDataManager _userDataManager;
        private readonly IUserManager _userManager;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<RecommendationEngine> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecommendationEngine"/> class.
        /// </summary>
        /// <param name="userDataManager">The user data manager.</param>
        /// <param name="userManager">The user manager.</param>
        /// <param name="libraryManager">The library manager.</param>
        /// <param name="logger">The logger.</param>
        public RecommendationEngine(
            IUserDataManager userDataManager,
            IUserManager userManager,
            ILibraryManager libraryManager,
            ILogger<RecommendationEngine> logger)
        {
            _userDataManager = userDataManager ?? throw new ArgumentNullException(nameof(userDataManager));
            _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Generates recommendations for a user.
        /// </summary>
        /// <param name="userId">The user identifier.</param>
        /// <param name="userProfile">The user's taste profile.</param>
        /// <param name="embeddings">Dictionary of item embeddings.</param>
        /// <param name="metadata">Dictionary of item metadata.</param>
        /// <param name="config">Plugin configuration.</param>
        /// <param name="mediaType">Filter to specific media type (null = all types).</param>
        /// <param name="maxResults">Maximum number of recommendations to return.</param>
        /// <returns>List of scored recommendations, ordered by score descending.</returns>
        /// <exception cref="ArgumentNullException">Thrown when required parameters are null.</exception>
        /// <exception cref="ArgumentException">Thrown when embeddings or metadata are empty.</exception>
        public List<ScoredRecommendation> GenerateRecommendations(
            Guid userId,
            UserProfile? userProfile,
            IReadOnlyDictionary<Guid, ItemEmbedding> embeddings,
            IReadOnlyDictionary<Guid, MediaItemMetadata> metadata,
            PluginConfiguration config,
            LocalMediaType? mediaType = null,
            int maxResults = 25)
        {
            if (embeddings == null)
            {
                throw new ArgumentNullException(nameof(embeddings));
            }

            if (metadata == null)
            {
                throw new ArgumentNullException(nameof(metadata));
            }

            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            if (embeddings.Count == 0)
            {
                throw new ArgumentException("Embeddings dictionary cannot be empty", nameof(embeddings));
            }

            if (metadata.Count == 0)
            {
                throw new ArgumentException("Metadata dictionary cannot be empty", nameof(metadata));
            }

            _logger.LogDebug(
                "Generating recommendations for user {UserId}, mediaType: {MediaType}, max: {MaxResults}",
                userId,
                mediaType?.ToString() ?? "All",
                maxResults);

            // Check for cold-start scenario
            if (userProfile == null || userProfile.WatchedItemCount < config.MinWatchedItemsForPersonalization)
            {
                _logger.LogDebug(
                    "Cold-start scenario for user {UserId}: {WatchedCount} watched items (min: {MinRequired})",
                    userId,
                    userProfile?.WatchedItemCount ?? 0,
                    config.MinWatchedItemsForPersonalization);

                return GenerateColdStartRecommendations(userId, metadata, mediaType, maxResults);
            }

            // Get unwatched candidates
            var candidates = GetUnwatchedCandidates(userId, embeddings.Keys, metadata, mediaType, config);

            if (candidates.Count == 0)
            {
                _logger.LogWarning("No unwatched candidates found for user {UserId}", userId);
                return new List<ScoredRecommendation>();
            }

            _logger.LogDebug(
                "Found {CandidateCount} unwatched candidates for user {UserId}",
                candidates.Count,
                userId);

            // Score all candidates
            var scoredCandidates = new List<ScoredRecommendation>();

            foreach (var candidateId in candidates)
            {
                if (!embeddings.TryGetValue(candidateId, out var embedding))
                {
                    continue; // Skip if no embedding
                }

                if (!metadata.TryGetValue(candidateId, out var itemMetadata))
                {
                    continue; // Skip if no metadata
                }

                var score = ScoreCandidate(userProfile, embedding, itemMetadata, config);

                scoredCandidates.Add(score);
            }

            // Sort by score descending and take top N
            var recommendations = scoredCandidates
                .OrderByDescending(r => r.Score)
                .Take(maxResults)
                .ToList();

            _logger.LogDebug(
                "Generated {RecommendationCount} recommendations for user {UserId}",
                recommendations.Count,
                userId);

            return recommendations;
        }

        /// <summary>
        /// Gets the set of item IDs that a user has access to based on their library permissions.
        /// Uses Jellyfin's built-in user-scoped query which respects library access settings.
        /// </summary>
        /// <param name="user">The Jellyfin user.</param>
        /// <returns>HashSet of accessible item IDs.</returns>
        private HashSet<Guid> GetUserAccessibleItemIds(Jellyfin.Database.Implementations.Entities.User user)
        {
            // Ids only, from the real libraries: this runs once per user, and hydrating every item
            // (with its images and user data) just to read the ids was the bulk of a refresh.
            var accessibleItems = _libraryManager.GetItemIds(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                IsVirtualItem = false,
                Recursive = true,
                TopParentIds = RecommendationLibraries.GetRealLibraryIds(_libraryManager),
                DtoOptions = new DtoOptions(false) { EnableImages = false, EnableUserData = false }
            });

            if (accessibleItems == null)
            {
                return new HashSet<Guid>();
            }

            return accessibleItems.ToHashSet();
        }

        /// <summary>
        /// Gets unwatched candidate items for a user.
        /// Excludes fully watched items, optionally partially watched series,
        /// and items from libraries the user cannot access.
        /// </summary>
        /// <param name="userId">The user identifier.</param>
        /// <param name="availableItemIds">Available item IDs from embeddings.</param>
        /// <param name="metadata">Item metadata dictionary.</param>
        /// <param name="mediaType">Filter to specific media type.</param>
        /// <param name="config">Plugin configuration.</param>
        /// <returns>List of unwatched item IDs.</returns>
        private List<Guid> GetUnwatchedCandidates(
            Guid userId,
            IEnumerable<Guid> availableItemIds,
            IReadOnlyDictionary<Guid, MediaItemMetadata> metadata,
            LocalMediaType? mediaType,
            PluginConfiguration config)
        {
            var user = _userManager.GetUserById(userId);
            if (user == null)
            {
                _logger.LogWarning("User not found: {UserId}", userId);
                return new List<Guid>();
            }

            // Get items accessible to this user based on library permissions
            var accessibleItemIds = GetUserAccessibleItemIds(user);
            _logger.LogDebug(
                "User {UserId} has access to {Count} items",
                userId,
                accessibleItemIds.Count);

            var candidates = new List<Guid>();

            foreach (var itemId in availableItemIds)
            {
                // Get item metadata for type filtering
                if (!metadata.TryGetValue(itemId, out var itemMetadata))
                {
                    continue;
                }

                // Filter by media type if specified
                if (mediaType.HasValue && itemMetadata.Type != mediaType.Value)
                {
                    continue;
                }

                // Exclude items from libraries the user cannot access
                if (!accessibleItemIds.Contains(itemId))
                {
                    continue;
                }

                // Exclude items with insufficient metadata (no genres AND no actors)
                // These produce unreliable similarity scores
                if (itemMetadata.Genres.Count == 0 && itemMetadata.Actors.Count == 0)
                {
                    continue;
                }

                var item = _libraryManager.GetItemById(itemId);
                if (item == null)
                {
                    _logger.LogDebug(
                        "Item not found in library: {ItemId} ({Name})",
                        itemId,
                        itemMetadata.Name);
                    continue;
                }

                // Log if item path suggests it's from virtual library (shouldn't happen but check)
                if (itemMetadata.Type == LocalMediaType.Series && item.Path != null && item.Path.Contains("virtual-libraries"))
                {
                    _logger.LogWarning(
                        "Virtual library item in candidates: {Name} (ItemId={ItemId}, Path={Path})",
                        itemMetadata.Name,
                        itemId,
                        item.Path);
                }

                var userData = _userDataManager.GetUserData(user, item);

                // Exclude fully watched items
                // For series, userData.Played is not reliable - we need to check episode watch status
                if (itemMetadata.Type == LocalMediaType.Series && item is Series series)
                {
                    // Exclude series with any watched episodes (both in-progress and fully watched)
                    if (HasAnyWatchedEpisodes(series, user))
                    {
                        _logger.LogDebug(
                            "Excluding series with watch history: {Name}",
                            itemMetadata.Name);
                        continue;
                    }
                }
                else if (userData != null && userData.Played)
                {
                    _logger.LogDebug(
                        "Excluding watched item: {Name} (Played={Played})",
                        itemMetadata.Name,
                        userData.Played);
                    continue;
                }

                // Exclude items with any playback progress (user is currently watching or has started)
                // These items will be removed from virtual library by PlayStatusSyncService
                // and should not be re-added to recommendations until fully unwatched
                if (userData != null && userData.PlaybackPositionTicks > 0)
                {
                    continue;
                }

                candidates.Add(itemId);
            }

            return candidates;
        }

        /// <summary>
        /// Checks if a series has any watched episodes.
        /// Series with any watch history (in-progress or fully watched) should be excluded
        /// from recommendations since the user has already engaged with them.
        /// </summary>
        /// <param name="series">The series to check.</param>
        /// <param name="user">The user to check watch status for.</param>
        /// <returns>True if the series has at least one watched episode.</returns>
        private bool HasAnyWatchedEpisodes(Series series, Jellyfin.Database.Implementations.Entities.User user)
        {
            // Query for any watched episodes in this series
            var watchedEpisodes = _libraryManager.GetItemIds(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                AncestorIds = new[] { series.Id },
                IsPlayed = true,
                Limit = 1, // We only need to know if any exist
                Recursive = true,
                DtoOptions = new DtoOptions(false) { EnableImages = false, EnableUserData = false }
            });

            return watchedEpisodes.Count > 0;
        }

        /// <summary>
        /// Scores a candidate item against the user's taste profile using cosine similarity
        /// and optionally rating proximity.
        /// </summary>
        /// <param name="userProfile">The user's taste profile.</param>
        /// <param name="candidateEmbedding">The candidate item's embedding.</param>
        /// <param name="itemMetadata">The candidate item's metadata.</param>
        /// <param name="config">Plugin configuration.</param>
        /// <returns>Scored recommendation.</returns>
        private ScoredRecommendation ScoreCandidate(
            UserProfile userProfile,
            ItemEmbedding candidateEmbedding,
            MediaItemMetadata itemMetadata,
            PluginConfiguration config)
        {
            // Compute cosine similarity between user taste vector and item embedding
            var cosineSimilarity = VectorMath.CosineSimilarity(
                userProfile.TasteVector,
                candidateEmbedding.Vector);

            // If rating proximity is disabled, return pure cosine similarity
            if (!config.EnableRatingProximity)
            {
                return new ScoredRecommendation(candidateEmbedding.ItemId, cosineSimilarity);
            }

            // Compute rating proximity components
            double communityProximity = 0.5; // neutral default
            double criticProximity = 0.5;    // neutral default

            // Community rating proximity (if both user and item have community ratings)
            if (itemMetadata.CommunityRating.HasValue && userProfile.AverageCommunityRating.HasValue)
            {
                var diff = Math.Abs(itemMetadata.CommunityRating.Value - userProfile.AverageCommunityRating.Value);

                // Community rating is 0-10 scale
                communityProximity = Math.Max(0, 1.0 - (diff / 10.0));
            }

            // Critic rating proximity (if both user and item have critic ratings)
            if (itemMetadata.CriticRating.HasValue && userProfile.AverageCriticRating.HasValue)
            {
                var diff = Math.Abs(itemMetadata.CriticRating.Value - userProfile.AverageCriticRating.Value);

                // Critic rating is 0-100 scale
                criticProximity = Math.Max(0, 1.0 - (diff / 100.0));
            }

            // Average the two rating proximities
            var ratingProximity = (communityProximity + criticProximity) / 2.0;

            // Blend cosine similarity with rating proximity
            var finalScore = ((1 - config.RatingProximityWeight) * cosineSimilarity)
                           + (config.RatingProximityWeight * ratingProximity);

            return new ScoredRecommendation(candidateEmbedding.ItemId, (float)finalScore);
        }

        /// <summary>
        /// Generates recommendations for users with insufficient watch history (cold-start).
        /// Returns top-rated items from the library.
        /// </summary>
        /// <param name="userId">The user identifier.</param>
        /// <param name="metadata">Item metadata dictionary.</param>
        /// <param name="mediaType">Filter to specific media type.</param>
        /// <param name="maxResults">Maximum number of recommendations.</param>
        /// <returns>List of top-rated items.</returns>
        private List<ScoredRecommendation> GenerateColdStartRecommendations(
            Guid userId,
            IReadOnlyDictionary<Guid, MediaItemMetadata> metadata,
            LocalMediaType? mediaType,
            int maxResults)
        {
            _logger.LogDebug(
                "Generating cold-start recommendations for user {UserId}",
                userId);

            // Filter to media type if specified
            var candidateMetadata = metadata.Values.AsEnumerable();

            if (mediaType.HasValue)
            {
                candidateMetadata = candidateMetadata.Where(m => m.Type == mediaType.Value);
            }

            // Get unwatched items that the user has access to
            var user = _userManager.GetUserById(userId);
            var unwatchedCandidates = new List<MediaItemMetadata>();

            if (user != null)
            {
                // Filter to items from libraries the user can access
                var accessibleItemIds = GetUserAccessibleItemIds(user);

                foreach (var item in candidateMetadata)
                {
                    // Exclude items from inaccessible libraries
                    if (!accessibleItemIds.Contains(item.Id))
                    {
                        continue;
                    }

                    var libraryItem = _libraryManager.GetItemById(item.Id);
                    if (libraryItem == null)
                    {
                        continue;
                    }

                    var userData = _userDataManager.GetUserData(user, libraryItem);
                    if (userData != null && userData.Played)
                    {
                        continue; // Skip watched items
                    }

                    unwatchedCandidates.Add(item);
                }
            }
            else
            {
                unwatchedCandidates = candidateMetadata.ToList();
            }

            // Sort by community rating (primary) and critic rating (secondary)
            // Normalize scores to [0-1] range to match personalized recommendation scores
            var topRated = unwatchedCandidates
                .OrderByDescending(m => m.CommunityRating ?? 0)
                .ThenByDescending(m => m.CriticRating ?? 0)
                .Take(maxResults)
                .Select(m => new ScoredRecommendation(m.Id, (m.CommunityRating ?? 0) / 10.0f))
                .ToList();

            _logger.LogDebug(
                "Generated {Count} cold-start recommendations for user {UserId}",
                topRated.Count,
                userId);

            return topRated;
        }
    }
}
