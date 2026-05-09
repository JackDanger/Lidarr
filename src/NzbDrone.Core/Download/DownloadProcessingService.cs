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

        private void RemoveStuckFailedImports()
        {
            var stuckDownloads = _trackedDownloadService.GetTrackedDownloads()
                                                       .Where(t => t.State == TrackedDownloadState.ImportFailed &&
                                                                   t.Added.HasValue &&
                                                                   DateTime.UtcNow - t.Added.Value > TimeSpan.FromDays(7))
                                                       .ToList();

            foreach (var trackedDownload in stuckDownloads)
            {
                _logger.Info("Removing download stuck in ImportFailed state for 7+ days: {0}", trackedDownload.DownloadItem.Title);
                _trackedDownloadService.StopTracking(trackedDownload.DownloadItem.DownloadId);
            }
        }

        public void Execute(ProcessMonitoredDownloadsCommand message)
        {
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

            // Remove downloads stuck in ImportFailed for 7+ days to prevent queue bloat
            RemoveStuckFailedImports();

            _eventAggregator.PublishEvent(new DownloadsProcessedEvent());
        }
    }
}
