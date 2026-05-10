using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.MediaFiles.Extraction;
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
        private readonly IExtractionService _extractionService;
        private readonly Logger _logger;

        public CompletedDownloadService(IEventAggregator eventAggregator,
                                        IHistoryService historyService,
                                        IProvideImportItemService provideImportItemService,
                                        IDownloadedTracksImportService downloadedTracksImportService,
                                        IArtistService artistService,
                                        IParsingService parsingService,
                                        ITrackedDownloadAlreadyImported trackedDownloadAlreadyImported,
                                        IExtractionService extractionService,
                                        Logger logger)
        {
            _eventAggregator = eventAggregator;
            _historyService = historyService;
            _provideImportItemService = provideImportItemService;
            _downloadedTracksImportService = downloadedTracksImportService;
            _artistService = artistService;
            _parsingService = parsingService;
            _trackedDownloadAlreadyImported = trackedDownloadAlreadyImported;
            _extractionService = extractionService;
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
                && IsBlockedByPersistentFailure(trackedDownload))
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

        // Invariants this method now enforces — keep them when editing:
        //
        //   1. Every code path through Import sets the TrackedDownload's State to
        //      exactly one terminal-or-pending value before returning. No path
        //      "leaks" with State == Importing left behind (the only legitimate
        //      Importing-on-exit case is an exception escaping ProcessPath, which
        //      DownloadProcessingService.Execute's catch block normalizes back to
        //      ImportPending).
        //
        //   2. The state assignment is the LAST thing that varies per path. There
        //      is no "default ImportPending then maybe override" pattern — that
        //      was the bug behind V3/V5 in docs/tracked-download-state-machine.md.
        //
        //   3. Per-file rejection messages are only attached when there are real
        //      per-file rejections. The "One or more tracks ..." header is added
        //      only as a parent grouping for actual file-level entries, never as
        //      a free-standing message (V4).
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

            // Extract any archives we recognise BEFORE setting Importing state and BEFORE
            // ProcessPath scans for audio. The marker file convention makes this idempotent
            // across refreshes; on the first pass after a .tar lands, audio appears for
            // free. See src/NzbDrone.Core/MediaFiles/Extraction/ExtractionService.cs.
            var preExtractOutput = trackedDownload.ImportItem.OutputPath.FullPath;
            _extractionService.ExtractIfNeeded(preExtractOutput);

            trackedDownload.State = TrackedDownloadState.Importing;

            var outputPath = trackedDownload.ImportItem.OutputPath.FullPath;
            List<ImportResult> importResults;

            try
            {
                importResults = _downloadedTracksImportService.ProcessPath(outputPath, ImportMode.Auto, trackedDownload.RemoteAlbum.Artist, trackedDownload.ImportItem);
            }
            catch (NotParentException ex)
            {
                // Deterministic data-shape error (e.g. existing TrackFile path is not under
                // the artist's current root folder — usually because the artist's path was
                // changed without moving the files, or because the same artist has files
                // under two different roots). Re-running won't fix this without user
                // intervention. Park as ImportBlocked with the exception message so the
                // user sees what's wrong instead of an endless DownloadProcessingService
                // catch loop. See V12 in docs/tracked-download-state-machine.md.
                _logger.Warn(ex, "Deterministic config error importing {0}; marking ImportBlocked", trackedDownload.DownloadItem.Title);
                trackedDownload.Warn(new TrackedDownloadStatusMessage(ex.Message, new List<string>()));
                SetStateToImportBlocked(trackedDownload);
                return;
            }

            if (VerifyImport(trackedDownload, importResults))
            {
                return;
            }

            ClassifyAndSetState(trackedDownload, importResults, outputPath);
        }

        // Single decision point for the post-ProcessPath terminal state. Each branch
        // sets exactly one State value and (if appropriate) attaches messages and
        // publishes events. Order matters: we handle structural failures (no audio
        // / unparseable file) before content failures (rejection chain), and we
        // give TryHandleNonActionable a chance to short-circuit the rejection chain
        // into Imported/ImportBlocked before falling through to ImportFailed.
        private void ClassifyAndSetState(TrackedDownload trackedDownload, List<ImportResult> importResults, string outputPath)
        {
            // (V3) The download contained no files Lidarr could even attempt to import
            // (Blu-ray ISO, SACD video, archive that wasn't extracted, empty folder).
            // Retrying won't help — the file set on disk doesn't change shape between
            // refreshes. Surface it to the user via ImportBlocked.
            if (importResults.Empty())
            {
                trackedDownload.Warn("No files found are eligible for import in {0}", outputPath);
                SetStateToImportBlocked(trackedDownload);
                return;
            }

            // (V5) The download has a single file that couldn't be parsed into a
            // LocalTrack at all (unsupported extension, corrupt tags, etc.). Same
            // treatment as V3 — the file's shape on disk won't change.
            if (importResults.Count == 1)
            {
                var only = importResults[0];
                if (only.Result == ImportResultType.Rejected && only.ImportDecision.Item == null)
                {
                    var error = only.Errors.FirstOrDefault() ?? "Could not parse file for import";
                    trackedDownload.Warn(new TrackedDownloadStatusMessage(error, new List<string>()));
                    SetStateToImportBlocked(trackedDownload);
                    return;
                }
            }

            // From here on we know there's at least one result with a parsed Item.
            var nonImported = importResults.Where(r => r.Result != ImportResultType.Imported).ToList();

            // No non-imported results at all means every file imported successfully
            // even though VerifyImport returned false (the album's expected track
            // count is higher than what we just imported — usually because the
            // monitored release lists tracks we haven't downloaded). Treat the
            // download as Imported; the missing-tracks problem is independent of
            // whether this download succeeded.
            if (nonImported.Count == 0)
            {
                _logger.Debug("All importResults imported but VerifyImport=false (release track count exceeds what was in this download). Marking {0} as Imported.", trackedDownload.DownloadItem.Title);
                trackedDownload.State = TrackedDownloadState.Imported;

                if (trackedDownload.RemoteAlbum?.Artist != null)
                {
                    _eventAggregator.PublishEvent(new DownloadCompletedEvent(trackedDownload, trackedDownload.RemoteAlbum.Artist.Id));
                }

                return;
            }

            // Mixed or all-rejected. Give the non-actionable short-circuit a turn
            // first; it may move us to Imported (we already have everything) or to
            // ImportBlocked (Lidarr can't match anything in MB). Either is terminal.
            if (TryHandleNonActionable(trackedDownload, importResults))
            {
                return;
            }

            // Fall-through: there's at least one rejection that future code or
            // metadata changes could plausibly fix. Park in ImportFailed for the
            // ResetFailedImportsForRetry loop to pick up next refresh.
            var statusMessages = BuildPerFileStatusMessages(nonImported);
            trackedDownload.State = TrackedDownloadState.ImportFailed;
            trackedDownload.Warn(statusMessages.ToArray());
            _eventAggregator.PublishEvent(new AlbumImportIncompleteEvent(trackedDownload));
        }

        private static List<TrackedDownloadStatusMessage> BuildPerFileStatusMessages(List<ImportResult> nonImported)
        {
            // Header parent + per-file children. The header is only attached when
            // there's at least one per-file message to group; that's why we don't
            // pre-seed it at construction time.
            var perFile = nonImported
                .Where(v => v.ImportDecision.Item != null)
                .OrderBy(v => v.ImportDecision.Item.Path)
                .Select(v => new TrackedDownloadStatusMessage(
                    Path.GetFileName(v.ImportDecision.Item.Path),
                    v.Errors))
                .ToList();

            var messages = new List<TrackedDownloadStatusMessage>();
            if (perFile.Count > 0)
            {
                messages.Add(new TrackedDownloadStatusMessage(
                    "One or more tracks expected in this release were not imported or missing from the release",
                    new List<string>()));
                messages.AddRange(perFile);
            }

            return messages;
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

        // Reasons set by ClassifyAndSetState (V3 / V5) and CheckEmptyResultForIssue
        // that mean re-running Import on the same folder won't change the outcome.
        // Recognised in addition to _unfindableInMetadataPrefixes by Check's
        // persistent-failure guard. Re-evaluated on Lidarr restart (cache clears) or
        // when the folder content actually changes (the user extracts the archive,
        // copies in audio files, etc) — but until then we don't burn cycles re-running
        // Import every refresh.
        private static readonly string[] _persistentImportFailurePrefixes =
        {
            "No files found are eligible for import",       // V3: empty folder / Blu-ray / SACD
            "Found archive file",                           // V5: .rar / .tar / .zip etc
            "Caution: Found executable",                    // V5: .exe / .sh etc
            "Could not parse file for import",              // V5 fallback
            "Invalid audio file, unsupported extension",    // single-file unknown ext
            "Unable to parse download, automatic import is not possible",
            "Artist name mismatch",                          // user-fixable but folder content didn't change
            "Download wasn't grabbed by Lidarr"
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
        // are what the importer most recently put on it). Return true iff every reason
        // we can find matches one of:
        //   - _alreadyHaveContentPrefixes ("we have it" — would be Imported, not Blocked)
        //   - _unfindableInMetadataPrefixes ("MB doesn't have this album yet")
        //   - _persistentImportFailurePrefixes ("nothing on disk for us to import")
        // AND at least one is a real blocker (not just "already have it"). Used to keep
        // Check from re-entering the import pipeline for items we've already classified
        // as can't-auto-resolve. Without this, V5/V3 cases bounce ImportBlocked →
        // ImportPending forever (~50 cycles per Execute pass were observed).
        //
        // Title vs Messages: per StatusMessage, Title is a *label* and Messages are the
        // *reasons*. Three patterns we recognise:
        //   - V3:  Title=DownloadItem.Title, Messages=["No files found..."]
        //   - V5:  Title="Found archive file...", Messages=[]   (label-as-reason)
        //   - V6:  Title=filename.flac, Messages=["Couldn't find similar album..."]
        //          (with a leading "One or more tracks expected..." parent header group)
        // So: when Messages is non-empty, the reasons live there; when Messages is
        // empty, the Title carries the reason. Don't try to match filenames against
        // the prefix list (V6 would never pass).
        private static bool IsBlockedByPersistentFailure(TrackedDownload trackedDownload)
        {
            var groups = trackedDownload.StatusMessages;
            if (groups == null || groups.Length == 0)
            {
                return false;
            }

            const string parentHeader = "One or more tracks expected in this release were not imported";

            var hasBlocker = false;
            foreach (var group in groups)
            {
                // Pick the entries that carry actual reasons. If Messages is non-empty
                // the reasons live there (Title is a per-file label like a filename).
                // If Messages is empty, the reason is in Title (V5 single-rejection
                // path, or our header-only "One or more tracks expected..." entry).
                List<string> reasons;
                if (group.Messages != null && group.Messages.Count > 0)
                {
                    reasons = group.Messages;
                }
                else if (!string.IsNullOrWhiteSpace(group.Title))
                {
                    reasons = new List<string> { group.Title };
                }
                else
                {
                    continue;
                }

                foreach (var entry in reasons)
                {
                    if (string.IsNullOrWhiteSpace(entry))
                    {
                        continue;
                    }

                    if (entry.StartsWith(parentHeader, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (MatchesAnyPrefix(entry, _alreadyHaveContentPrefixes))
                    {
                        continue;
                    }

                    if (MatchesAnyPrefix(entry, _unfindableInMetadataPrefixes) ||
                        MatchesAnyPrefix(entry, _persistentImportFailurePrefixes))
                    {
                        hasBlocker = true;
                        continue;
                    }

                    // Unrecognised reason — allow retry, the situation may have changed.
                    return false;
                }
            }

            return hasBlocker;
        }

        private bool TryHandleNonActionable(TrackedDownload trackedDownload, List<ImportResult> importResults)
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

                // Surface the per-file detail so the user can see exactly which
                // subfolders / files Lidarr couldn't match, then go terminal.
                var perFile = BuildPerFileStatusMessages(nonImported);
                if (perFile.Count > 0)
                {
                    trackedDownload.Warn(perFile.ToArray());
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
