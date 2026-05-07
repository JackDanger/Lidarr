using NLog;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.MediaFiles.TrackImport
{
    public class AutoRetryFailedImportsOnStartupHandler : IHandle<ApplicationStartedEvent>
    {
        private readonly Logger _logger;
        private readonly IManageCommandQueue _commandQueueManager;

        public AutoRetryFailedImportsOnStartupHandler(Logger logger, IManageCommandQueue commandQueueManager)
        {
            _logger = logger;
            _commandQueueManager = commandQueueManager;
        }

        public void Handle(ApplicationStartedEvent message)
        {
            _logger.Info(
                "Application started - improved import matching active. Unimported files will be automatically " +
                "re-evaluated with pre-normalization, fuzzy matching, multi-disc detection, and other improvements. " +
                "Triggering immediate root folder scan to process pending imports.");

            _commandQueueManager.Push(new RescanFoldersCommand(), CommandTrigger.Manual);
        }
    }
}
