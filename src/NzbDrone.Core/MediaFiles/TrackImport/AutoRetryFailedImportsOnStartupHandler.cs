using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.TrackImport.Manual;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.MediaFiles.TrackImport
{
    public class AutoRetryFailedImportsOnStartupHandler : IHandle<ApplicationStartedEvent>, IHandle<TrackedDownloadRefreshedEvent>
    {
        private readonly Logger _logger;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly ICommandRepository _commandRepository;
        private readonly ITrackedDownloadService _trackedDownloadService;

        // The reset has to fire after the TrackedDownload cache has been populated
        // from the download client, but before any per-refresh `Execute()` pass walks
        // it. ApplicationStartedEvent is too early — the cache is still empty —
        // and TrackedDownloadRefreshedEvent fires immediately after Refresh()
        // populates it (and just before Refresh() pushes ProcessMonitoredDownloads).
        // We listen for the latter, run the reset once, then never again. Per-refresh
        // resetting was option A in the design — discarded because the items that
        // currently land in ImportFailed aren't ones repeated immediate retry can
        // help (artist mismatch, score-too-low, etc.); they need external state to
        // change before they could succeed. Restart is the natural trigger for
        // "external state changed" in our workflow (lidarr-deploy restarts every
        // time we change matching code).
        private int _resetFired;

        public AutoRetryFailedImportsOnStartupHandler(Logger logger,
                                                     IManageCommandQueue commandQueueManager,
                                                     ICommandRepository commandRepository,
                                                     ITrackedDownloadService trackedDownloadService)
        {
            _logger = logger;
            _commandQueueManager = commandQueueManager;
            _commandRepository = commandRepository;
            _trackedDownloadService = trackedDownloadService;
        }

        public void Handle(ApplicationStartedEvent message)
        {
            _logger.Info("Initializing improved import matching system on startup.");

            // The reset deliberately doesn't run here — TrackedDownloadService's
            // cache is empty until the first Refresh populates it from the
            // download client. We schedule that refresh below, then catch
            // TrackedDownloadRefreshedEvent (handler below) to do the reset
            // exactly once with a populated cache.

            // Find all pending manual import commands that need to be retried
            var pendingManualImports = GetPendingManualImports();

            if (pendingManualImports.Any())
            {
                _logger.Info(
                    "Found {0} pending manual imports. These will be reprocessed with improved matching: " +
                    "pre-normalization, fuzzy matching, multi-disc detection, classical music handling, " +
                    "and concurrent metadata lookups.",
                    pendingManualImports.Count);

                // Re-queue each pending manual import with high priority so they execute with new logic
                foreach (var manualImportCmd in pendingManualImports)
                {
                    try
                    {
                        // Push back to queue with high priority to execute early with improved logic
                        _commandQueueManager.Push<ManualImportCommand>(manualImportCmd, CommandPriority.High);
                        _logger.Debug("Re-queued manual import for reprocessing with improved logic");
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Failed to re-queue manual import command");
                    }
                }
            }

            // Trigger root folder scan to catch any files that haven't been processed yet
            _logger.Info("Triggering root folder scan to identify any unmatched files with improved logic.");
            _commandQueueManager.Push(new RescanFoldersCommand(), CommandPriority.High);

            // Also trigger refresh of monitored downloads to re-import failed items with improved logic
            _logger.Info("Triggering refresh of monitored downloads to retry failed imports with improved matching.");
            _commandQueueManager.Push(new RefreshMonitoredDownloadsCommand(), CommandPriority.High);
        }

        public void Handle(TrackedDownloadRefreshedEvent message)
        {
            // Fire the reset exactly once, on the first refresh after process start.
            // Subsequent refreshes (which happen every ~8 minutes via the scheduled
            // RefreshMonitoredDownloads command, plus on any user-triggered refresh)
            // are no-ops here — items in ImportFailed stay there until the next
            // Lidarr restart, which is when our matching code can have changed.
            if (Interlocked.Exchange(ref _resetFired, 1) != 0)
            {
                return;
            }

            ResetFailedImportsForRetry();
        }

        private void ResetFailedImportsForRetry()
        {
            try
            {
                var failed = _trackedDownloadService.GetTrackedDownloads()
                                                   .Where(t => t.State == TrackedDownloadState.ImportFailed)
                                                   .ToList();

                if (failed.Count == 0)
                {
                    return;
                }

                _logger.Info("Re-queueing {0} previously-failed import(s) for retry with current matching logic.", failed.Count);

                foreach (var trackedDownload in failed)
                {
                    // Don't call trackedDownload.Warn(): that surfaces in the UI as a per-item
                    // rejection message. The transition is internal.
                    trackedDownload.State = TrackedDownloadState.ImportPending;
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to reset previously-failed imports on startup");
            }
        }

        private List<ManualImportCommand> GetPendingManualImports()
        {
            try
            {
                // Get all queued commands from database
                var queuedCommands = _commandRepository.Queued();

                var manualImports = new List<ManualImportCommand>();

                // Filter to manual import commands
                foreach (var cmd in queuedCommands.Where(c => c.Name == "ManualImport"))
                {
                    var manualImportCmd = cmd.Body as ManualImportCommand;
                    if (manualImportCmd != null)
                    {
                        manualImports.Add(manualImportCmd);
                    }
                }

                return manualImports;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error retrieving pending manual imports");
                return new List<ManualImportCommand>();
            }
        }
    }
}
