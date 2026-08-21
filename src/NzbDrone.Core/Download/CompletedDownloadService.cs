using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.ImportLists.Exclusions;
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
        private readonly IDiskProvider _diskProvider;
        private readonly IDiskScanService _diskScanService;
        private readonly IConfigService _configService;
        private readonly IFailedDownloadService _failedDownloadService;
        private readonly IImportListExclusionService _exclusionService;
        private readonly Logger _logger;

        public CompletedDownloadService(IEventAggregator eventAggregator,
                                        IHistoryService historyService,
                                        IProvideImportItemService provideImportItemService,
                                        IDownloadedTracksImportService downloadedTracksImportService,
                                        IArtistService artistService,
                                        IParsingService parsingService,
                                        ITrackedDownloadAlreadyImported trackedDownloadAlreadyImported,
                                        IExtractionService extractionService,
                                        IDiskProvider diskProvider,
                                        IDiskScanService diskScanService,
                                        IConfigService configService,
                                        IFailedDownloadService failedDownloadService,
                                        IImportListExclusionService exclusionService,
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
            _diskProvider = diskProvider;
            _diskScanService = diskScanService;
            _configService = configService;
            _failedDownloadService = failedDownloadService;
            _exclusionService = exclusionService;
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

            var outputPath = trackedDownload.ImportItem.OutputPath.FullPath;

            // Extract any archives we recognise BEFORE setting Importing state and BEFORE
            // ProcessPath scans for audio. The marker file convention makes this idempotent
            // across refreshes; on the first pass after a .tar lands, audio appears for
            // free. See src/NzbDrone.Core/MediaFiles/Extraction/ExtractionService.cs.
            _extractionService.ExtractIfNeeded(outputPath);

            trackedDownload.State = TrackedDownloadState.Importing;

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
            // (User-curated exclusion) The file's tags identify it as part of an
            // artist or album the user has explicitly excluded via the Add-list-
            // exclusion checkbox on artist/album delete. That's an active
            // preference, not a passive "skip in import lists" flag — honour it
            // by terminal-failing the download. Same severity as a malware
            // payload: blocklist + remove from download client + don't re-grab.
            if (_configService.RespectExclusionsOnImport)
            {
                var excluded = FindExcludedMatch(importResults);
                if (excluded != null)
                {
                    _logger.Warn(
                        "Download {0} is tagged for excluded {1} '{2}' (MBID {3}) — marking failed and removing",
                        trackedDownload.DownloadItem.Title,
                        excluded.Kind,
                        excluded.Name,
                        excluded.ForeignId);
                    _failedDownloadService.MarkAsFailed(trackedDownload, skipRedownload: true);
                    return;
                }
            }

            // (V3) The download contained no files Lidarr could even attempt to import
            // (Blu-ray ISO, SACD video, archive that wasn't extracted, empty folder).
            // Retrying won't help — the file set on disk doesn't change shape between
            // refreshes. Surface it to the user via ImportBlocked.
            if (importResults.Empty())
            {
                // Stronger signal: we already unpacked an archive in this folder
                // (a .lidarr-extracted marker is present) and there's still no audio
                // to import. The archive was a dud — DVD-Video disc, an .exe-only
                // payload, etc. Treat it the same as a failed download: blocklist
                // the release, tell the download client to remove the files, and
                // do NOT re-grab. This is the terminal state ImportBlocked refuses
                // to be (ImportBlocked is "intervene and re-check"; this is "give up").
                if (HasExtractedNoAudio(outputPath))
                {
                    _logger.Warn(
                        "Download {0} was extracted but contains no audio — marking failed and removing",
                        trackedDownload.DownloadItem.Title);
                    _failedDownloadService.MarkAsFailed(trackedDownload, skipRedownload: true);
                    return;
                }

                // Music-video / concert grab: video files present, zero importable
                // audio. Lidarr can't use it. Terminal-fail it (blocklist + remove from
                // client + don't re-grab) so it stops clogging the queue, unless the
                // user has opted out (then it parks as ImportBlocked like before).
                if (IsVideoOnlyDownload(outputPath))
                {
                    if (_configService.DeleteVideoOnlyDownloads)
                    {
                        _logger.Warn(
                            "Download {0} contains video files but no audio — Lidarr only imports audio. Marking failed, blocklisting, and removing from the download client.",
                            trackedDownload.DownloadItem.Title);
                        _failedDownloadService.MarkAsFailed(trackedDownload, skipRedownload: true);
                        return;
                    }

                    trackedDownload.Warn("Download contains video files but no audio; Lidarr only imports audio.");
                    SetStateToImportBlocked(trackedDownload);
                    return;
                }

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
                    // A lone unimportable file that's actually a video (e.g. a single
                    // ".mkv" music video) gets the same terminal-fail treatment as the
                    // folder case above, when enabled.
                    if (_configService.DeleteVideoOnlyDownloads && IsVideoOnlyDownload(outputPath))
                    {
                        _logger.Warn(
                            "Download {0} is a single video file with no audio — Lidarr only imports audio. Marking failed, blocklisting, and removing from the download client.",
                            trackedDownload.DownloadItem.Title);
                        _failedDownloadService.MarkAsFailed(trackedDownload, skipRedownload: true);
                        return;
                    }

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
            // first; it may move us to Imported (we already have everything), to
            // Imported via orphan-import (we couldn't match in MB but filed the audio
            // into the artist's .unmatched/ folder anyway), or to ImportBlocked
            // (some non-MB content + we can't recover). Each is terminal.
            if (TryHandleNonActionable(trackedDownload, importResults, outputPath))
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
            "Not an upgrade for existing track file",

            // Album-level rejection from AlreadyImportedSpecification: download history
            // shows we already imported this exact download. The bits are redundant —
            // treat as Imported so the queue clears and the client cleans up the source.
            "Album already imported at"
        };

        private static readonly string[] _unfindableInMetadataPrefixes =
        {
            "Couldn't find similar album for",
            "No tracks could be matched to a release"
        };

        // Rejections that mean "this download is done with as far as auto-import is
        // concerned, but we must NOT delete or claim we imported it" — park as
        // ImportBlocked (terminal-but-visible) instead. Distinct from the
        // already-have bucket because the cause is not a confirmed duplicate:
        //
        //   "Failed to import track, Destination already exists." is thrown at the
        //   file-move stage, not by the decision engine. It usually means a true
        //   duplicate, but it can also be a naming-token collision between two
        //   genuinely different tracks/releases, or a stale orphan file at the
        //   destination with no TrackFile row. Marking it Imported + removing the
        //   source would silently discard a real download in those cases, so we
        //   surface it for the user rather than guess. Also listed in
        //   _persistentImportFailurePrefixes so Check doesn't bounce it every refresh.
        private static readonly string[] _blockedNeedsReviewPrefixes =
        {
            "Failed to import track, Destination already exists"
        };

        // Rejections where the specific RELEASE (not the album) is unimportable and
        // re-grabbing the identical release will always fail the same way: unparseable
        // files, wrong artist, or a match too weak to trust. Blocklist the release
        // (MarkAsFailed, skipRedownload) so the missing-album search can never
        // re-download this exact one — the album stays wanted, so a DIFFERENT release
        // is tried next. This is the "fetch each release at most once" guarantee, and it
        // deliberately overrides the older "keep score-thresholds retryable" behaviour:
        // a retry here means a re-grab, which is the duplicate-download problem itself.
        private static readonly string[] _blocklistUnimportableReleasePrefixes =
        {
            "Album match is not close enough",
            "Couldn't parse track from",
            "Could not parse file for import",
            "Unable to parse download, automatic import is not possible",
            "Invalid audio file, unsupported extension",
            "Artist name mismatch"
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
            "Download wasn't grabbed by Lidarr",
            "Failed to import track, Destination already exists" // collision parked as ImportBlocked, don't re-run
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
                        MatchesAnyPrefix(entry, _blockedNeedsReviewPrefixes) ||
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

        private bool TryHandleNonActionable(TrackedDownload trackedDownload, List<ImportResult> importResults, string outputPath)
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
            var needsReview = false;
            var mustBlocklist = false;

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

                    if (MatchesAnyPrefix(error, _blockedNeedsReviewPrefixes))
                    {
                        needsReview = true;
                        continue;
                    }

                    if (MatchesAnyPrefix(error, _blocklistUnimportableReleasePrefixes))
                    {
                        mustBlocklist = true;
                        continue;
                    }

                    // Some other rejection — leave for retry under existing flow.
                    return false;
                }
            }

            // Destination-collision (and friends): park as ImportBlocked so the user
            // can see it, but never delete the source or record a false import. We
            // don't orphan-import these — the files DID match an album, the move just
            // collided, so .unmatched/ is the wrong home. ImportBlocked wins over the
            // unfindable/orphan path when both are present in a mixed download.
            if (needsReview)
            {
                _logger.Info("Download '{0}' hit a destination collision / needs-review rejection; marking ImportBlocked (no delete, no re-grab).", trackedDownload.DownloadItem.Title);

                var reviewMessages = BuildPerFileStatusMessages(nonImported);
                if (reviewMessages.Count > 0)
                {
                    trackedDownload.Warn(reviewMessages.ToArray());
                }

                SetStateToImportBlocked(trackedDownload);
                return true;
            }

            if (hasUnfindable || mustBlocklist)
            {
                // For unfindable audio, first file the bits into the artist's
                // .unmatched/ folder so a genuine download isn't lost (parse-garbage from
                // the blocklist bucket has nothing worth saving). Then, either way,
                // blocklist the release so the missing-album search can never re-download
                // this exact one — the album stays wanted, so a DIFFERENT release is tried
                // next. This is what stops the "grab -> fail -> grab the same release again"
                // loop that was hammering the indexer.
                if (hasUnfindable)
                {
                    TryOrphanImport(trackedDownload, nonImported, outputPath);
                }

                _logger.Info("Download '{0}' can't be imported (unparseable or unmatchable release); blocklisting it so it isn't re-grabbed. The album stays wanted for a different release.", trackedDownload.DownloadItem.Title);

                var perFile = BuildPerFileStatusMessages(nonImported);
                if (perFile.Count > 0)
                {
                    trackedDownload.Warn(perFile.ToArray());
                }

                _failedDownloadService.MarkAsFailed(trackedDownload, skipRedownload: true);
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

        // Move audio files Lidarr couldn't match in MB into the artist's library
        // folder under `.unmatched/<original-download-folder>/`. The dot-prefix
        // means DiskScanService skips this directory on future scans, so we don't
        // re-attempt identification on it forever; the bits live next to the
        // artist's matched albums for organisational clarity.
        //
        // Prototype constraints (will probably revisit before upstreaming):
        //   - We need a parsed Artist with a real Path; without one, we don't
        //     know where the files belong and bail out (caller falls through to
        //     ImportBlocked).
        //   - Files are MOVED, not copied. SAB cleans up the empty download
        //     folder on the next refresh as it does for normal imports.
        //   - No DB rows are created for the moved files. The Lidarr UI won't
        //     show them under the artist; that requires schema work for
        //     "TrackFile without Track" which is out of scope here.
        //   - A `_lidarr-unmatched.txt` marker is dropped in the destination
        //     folder with download title, timestamp, and the rejection reasons
        //     so the user can see what happened later.
        // Result of an exclusion-match lookup. Carries enough to log clearly
        // and (later) to surface in the modal as "rejected by user preference".
        private sealed class ExcludedMatch
        {
            public string Kind { get; init; }
            public string Name { get; init; }
            public string ForeignId { get; init; }
        }

        // Walk the parsed file tags of every importResult; if any file's
        // ArtistMBId or AlbumMBId is in the user's ImportListExclusion table,
        // return the first hit. Returns null if no MBIDs match (or no MBIDs
        // were tagged — the MB-name lookup fallback for untagged files arrives
        // with feature (b) and is intentionally out of scope here).
        private ExcludedMatch FindExcludedMatch(List<ImportResult> importResults)
        {
            var mbids = importResults
                .Select(r => r.ImportDecision?.Item?.FileTrackInfo)
                .Where(t => t != null)
                .SelectMany(t => new[] { t.ArtistMBId, t.AlbumMBId, t.ReleaseMBId })
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct()
                .ToList();

            if (mbids.Count == 0)
            {
                return null;
            }

            var exclusions = _exclusionService.FindByForeignId(mbids);
            if (exclusions.Count == 0)
            {
                return null;
            }

            var hit = exclusions[0];

            // Heuristic: artist MBIDs from MB are bare GUIDs; album and release
            // MBIDs are too, so we can't tell them apart by shape. Cross-reference
            // against the per-file tags to label the kind for the log line. Falls
            // back to "match" if we can't decide cleanly.
            var kind = "match";
            foreach (var r in importResults)
            {
                var t = r.ImportDecision?.Item?.FileTrackInfo;
                if (t == null)
                {
                    continue;
                }

                if (t.ArtistMBId == hit.ForeignId)
                {
                    kind = "artist";
                    break;
                }

                if (t.AlbumMBId == hit.ForeignId || t.ReleaseMBId == hit.ForeignId)
                {
                    kind = "album";
                    break;
                }
            }

            return new ExcludedMatch
            {
                Kind = kind,
                Name = hit.Name,
                ForeignId = hit.ForeignId,
            };
        }

        // True iff ExtractionService has previously unpacked at least one archive
        // under outputPath and no audio remains for import. The marker (suffix
        // ExtractionService.MarkerSuffix, written only after a successful extract)
        // is the persistence layer for "we already tried" — survives restarts,
        // so this stays correct across refresh cycles instead of only firing on
        // the same Check() that did the extraction.
        private bool HasExtractedNoAudio(string outputPath)
        {
            if (!_diskProvider.FolderExists(outputPath))
            {
                return false;
            }

            var hasMarker = _diskProvider.GetFiles(outputPath, true)
                .Any(p => p.EndsWith(ExtractionService.MarkerSuffix));

            if (!hasMarker)
            {
                return false;
            }

            return !_diskScanService.GetAudioFiles(outputPath).Any();
        }

        // True iff the download is a music-video / concert grab: at least one video
        // file present and ZERO importable audio. "No audio" is decided by the
        // authoritative disk scan (the same GetAudioFiles HasExtractedNoAudio and
        // TryOrphanImport use), never by the absence of import results — so a folder
        // that holds real music plus a bonus video never qualifies, even if those
        // tracks failed to import for an unrelated reason. This is the guard that
        // keeps the blocklist+delete action from ever touching real music.
        private bool IsVideoOnlyDownload(string outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                return false;
            }

            // Single-file download whose path is the file itself.
            if (_diskProvider.FileExists(outputPath))
            {
                return FileExtensions.VideoExtensions.Contains(Path.GetExtension(outputPath));
            }

            if (!_diskProvider.FolderExists(outputPath))
            {
                return false;
            }

            if (_diskScanService.GetAudioFiles(outputPath).Any())
            {
                return false;
            }

            return _diskProvider.GetFiles(outputPath, true)
                .Any(f => FileExtensions.VideoExtensions.Contains(Path.GetExtension(f)));
        }

        private bool TryOrphanImport(TrackedDownload trackedDownload, List<ImportResult> nonImported, string outputPath)
        {
            // Master toggle: if the user has switched orphan-import off they want
            // the items in ImportBlocked so they can be triaged manually.
            if (!_configService.OrphanImportEnabled)
            {
                return false;
            }

            var artist = trackedDownload.RemoteAlbum?.Artist;
            if (artist == null || string.IsNullOrWhiteSpace(artist.Path))
            {
                return false;
            }

            if (!_diskProvider.FolderExists(outputPath))
            {
                return false;
            }

            // GetAudioFiles applies DiskScanService's exclusion regex (._*, .DS_Store,
            // dot-prefixed folders, etc.) so we don't carry junk into the destination.
            var audioFiles = _diskScanService.GetAudioFiles(outputPath).ToList();

            if (audioFiles.Count == 0)
            {
                return false;
            }

            var downloadFolderName = Path.GetFileName(outputPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(downloadFolderName))
            {
                downloadFolderName = "unknown-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            }

            var subfolder = (_configService.OrphanImportSubfolder ?? string.Empty).Trim();
            var destinationRoot = string.IsNullOrEmpty(subfolder)
                ? Path.Combine(artist.Path, downloadFolderName)
                : Path.Combine(artist.Path, subfolder, downloadFolderName);

            try
            {
                _diskProvider.CreateFolder(destinationRoot);

                string lastDestDir = null;
                foreach (var file in audioFiles)
                {
                    // Preserve the file's relative location so multi-disc / discography
                    // structure inside the download is intact at the destination.
                    var relative = outputPath.GetRelativePath(file.FullName);
                    var destination = Path.Combine(destinationRoot, relative);
                    var destDir = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(destDir) && destDir != lastDestDir)
                    {
                        _diskProvider.CreateFolder(destDir);
                        lastDestDir = destDir;
                    }

                    _diskProvider.MoveFile(file.FullName, destination, overwrite: true);
                }

                if (_configService.OrphanImportWriteMarker)
                {
                    WriteOrphanMarker(destinationRoot, trackedDownload, nonImported);
                }

                _logger.Info(
                    "Orphan-imported {0} audio files from '{1}' into {2} (couldn't match in MB)",
                    audioFiles.Count,
                    trackedDownload.DownloadItem.Title,
                    destinationRoot);

                trackedDownload.State = TrackedDownloadState.Imported;
                _eventAggregator.PublishEvent(new DownloadCompletedEvent(trackedDownload, artist.Id));
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Orphan import failed for '{0}'; falling back to ImportBlocked", trackedDownload.DownloadItem.Title);
                return false;
            }
        }

        private void WriteOrphanMarker(string destinationRoot, TrackedDownload trackedDownload, List<ImportResult> nonImported)
        {
            try
            {
                var lines = new List<string>
                {
                    $"Source download : {trackedDownload.DownloadItem.Title}",
                    $"Download id     : {trackedDownload.DownloadItem.DownloadId}",
                    $"Imported at     : {DateTime.UtcNow:o}",
                    $"Reason          : Lidarr couldn't match these files against any MusicBrainz release for the parsed artist.",
                    string.Empty,
                    "Per-file rejection messages:"
                };

                foreach (var r in nonImported.Take(50))
                {
                    var path = r.ImportDecision?.Item?.Path ?? "(unknown)";
                    var err = r.Errors == null || r.Errors.Count == 0 ? "(none)" : string.Join("; ", r.Errors);
                    lines.Add($"  {Path.GetFileName(path)}: {err}");
                }

                lines.Add(string.Empty);
                lines.Add("These files have NOT been linked to a Lidarr Album record. Use the manual");
                lines.Add("import UI on the parent folder to disambiguate, or move them into the");
                lines.Add("appropriate album folder by hand. See Settings → Media Management → Orphan");
                lines.Add("Import for placement and marker controls.");

                // _diskProvider.WriteAllText (vs File.WriteAllText) avoids a known
                // .NET Core bug on CIFS-mounted folders — see DiskProviderBase.
                _diskProvider.WriteAllText(Path.Combine(destinationRoot, "_lidarr-unmatched.txt"), string.Join("\n", lines));
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Couldn't write orphan-import marker in {0}", destinationRoot);
            }
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
