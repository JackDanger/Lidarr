using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaFiles.TrackImport.Manual;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.MediaFiles.TrackImport
{
    public class AutoRetryFailedImportsOnStartupHandler : IHandle<ApplicationStartedEvent>
    {
        private readonly Logger _logger;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly ICommandRepository _commandRepository;

        public AutoRetryFailedImportsOnStartupHandler(Logger logger, IManageCommandQueue commandQueueManager, ICommandRepository commandRepository)
        {
            _logger = logger;
            _commandQueueManager = commandQueueManager;
            _commandRepository = commandRepository;
        }

        public void Handle(ApplicationStartedEvent message)
        {
            _logger.Info("Initializing improved import matching system on startup.");

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

            // Also trigger disk scan to catch any files that haven't been processed yet
            _logger.Info("Triggering root folder scan to identify any unmatched files with improved logic.");
            _commandQueueManager.Push(new RescanFoldersCommand(), CommandPriority.High);
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
