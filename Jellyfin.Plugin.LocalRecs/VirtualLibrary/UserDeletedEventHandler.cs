using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller.Events;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalRecs.VirtualLibrary
{
    /// <summary>
    /// Handles user deletion events: removes the user's recommendation libraries and folders.
    /// </summary>
    public class UserDeletedEventHandler : IEventConsumer<UserDeletedEventArgs>
    {
        private readonly ILogger<UserDeletedEventHandler> _logger;
        private readonly VirtualLibraryManager _virtualLibraryManager;
        private readonly RecommendationLibraryService _libraryService;

        /// <summary>
        /// Initializes a new instance of the <see cref="UserDeletedEventHandler"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="virtualLibraryManager">Virtual library manager for directory operations.</param>
        /// <param name="libraryService">Recommendation library service.</param>
        /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
        public UserDeletedEventHandler(
            ILogger<UserDeletedEventHandler> logger,
            VirtualLibraryManager virtualLibraryManager,
            RecommendationLibraryService libraryService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _virtualLibraryManager = virtualLibraryManager ?? throw new ArgumentNullException(nameof(virtualLibraryManager));
            _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        }

        /// <inheritdoc />
        public async Task OnEvent(UserDeletedEventArgs eventArgs)
        {
            var user = eventArgs.Argument;
            if (user == null)
            {
                _logger.LogWarning("Received UserDeletedEventArgs with null user");
                return;
            }

            _logger.LogInformation("User deleted: {Username} ({UserId}) - removing recommendation libraries", user.Username, user.Id);

            try
            {
                await _libraryService.EnsureAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove recommendation libraries for deleted user {Username}", user.Username);
            }

            _virtualLibraryManager.DeleteUserDirectories(user.Id, user.Username);
        }
    }
}
