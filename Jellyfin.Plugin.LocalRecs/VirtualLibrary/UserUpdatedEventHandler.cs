using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Events.Users;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Events;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalRecs.VirtualLibrary
{
    /// <summary>
    /// Handles user updates: when an admin turns "access all libraries" back on for a user, that user would see
    /// every other user's recommendation libraries, so their access is switched back to a managed list at once.
    /// </summary>
    public class UserUpdatedEventHandler : IEventConsumer<UserUpdatedEventArgs>
    {
        private readonly ILogger<UserUpdatedEventHandler> _logger;
        private readonly RecommendationLibraryService _libraryService;

        /// <summary>
        /// Initializes a new instance of the <see cref="UserUpdatedEventHandler"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="libraryService">Recommendation library service.</param>
        /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
        public UserUpdatedEventHandler(
            ILogger<UserUpdatedEventHandler> logger,
            RecommendationLibraryService libraryService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        }

        /// <inheritdoc />
        public Task OnEvent(UserUpdatedEventArgs eventArgs)
        {
            // The plugin's own policy updates always turn "access all libraries" off, so they don't loop back here.
            var user = eventArgs.Argument;
            if (user == null || !user.HasPermission(PermissionKind.EnableAllFolders))
            {
                return Task.CompletedTask;
            }

            // In the background, so the admin's save isn't held up.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _libraryService.EnsureAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to update library access for user {Username}", user.Username);
                }
            });

            return Task.CompletedTask;
        }
    }
}
