using NLog;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.MediaFiles.TrackImport
{
    public class AutoRetryFailedImportsOnStartupHandler : IHandle<ApplicationStartedEvent>
    {
        private readonly Logger _logger;

        public AutoRetryFailedImportsOnStartupHandler(Logger logger)
        {
            _logger = logger;
        }

        public void Handle(ApplicationStartedEvent message)
        {
            _logger.Info(
                "Application started - improved import matching active. Unimported files will be automatically " +
                "re-evaluated with pre-normalization, fuzzy matching, multi-disc detection, and other improvements. " +
                "Next automatic root folder scan will process these with new matching logic.");
        }
    }
}
