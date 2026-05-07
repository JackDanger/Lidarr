using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles.TrackImport.Aggregation;
using NzbDrone.Core.MediaFiles.TrackImport.Identification;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.MediaFiles.TrackImport
{
    public class AutoRetryFailedImportsOnStartupHandler : IHandle<ApplicationStartedEvent>
    {
        private readonly IRootFolderService _rootFolderService;
        private readonly IDiskProvider _diskProvider;
        private readonly IAugmentingService _augmentingService;
        private readonly IIdentificationService _identificationService;
        private readonly IImportApprovedTracks _importApprovedTracks;
        private readonly Logger _logger;

        public AutoRetryFailedImportsOnStartupHandler(IRootFolderService rootFolderService,
                                                       IDiskProvider diskProvider,
                                                       IAugmentingService augmentingService,
                                                       IIdentificationService identificationService,
                                                       IImportApprovedTracks importApprovedTracks,
                                                       Logger logger)
        {
            _rootFolderService = rootFolderService;
            _diskProvider = diskProvider;
            _augmentingService = augmentingService;
            _identificationService = identificationService;
            _importApprovedTracks = importApprovedTracks;
            _logger = logger;
        }

        public void Handle(ApplicationStartedEvent message)
        {
            _logger.ProgressInfo("Starting auto-retry of failed imports");

            var rootFolders = _rootFolderService.All().ToList();

            if (!rootFolders.Any())
            {
                _logger.Info("No root folders configured, skipping auto-retry");
                return;
            }

            var totalRetried = 0;
            var totalImported = 0;

            foreach (var rootFolder in rootFolders)
            {
                try
                {
                    _logger.Debug("Auto-retrying imports for root folder: {0}", rootFolder.Path);

                    var mediaFiles = _diskProvider.GetFiles(rootFolder.Path, recursive: true)
                        .Where(f => MediaFileExtensions.MediaFileExtensions.Extensions.Contains(System.IO.Path.GetExtension(f)))
                        .ToList();

                    if (!mediaFiles.Any())
                    {
                        _logger.Debug("No media files found in {0}", rootFolder.Path);
                        continue;
                    }

                    _logger.Debug("Found {0} media files to retry in {1}", mediaFiles.Count, rootFolder.Path);

                    var localTracks = new List<LocalTrack>();
                    foreach (var filePath in mediaFiles)
                    {
                        try
                        {
                            var fileInfo = _diskProvider.GetFileInfo(filePath);
                            var localTrack = new LocalTrack
                            {
                                Path = filePath,
                                Size = fileInfo.Length,
                                Modified = fileInfo.LastWriteTimeUtc
                            };

                            _augmentingService.Augment(localTrack, true);
                            localTracks.Add(localTrack);
                        }
                        catch (Exception e)
                        {
                            _logger.Debug(e, "Error augmenting file {0}", filePath);
                        }
                    }

                    if (!localTracks.Any())
                    {
                        _logger.Debug("No valid tracks found after augmentation in {0}", rootFolder.Path);
                        continue;
                    }

                    var config = new ImportDecisionMakerConfig
                    {
                        NewDownload = false,
                        SingleRelease = false,
                        IncludeExisting = false,
                        AddNewArtists = false,
                        Filter = FilterFilesType.None
                    };

                    // Identify albums with our improved matching
                    var identified = _identificationService.Identify(
                        localTracks,
                        null,
                        config);

                    // Filter for high-confidence matches
                    var highConfidenceMatches = identified
                        .Where(x => x.AlbumRelease != null &&
                                   x.Distance.NormalizedDistance < 0.15) // 85%+ confidence
                        .ToList();

                    totalRetried += mediaFiles.Count;
                    totalImported += highConfidenceMatches.Count;

                    if (highConfidenceMatches.Any())
                    {
                        _logger.Info("Auto-importing {0} high-confidence matches from {1}",
                                    highConfidenceMatches.Count, rootFolder.Path);

                        try
                        {
                            _importApprovedTracks.Import(highConfidenceMatches, false, null);
                        }
                        catch (Exception e)
                        {
                            _logger.Error(e, "Error during auto-import of matched tracks");
                        }
                    }
                }
                catch (Exception e)
                {
                    _logger.Error(e, "Error processing root folder {0} for auto-retry", rootFolder.Path);
                }
            }

            _logger.ProgressInfo("Auto-retry of failed imports completed. Retried: {0}, Imported: {1}",
                                totalRetried, totalImported);
        }
    }
}
