using System;
using System.Linq;
using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Download
{
    public class DownloadProcessingService : IExecute<ProcessMonitoredDownloadsCommand>
    {
        private readonly IConfigService _configService;
        private readonly ICompletedDownloadService _completedDownloadService;
        private readonly IFailedDownloadService _failedDownloadService;
        private readonly ITrackedDownloadService _trackedDownloadService;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        public DownloadProcessingService(IConfigService configService,
                                         ICompletedDownloadService completedDownloadService,
                                         IFailedDownloadService failedDownloadService,
                                         ITrackedDownloadService trackedDownloadService,
                                         IEventAggregator eventAggregator,
                                         Logger logger)
        {
            _configService = configService;
            _completedDownloadService = completedDownloadService;
            _failedDownloadService = failedDownloadService;
            _trackedDownloadService = trackedDownloadService;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        private void RemoveCompletedDownloads()
        {
            var trackedDownloads = _trackedDownloadService.GetTrackedDownloads()
                                                          .Where(t => !t.DownloadItem.Removed && t.DownloadItem.CanBeRemoved && t.State == TrackedDownloadState.Imported)
                                                          .ToList();

            foreach (var trackedDownload in trackedDownloads)
            {
                _eventAggregator.PublishEvent(new DownloadCanBeRemovedEvent(trackedDownload));
            }
        }

        public void Execute(ProcessMonitoredDownloadsCommand message)
        {
            // Reset of ImportFailed -> ImportPending is now triggered only at startup by
            // AutoRetryFailedImportsOnStartupHandler. Doing it on every refresh caused
            // thrashing: items that genuinely fail (e.g. partial-import where Lidarr
            // identified more candidate albums than actually exist) would bounce back to
            // ImportPending each refresh, the importer would delete and re-copy the same
            // already-imported files, and disk I/O would loop forever.
            var enableCompletedDownloadHandling = _configService.EnableCompletedDownloadHandling;
            var trackedDownloads = _trackedDownloadService.GetTrackedDownloads()
                                                          .Where(t => t.IsTrackable)
                                                          .ToList();

            foreach (var trackedDownload in trackedDownloads)
            {
                try
                {
                    // Normalize any lingering 'Importing' state on startup so items don't get stuck indefinitely
                    if (enableCompletedDownloadHandling &&
                        trackedDownload.DownloadItem.Status == DownloadItemStatus.Completed &&
                        trackedDownload.State == TrackedDownloadState.Importing)
                    {
                        trackedDownload.State = TrackedDownloadState.ImportPending;
                        trackedDownload.Warn("Resuming import after restart.");
                    }

                    if (trackedDownload.State == TrackedDownloadState.DownloadFailedPending)
                    {
                        _failedDownloadService.ProcessFailed(trackedDownload);
                    }
                    else if (enableCompletedDownloadHandling && trackedDownload.State == TrackedDownloadState.ImportPending)
                    {
                        _completedDownloadService.Import(trackedDownload);
                    }
                }
                catch (Exception e)
                {
                    _logger.Debug(e, "Failed to process download: {0}", trackedDownload.DownloadItem.Title);

                    // Ensure forward progress: if an exception occurred during import after setting state to Importing,
                    // do not leave the item stuck in Importing indefinitely. Move it back to ImportPending so it will retry.
                    if (trackedDownload.State == TrackedDownloadState.Importing)
                    {
                        trackedDownload.State = TrackedDownloadState.ImportPending;
                        trackedDownload.Warn("Automatic import encountered an unexpected error and will be retried.");
                    }
                }
            }

            // Imported downloads are no longer trackable so process them after processing trackable downloads
            RemoveCompletedDownloads();

            _eventAggregator.PublishEvent(new DownloadsProcessedEvent());
        }
    }
}
