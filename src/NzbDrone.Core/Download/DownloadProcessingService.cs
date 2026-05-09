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

        private void ResetFailedImportsForRetry()
        {
            // Move ImportFailed -> ImportPending so failures get a second pass under the
            // current matching code. This used to cause file-level thrashing (delete +
            // re-copy of identical content every refresh) — that's now prevented by the
            // idempotency guard in UpgradeMediaFileService.UpgradeTrackFile, so the
            // reset is safe to run every pass.
            //
            // The startup handler also kicks a reset, but it can fire before the
            // TrackedDownload cache is populated from the download client; this in-loop
            // call is the reliable trigger.
            var failed = _trackedDownloadService.GetTrackedDownloads()
                                               .Where(t => t.State == TrackedDownloadState.ImportFailed)
                                               .ToList();

            if (failed.Count == 0)
            {
                return;
            }

            // Single summary line — don't call trackedDownload.Warn(), which would
            // surface this state transition in the queue UI as if it were a rejection.
            _logger.Info("Re-queueing {0} previously-failed import(s) for retry with current matching logic.", failed.Count);

            foreach (var trackedDownload in failed)
            {
                trackedDownload.State = TrackedDownloadState.ImportPending;
            }
        }

        public void Execute(ProcessMonitoredDownloadsCommand message)
        {
            // Reset failed imports first so they're re-evaluated in this same pass.
            // Idempotent at the file level thanks to UpgradeTrackFile's same-content
            // short-circuit — items that genuinely have nothing new to import become
            // a CPU-only no-op rather than a disk-thrash loop.
            ResetFailedImportsForRetry();

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
