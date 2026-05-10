using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MediaFiles.TrackImport;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.Download
{
    public interface ICompletedDownloadService
    {
        void Check(TrackedDownload trackedDownload);
        void Import(TrackedDownload trackedDownload);
        bool VerifyImport(TrackedDownload trackedDownload, List<ImportResult> importResults);
    }

    public class CompletedDownloadService : ICompletedDownloadService
    {
        private readonly IEventAggregator _eventAggregator;
        private readonly IHistoryService _historyService;
        private readonly IDownloadedTracksImportService _downloadedTracksImportService;
        private readonly IArtistService _artistService;
        private readonly IProvideImportItemService _provideImportItemService;
        private readonly IParsingService _parsingService;
        private readonly ITrackedDownloadAlreadyImported _trackedDownloadAlreadyImported;
        private readonly Logger _logger;

        public CompletedDownloadService(IEventAggregator eventAggregator,
                                        IHistoryService historyService,
                                        IProvideImportItemService provideImportItemService,
                                        IDownloadedTracksImportService downloadedTracksImportService,
                                        IArtistService artistService,
                                        IParsingService parsingService,
                                        ITrackedDownloadAlreadyImported trackedDownloadAlreadyImported,
                                        Logger logger)
        {
            _eventAggregator = eventAggregator;
            _historyService = historyService;
            _provideImportItemService = provideImportItemService;
            _downloadedTracksImportService = downloadedTracksImportService;
            _artistService = artistService;
            _parsingService = parsingService;
            _trackedDownloadAlreadyImported = trackedDownloadAlreadyImported;
            _logger = logger;
        }

        public void Check(TrackedDownload trackedDownload)
        {
            if (trackedDownload.DownloadItem.Status != DownloadItemStatus.Completed)
            {
                return;
            }

            SetImportItem(trackedDownload);

            // Only process tracked downloads that are still downloading or have been blocked for importing due to an issue with matching
            if (trackedDownload.State != TrackedDownloadState.Downloading && trackedDownload.State != TrackedDownloadState.ImportBlocked)
            {
                return;
            }

            // ImportBlocked covers two distinct cases. The "user must intervene" cases
            // (Artist mismatch / Unable to parse / etc.) deserve a re-check whenever the
            // monitor refreshes — the user may have fixed the underlying problem. But
            // the "no MusicBrainz match anywhere" case (set by TryHandleNonActionable)
            // is structurally unfixable from inside Lidarr; re-running Check on it just
            // bounces the state back to ImportPending, the importer marks it ImportBlocked
            // again, and we burn CPU forever (~31 cycles per item over 2.5h was observed).
            // Recognize that case by its StatusMessages and exit before the reset.
            if (trackedDownload.State == TrackedDownloadState.ImportBlocked
                && IsBlockedByUnfindableMetadata(trackedDownload))
            {
                return;
            }

            var historyItem = _historyService.MostRecentForDownloadId(trackedDownload.DownloadItem.DownloadId);

            if (historyItem == null && trackedDownload.DownloadItem.Category.IsNullOrWhiteSpace())
            {
                trackedDownload.Warn("Download wasn't grabbed by Lidarr and not in a category, Skipping.");
                return;
            }

            if (!ValidatePath(trackedDownload))
            {
                return;
            }

            var artist = _parsingService.GetArtist(trackedDownload.DownloadItem.Title);

            if (artist == null)
            {
                if (historyItem != null)
                {
                    artist = _artistService.GetArtist(historyItem.ArtistId);
                }

                if (artist == null)
                {
                    trackedDownload.Warn("Artist name mismatch, automatic import is not possible. Check the download troubleshooting entry on the wiki for common causes.");
                    SetStateToImportBlocked(trackedDownload);

                    return;
                }
            }

            trackedDownload.State = TrackedDownloadState.ImportPending;
        }

        public void Import(TrackedDownload trackedDownload)
        {
            SetImportItem(trackedDownload);

            if (!ValidatePath(trackedDownload))
            {
                return;
            }

            if (trackedDownload.RemoteAlbum == null)
            {
                trackedDownload.Warn("Unable to parse download, automatic import is not possible.");
                SetStateToImportBlocked(trackedDownload);

                return;
            }

            trackedDownload.State = TrackedDownloadState.Importing;

            var outputPath = trackedDownload.ImportItem.OutputPath.FullPath;
            var importResults = _downloadedTracksImportService.ProcessPath(outputPath, ImportMode.Auto, trackedDownload.RemoteAlbum.Artist, trackedDownload.ImportItem);

            if (VerifyImport(trackedDownload, importResults))
            {
                return;
            }

            trackedDownload.State = TrackedDownloadState.ImportPending;

            if (importResults.Empty())
            {
                trackedDownload.Warn("No files found are eligible for import in {0}", outputPath);

                return;
            }

            if (importResults.Count == 1)
            {
                var firstResult = importResults.First();

                if (firstResult.Result == ImportResultType.Rejected && firstResult.ImportDecision.Item == null)
                {
                    trackedDownload.Warn(new TrackedDownloadStatusMessage(firstResult.Errors.First(), new List<string>()));

                    return;
                }
            }

            var statusMessages = new List<TrackedDownloadStatusMessage>
                                 {
                                    new TrackedDownloadStatusMessage("One or more tracks expected in this release were not imported or missing from the release", new List<string>())
                                 };

            if (importResults.Any(c => c.Result != ImportResultType.Imported))
            {
                statusMessages.AddRange(
                    importResults
                        .Where(v => v.Result != ImportResultType.Imported && v.ImportDecision.Item != null)
                        .OrderBy(v => v.ImportDecision.Item.Path)
                        .Select(v =>
                            new TrackedDownloadStatusMessage(Path.GetFileName(v.ImportDecision.Item.Path),
                                v.Errors)));

                // Short-circuit terminal states: we evaluate whether every non-imported
                // result is "non-actionable" (either benign — we already have it — or
                // structurally unfixable — MB has no entry). If so we move the download
                // out of the retry loop. Otherwise it stays ImportFailed so it gets
                // another pass when matching code or metadata changes.
                if (TryHandleNonActionable(trackedDownload, importResults, statusMessages))
                {
                    return;
                }

                // Mark as failed to prevent further attempts at processing
                trackedDownload.State = TrackedDownloadState.ImportFailed;

                if (statusMessages.Any())
                {
                    trackedDownload.Warn(statusMessages.ToArray());
                }

                // Publish event to notify album was imported incomplete
                _eventAggregator.PublishEvent(new AlbumImportIncompleteEvent(trackedDownload));

                return;
            }

            if (statusMessages.Any())
            {
                trackedDownload.Warn(statusMessages.ToArray());
                SetStateToImportBlocked(trackedDownload);
            }
        }

        // Rejection reasons split into two non-actionable buckets:
        //
        // _alreadyHaveContentPrefixes — the library already has this. The download
        //   succeeded from the user's perspective; just nothing to import. State →
        //   Imported, queue clears.
        //
        // _unfindableInMetadataPrefixes — Lidarr couldn't identify the content
        //   against any MusicBrainz release. We can't import without metadata.
        //   State → ImportBlocked: still visible in queue, but not auto-retried.
        //   The user can manually import via UI, or we'll re-process automatically
        //   after a Lidarr restart (cache cleared) or a user-triggered refresh.
        //
        // Anything else (album/track score thresholds, "destination already exists",
        // etc.) stays in the normal ImportFailed retry loop because future code
        // changes or metadata updates could plausibly fix it.
        private static readonly string[] _alreadyHaveContentPrefixes =
        {
            "All matched tracks already in library",
            "Has fewer tracks than existing release",
            "Not an upgrade for existing album file",
            "Not an upgrade for existing track file"
        };

        private static readonly string[] _unfindableInMetadataPrefixes =
        {
            "Couldn't find similar album for",
            "No tracks could be matched to a release"
        };

        private static bool MatchesAnyPrefix(string error, string[] prefixes)
        {
            if (string.IsNullOrWhiteSpace(error))
            {
                return false;
            }

            foreach (var prefix in prefixes)
            {
                if (error.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        // Look at the StatusMessages currently attached to the tracked download (these
        // are what the importer most recently put on it). True iff there's at least one
        // file-level message and every file-level message is one of our two non-actionable
        // bucket reasons, AND at least one is "couldn't find" (otherwise it would have
        // been set Imported, not ImportBlocked, by TryHandleNonActionable). Used to keep
        // Check from re-entering the import pipeline for items we've already determined
        // can't be auto-resolved.
        private static bool IsBlockedByUnfindableMetadata(TrackedDownload trackedDownload)
        {
            var groups = trackedDownload.StatusMessages;
            if (groups == null || groups.Length == 0)
            {
                return false;
            }

            // Skip the leading "One or more tracks expected ..." header message which
            // has no per-file content; only file-level entries (.Messages.Count > 0)
            // carry our rejection reasons.
            var fileLevel = groups.SelectMany(g => g.Messages ?? new List<string>()).ToList();
            if (fileLevel.Count == 0)
            {
                return false;
            }

            var hasUnfindable = false;
            foreach (var msg in fileLevel)
            {
                if (MatchesAnyPrefix(msg, _alreadyHaveContentPrefixes))
                {
                    continue;
                }

                if (MatchesAnyPrefix(msg, _unfindableInMetadataPrefixes))
                {
                    hasUnfindable = true;
                    continue;
                }

                // Some other message we don't classify — fall through to retry, since
                // the situation may have changed.
                return false;
            }

            return hasUnfindable;
        }

        private bool TryHandleNonActionable(TrackedDownload trackedDownload, List<ImportResult> importResults, List<TrackedDownloadStatusMessage> statusMessages)
        {
            // Walk every non-imported result. If any of them includes a rejection we
            // could plausibly fix later (i.e. doesn't match either non-actionable
            // bucket), fall back to the normal ImportFailed retry path — we don't want
            // to bury items that future code changes could rescue.
            //
            // If everything's accounted for, choose between Imported (we have it all)
            // and ImportBlocked (some content can't be matched).
            var nonImported = importResults.Where(r => r.Result != ImportResultType.Imported).ToList();
            if (nonImported.Count == 0)
            {
                return false;
            }

            var hasUnfindable = false;

            foreach (var result in nonImported)
            {
                if (result.Errors == null || result.Errors.Count == 0)
                {
                    return false;
                }

                foreach (var error in result.Errors)
                {
                    if (MatchesAnyPrefix(error, _alreadyHaveContentPrefixes))
                    {
                        continue;
                    }

                    if (MatchesAnyPrefix(error, _unfindableInMetadataPrefixes))
                    {
                        hasUnfindable = true;
                        continue;
                    }

                    // Some other rejection — leave for retry under existing flow.
                    return false;
                }
            }

            if (hasUnfindable)
            {
                _logger.Info("Download '{0}' has files Lidarr couldn't match in MusicBrainz; marking as ImportBlocked (manual import or MB metadata needed).", trackedDownload.DownloadItem.Title);

                if (statusMessages.Any())
                {
                    trackedDownload.Warn(statusMessages.ToArray());
                }

                SetStateToImportBlocked(trackedDownload);
                return true;
            }

            _logger.Info("Download '{0}' content already in library; marking as Imported (no upgrades available).", trackedDownload.DownloadItem.Title);
            trackedDownload.State = TrackedDownloadState.Imported;

            if (trackedDownload.RemoteAlbum?.Artist != null)
            {
                _eventAggregator.PublishEvent(new DownloadCompletedEvent(trackedDownload, trackedDownload.RemoteAlbum.Artist.Id));
            }

            return true;
        }

        public bool VerifyImport(TrackedDownload trackedDownload, List<ImportResult> importResults)
        {
            var allTracksImported =
                (importResults.Any() && importResults.All(c => c.Result == ImportResultType.Imported)) ||
                importResults.Where(c => c.Result == ImportResultType.Imported)
                    .SelectMany(c => c.ImportDecision.Item.Tracks)
                    .Count() >= Math.Max(1, trackedDownload.RemoteAlbum.Albums.Sum(x => x.AlbumReleases.Value.Where(y => y.Monitored).Sum(z => z.TrackCount)));

            if (allTracksImported)
            {
                _logger.Debug("All albums were imported for {0}", trackedDownload.DownloadItem.Title);
                trackedDownload.State = TrackedDownloadState.Imported;

                _eventAggregator.PublishEvent(new DownloadCompletedEvent(trackedDownload, trackedDownload.RemoteAlbum.Artist.Id));
                return true;
            }

            var historyItems = _historyService.FindByDownloadId(trackedDownload.DownloadItem.DownloadId)
                .OrderByDescending(h => h.Date)
                .ToList();

            // Double check if all episodes were imported by checking the history if at least one
            // file was imported. This will allow the decision engine to reject already imported
            // episode files and still mark the download complete when all files are imported.

            // EDGE CASE: This process relies on EpisodeIds being consistent between executions, if a series is updated
            // and an episode is removed, but later comes back with a different ID then Sonarr will treat it as incomplete.
            // Since imports should be relatively fast and these types of data changes are infrequent this should be quite
            // safe, but commenting for future benefit.
            var atLeastOneTrackImported = importResults.Any(c => c.Result == ImportResultType.Imported);
            var allTracksImportedInHistory = _trackedDownloadAlreadyImported.IsImported(trackedDownload, historyItems);

            if (allTracksImportedInHistory)
            {
                // Log different error messages depending on the circumstances, but treat both as fully imported, because that's the reality.
                // The second message shouldn't be logged in most cases, but continued reporting would indicate an ongoing issue.
                if (atLeastOneTrackImported)
                {
                    _logger.Debug("All albums were imported in history for {0}", trackedDownload.DownloadItem.Title);
                }
                else
                {
                    _logger.ForDebugEvent()
                           .Message("No albums were just imported, but all albums were previously imported, possible issue with download history.")
                           .Property("ArtistId", trackedDownload.RemoteAlbum.Artist.Id)
                           .Property("DownloadId", trackedDownload.DownloadItem.DownloadId)
                           .Property("Title", trackedDownload.DownloadItem.Title)
                           .Property("Path", trackedDownload.DownloadItem.OutputPath.ToString())
                           .WriteSentryWarn("DownloadHistoryIncomplete")
                           .Log();
                }

                trackedDownload.State = TrackedDownloadState.Imported;
                _eventAggregator.PublishEvent(new DownloadCompletedEvent(trackedDownload, trackedDownload.RemoteAlbum.Artist.Id));

                return true;
            }

            _logger.Debug("Not all albums have been imported for the release '{0}'", trackedDownload.DownloadItem.Title);
            return false;
        }

        private void SetStateToImportBlocked(TrackedDownload trackedDownload)
        {
            trackedDownload.State = TrackedDownloadState.ImportBlocked;
        }

        private void SetImportItem(TrackedDownload trackedDownload)
        {
            trackedDownload.ImportItem = _provideImportItemService.ProvideImportItem(trackedDownload.DownloadItem, trackedDownload.ImportItem);
        }

        private bool ValidatePath(TrackedDownload trackedDownload)
        {
            var downloadItemOutputPath = trackedDownload.ImportItem.OutputPath;

            if (downloadItemOutputPath.IsEmpty)
            {
                trackedDownload.Warn("Download doesn't contain intermediate path, Skipping.");
                return false;
            }

            if ((OsInfo.IsWindows && !downloadItemOutputPath.IsWindowsPath) ||
                (OsInfo.IsNotWindows && !downloadItemOutputPath.IsUnixPath))
            {
                trackedDownload.Warn("[{0}] is not a valid local path. You may need a Remote Path Mapping.", downloadItemOutputPath);
                return false;
            }

            return true;
        }
    }
}
