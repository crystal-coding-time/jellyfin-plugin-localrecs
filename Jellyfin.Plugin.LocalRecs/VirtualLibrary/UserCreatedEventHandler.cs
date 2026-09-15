using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller.Events;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LocalRecs.VirtualLibrary
{
    /// <summary>
    /// Handles user creation events: creates the new user's recommendation libraries and switches them from
    /// "access all libraries" (the default for new users) to a list without other users' recommendations.
    /// </summary>
    public class UserCreatedEventHandler : IEventConsumer<UserCreatedEventArgs>
    {
        private readonly ILogger<UserCreatedEventHandler> _logger;
        private readonly RecommendationLibraryService _libraryService;

        /// <summary>
        /// Initializes a new instance of the <see cref="UserCreatedEventHandler"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="libraryService">Recommendation library service.</param>
        /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
        public UserCreatedEventHandler(
            ILogger<UserCreatedEventHandler> logger,
            RecommendationLibraryService libraryService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        }

        /// <inheritdoc />
        public async Task OnEvent(UserCreatedEventArgs eventArgs)
        {
            var user = eventArgs.Argument;
            if (user == null)
            {
                _logger.LogWarning("Received UserCreatedEventArgs with null user");
                return;
            }

            _logger.LogInformation("User created: {Username} ({UserId}) - setting up recommendation libraries", user.Username, user.Id);

            try
            {
                await _libraryService.EnsureAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set up recommendation libraries for new user {Username}", user.Username);
            }
        }
    }
}
