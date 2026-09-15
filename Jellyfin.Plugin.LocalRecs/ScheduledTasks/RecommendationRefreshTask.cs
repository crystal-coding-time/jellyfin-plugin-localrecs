using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LocalRecs.Configuration;
using Jellyfin.Plugin.LocalRecs.Models;
using Jellyfin.Plugin.LocalRecs.Services;
using Jellyfin.Plugin.LocalRecs.VirtualLibrary;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalRecs.ScheduledTasks
{
    /// <summary>
    /// Scheduled task for refreshing recommendations for all users.
    /// Can be triggered manually or scheduled via Jellyfin's task scheduler.
    /// </summary>
    public class RecommendationRefreshTask : IScheduledTask
    {
        private readonly ILogger<RecommendationRefreshTask> _logger;
        private readonly IUserManager _userManager;
        private readonly RecommendationRefreshService _refreshService;
        private readonly VirtualLibraryManager _virtualLibraryManager;
        private readonly RecommendationLibraryService _libraryService;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecommendationRefreshTask"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="userManager">User manager.</param>
        /// <param name="refreshService">Recommendation refresh service.</param>
        /// <param name="virtualLibraryManager">Virtual library manager.</param>
        /// <param name="libraryService">Recommendation library service.</param>
        public RecommendationRefreshTask(
            ILogger<RecommendationRefreshTask> logger,
            IUserManager userManager,
            RecommendationRefreshService refreshService,
            VirtualLibraryManager virtualLibraryManager,
            RecommendationLibraryService libraryService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
            _refreshService = refreshService ?? throw new ArgumentNullException(nameof(refreshService));
            _virtualLibraryManager = virtualLibraryManager ?? throw new ArgumentNullException(nameof(virtualLibraryManager));
            _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        }

        /// <inheritdoc />
        public string Name => "Refresh Local Recommendations";

        /// <inheritdoc />
        public string Key => "LocalRecsRefresh";

        /// <inheritdoc />
        public string Description => "Updates personalized recommendations for all users based on watch history and metadata similarity";

        /// <inheritdoc />
        public string Category => "Local Recommendations";

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting recommendation refresh task (Build: {BuildVersion})", Plugin.BuildVersion);
            var startTime = DateTime.UtcNow;

            try
            {
                var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

                // Step 1: Get all users and make sure each has their own libraries (5% progress)
                progress?.Report(0);
                cancellationToken.ThrowIfCancellationRequested();
                var users = _userManager.GetUsers().ToList();

                if (users.Count == 0)
                {
                    _logger.LogWarning("No users found, skipping recommendation refresh");
                    progress?.Report(100);
                    return;
                }

                await _libraryService.EnsureAsync(cancellationToken).ConfigureAwait(false);
                progress?.Report(5);

                // Step 2: Generate recommendations for all users (5-80% progress)
                var userIds = users.Select(u => u.Id).ToList();
                var userRecommendations = await _refreshService.GenerateRecommendationsForMultipleUsersAsync(
                    userIds,
                    config).ConfigureAwait(false);

                progress?.Report(80);

                // Step 3: Sync recommendation folders for each user (80-90% progress)
                cancellationToken.ThrowIfCancellationRequested();
                var successfulUsers = 0;
                var failedUsers = new List<string>();

                foreach (var user in users)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        if (userRecommendations.TryGetValue(user.Id, out var recs))
                        {
                            _logger.LogDebug("Syncing recommendation folders for user {UserName} ({UserId})", user.Username, user.Id);

                            _virtualLibraryManager.SyncRecommendations(
                                user.Id,
                                recs.Movies,
                                MediaType.Movie);

                            _virtualLibraryManager.SyncRecommendations(
                                user.Id,
                                recs.Tv,
                                MediaType.Series);

                            successfulUsers++;
                            _logger.LogDebug(
                                "Updated recommendation folders for {UserName}: {MovieCount} movies, {TvCount} TV shows",
                                user.Username,
                                recs.Movies.Count,
                                recs.Tv.Count);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to sync recommendation folders for user {UserName} ({UserId})", user.Username, user.Id);
                        failedUsers.Add(user.Username);
                    }
                }

                progress?.Report(90);

                // Step 4: Scan the recommendation libraries (90-95% progress)
                await _libraryService.ScanAsync(cancellationToken).ConfigureAwait(false);

                progress?.Report(95);

                // Note: Play status sync happens automatically via ItemAdded event when Jellyfin scans new items

                // Step 5: Report results (100% progress)
                var duration = DateTime.UtcNow - startTime;
                _logger.LogInformation(
                    "Recommendation refresh completed in {Duration:F2} seconds: {Success}/{Total} users successful",
                    duration.TotalSeconds,
                    successfulUsers,
                    users.Count);

                if (failedUsers.Count > 0)
                {
                    _logger.LogWarning("Failed to sync recommendations for {Count} users: {Users}", failedUsers.Count, string.Join(", ", failedUsers));
                }

                progress?.Report(100);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Recommendation refresh task was cancelled");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Recommendation refresh task failed");
                throw;
            }
        }

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // At startup, so a fresh install gets recommendations without a manual run, and daily at 4:00 AM
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = MediaBrowser.Model.Tasks.TaskTriggerInfoType.StartupTrigger
                },
                new TaskTriggerInfo
                {
                    Type = MediaBrowser.Model.Tasks.TaskTriggerInfoType.DailyTrigger,
                    TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
                }
            };
        }
    }
}
