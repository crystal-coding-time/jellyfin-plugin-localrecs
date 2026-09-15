using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalRecs.VirtualLibrary
{
    /// <summary>
    /// Starts the plugin's background work. Setting up users' recommendation libraries and syncing play status
    /// can take a while on large servers, so both wait until Jellyfin reports it has started and then run in the
    /// background instead of holding up server startup.
    /// </summary>
    public class VirtualLibraryInitializer : IHostedService
    {
        private readonly ILogger<VirtualLibraryInitializer> _logger;
        private readonly PlayStatusSyncService _playStatusSyncService;
        private readonly RecommendationLibraryService _libraryService;
        private readonly IHostApplicationLifetime _lifetime;

        /// <summary>
        /// Initializes a new instance of the <see cref="VirtualLibraryInitializer"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="playStatusSyncService">Play status sync service.</param>
        /// <param name="libraryService">Recommendation library service.</param>
        /// <param name="lifetime">Application lifetime, used to wait for server startup.</param>
        /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
        public VirtualLibraryInitializer(
            ILogger<VirtualLibraryInitializer> logger,
            PlayStatusSyncService playStatusSyncService,
            RecommendationLibraryService libraryService,
            IHostApplicationLifetime lifetime)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _playStatusSyncService = playStatusSyncService ?? throw new ArgumentNullException(nameof(playStatusSyncService));
            _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
            _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _libraryService.StartWatchingLibraries();
            _lifetime.ApplicationStarted.Register(() => _ = Task.Run(RunStartupWorkAsync, CancellationToken.None));
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken)
        {
            // Dispose the play status sync service to unsubscribe from events and flush pending updates
            _playStatusSyncService.Dispose();
            _logger.LogInformation("Virtual library services stopped");
            return Task.CompletedTask;
        }

        private async Task RunStartupWorkAsync()
        {
            try
            {
                await _libraryService.EnsureAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set up recommendation libraries");
            }

            try
            {
                // Makes recommendation items reflect the real library's play status
                _logger.LogInformation("Syncing play status from source library to virtual library items...");
                _playStatusSyncService.SyncPlayStatusFromSourceLibraryForAllUsers();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background play status sync failed");
            }
        }
    }
}
