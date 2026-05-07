using NLog;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.LifeCycle;

namespace NzbDrone.Core.MediaFiles.TrackImport
{
    public class AutoRetryFailedImportsOnStartupInitiator : IHandle<ApplicationStartedEvent>
    {
        private readonly ICommandExecutor _commandExecutor;
        private readonly Logger _logger;

        public AutoRetryFailedImportsOnStartupInitiator(ICommandExecutor commandExecutor, Logger logger)
        {
            _commandExecutor = commandExecutor;
            _logger = logger;
        }

        public void Handle(ApplicationStartedEvent message)
        {
            _logger.ProgressInfo("Triggering auto-retry of failed imports on application start");
            _commandExecutor.PublishCommand(new AutoRetryFailedImportsOnStartupCommand());
        }
    }
}
