using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles.TrackImport.Aggregation;
using NzbDrone.Core.MediaFiles.TrackImport.Identification;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.MediaFiles.TrackImport
{
    public interface IMakeImportDecision
    {
        List<ImportDecision<LocalTrack>> GetImportDecisions(List<IFileInfo> musicFiles, IdentificationOverrides idOverrides, ImportDecisionMakerInfo itemInfo, ImportDecisionMakerConfig config);
    }

    public class IdentificationOverrides
    {
        public Artist Artist { get; set; }
        public Album Album { get; set; }
        public AlbumRelease AlbumRelease { get; set; }
    }

    public class ImportDecisionMakerInfo
    {
        public DownloadClientItem DownloadClientItem { get; set; }
        public ParsedAlbumInfo ParsedAlbumInfo { get; set; }
    }

    public class ImportDecisionMakerConfig
    {
        public FilterFilesType Filter { get; set; }
        public bool NewDownload { get; set; }
        public bool SingleRelease { get; set; }
        public bool IncludeExisting { get; set; }
        public bool AddNewArtists { get; set; }
    }

    public class ImportDecisionMaker : IMakeImportDecision
    {
        private readonly IEnumerable<IImportDecisionEngineSpecification<LocalTrack>> _trackSpecifications;
        private readonly IEnumerable<IImportDecisionEngineSpecification<LocalAlbumRelease>> _albumSpecifications;
        private readonly IMediaFileService _mediaFileService;
        private readonly IAudioTagService _audioTagService;
        private readonly IAugmentingService _augmentingService;
        private readonly IIdentificationService _identificationService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IQualityProfileService _qualityProfileService;
        private readonly Logger _logger;

        // Cache of GetImportDecisions results keyed by (file paths + sizes + mtimes +
        // overrides + config). Auto-import normally runs full identification when a
        // download completes; opening the manual-import modal seconds-to-minutes later
        // would otherwise repeat the same ~10s of work. The cache lets the modal serve
        // from the freshly-computed result instead. Cache invalidates automatically
        // when any file in the folder changes (mtime/size in the key) and on Lidarr
        // restart (in-memory).
        private readonly ICached<List<ImportDecision<LocalTrack>>> _decisionCache;
        private static readonly TimeSpan _decisionCacheTtl = TimeSpan.FromHours(2);

        public ImportDecisionMaker(IEnumerable<IImportDecisionEngineSpecification<LocalTrack>> trackSpecifications,
                                   IEnumerable<IImportDecisionEngineSpecification<LocalAlbumRelease>> albumSpecifications,
                                   IMediaFileService mediaFileService,
                                   IAudioTagService audioTagService,
                                   IAugmentingService augmentingService,
                                   IIdentificationService identificationService,
                                   IRootFolderService rootFolderService,
                                   IQualityProfileService qualityProfileService,
                                   ICacheManager cacheManager,
                                   Logger logger)
        {
            _trackSpecifications = trackSpecifications;
            _albumSpecifications = albumSpecifications;
            _mediaFileService = mediaFileService;
            _audioTagService = audioTagService;
            _augmentingService = augmentingService;
            _identificationService = identificationService;
            _rootFolderService = rootFolderService;
            _qualityProfileService = qualityProfileService;
            _decisionCache = cacheManager.GetCache<List<ImportDecision<LocalTrack>>>(GetType(), "decisions");
            _logger = logger;
        }

        public Tuple<List<LocalTrack>, List<ImportDecision<LocalTrack>>> GetLocalTracks(List<IFileInfo> musicFiles, DownloadClientItem downloadClientItem, ParsedAlbumInfo folderInfo, FilterFilesType filter)
        {
            var watch = new System.Diagnostics.Stopwatch();
            watch.Start();

            var files = _mediaFileService.FilterUnchangedFiles(musicFiles, filter);

            var localTracks = new List<LocalTrack>();
            var decisions = new List<ImportDecision<LocalTrack>>();

            _logger.Debug("Analyzing {0}/{1} files.", files.Count, musicFiles.Count);

            if (!files.Any())
            {
                return Tuple.Create(localTracks, decisions);
            }

            ParsedAlbumInfo downloadClientItemInfo = null;

            if (downloadClientItem != null)
            {
                downloadClientItemInfo = Parser.Parser.ParseAlbumTitle(downloadClientItem.Title);
            }

            var i = 1;
            foreach (var file in files)
            {
                _logger.ProgressInfo($"Reading file {i++}/{files.Count}");

                var localTrack = new LocalTrack
                {
                    DownloadClientAlbumInfo = downloadClientItemInfo,
                    FolderAlbumInfo = folderInfo,
                    Path = file.FullName,
                    Size = file.Length,
                    Modified = file.LastWriteTimeUtc,
                    FileTrackInfo = _audioTagService.ReadTags(file.FullName),
                    AdditionalFile = false
                };

                try
                {
                    // TODO fix otherfiles?
                    _augmentingService.Augment(localTrack, true);
                    localTracks.Add(localTrack);
                }
                catch (AugmentingFailedException)
                {
                    decisions.Add(new ImportDecision<LocalTrack>(localTrack, new Rejection("Unable to parse file")));
                }
                catch (Exception e)
                {
                    _logger.Error(e, "Couldn't import file. {0}", localTrack.Path);

                    decisions.Add(new ImportDecision<LocalTrack>(localTrack, new Rejection("Unexpected error processing file")));
                }
            }

            _logger.Debug($"Tags parsed for {files.Count} files in {watch.ElapsedMilliseconds}ms");

            return Tuple.Create(localTracks, decisions);
        }

        public List<ImportDecision<LocalTrack>> GetImportDecisions(List<IFileInfo> musicFiles, IdentificationOverrides idOverrides, ImportDecisionMakerInfo itemInfo, ImportDecisionMakerConfig config)
        {
            idOverrides ??= new IdentificationOverrides();
            itemInfo ??= new ImportDecisionMakerInfo();

            // Cache lookup: build a key from the inputs that determine the result, then
            // return any cached value. We don't cache when files is empty (no benefit)
            // or when identification overrides force a specific Artist/Album/Release
            // (those calls are usually one-off interactive selections where the user
            // expects fresh evaluation). Cache hits are lifecycle-safe: the decisions'
            // referenced LocalTrack/Album/AlbumRelease entities were materialized on
            // the original computation pass; they're plain CLR objects we can replay.
            string cacheKey = null;
            if (musicFiles.Count > 0 && idOverrides.Artist == null && idOverrides.Album == null && idOverrides.AlbumRelease == null)
            {
                cacheKey = BuildDecisionCacheKey(musicFiles, idOverrides, config);
                var cached = _decisionCache.Find(cacheKey);
                if (cached != null)
                {
                    _logger.Debug("Returning {0} cached decisions for {1} files (key {2}…)", cached.Count, musicFiles.Count, cacheKey.Substring(0, 8));
                    return cached;
                }
            }

            var trackData = GetLocalTracks(musicFiles, itemInfo.DownloadClientItem, itemInfo.ParsedAlbumInfo, config.Filter);
            var localTracks = trackData.Item1;
            var decisions = trackData.Item2;

            localTracks.ForEach(x => x.ExistingFile = !config.NewDownload);

            var releases = _identificationService.Identify(localTracks, idOverrides, config);

            var albums = releases.GroupBy(x => x.AlbumRelease?.Album?.Value.ForeignAlbumId);

            // group releases that are identified as the same album
            foreach (var album in albums)
            {
                var albumDecisions = new List<ImportDecision<LocalAlbumRelease>>();

                foreach (var release in album)
                {
                    // make sure the appropriate quality profile is set for the release artist
                    // in case it's a new artist
                    EnsureData(release);
                    release.NewDownload = config.NewDownload;

                    albumDecisions.Add(GetDecision(release, itemInfo.DownloadClientItem));
                }

                // if multiple album releases accepted, reject all but one with most tracks
                var acceptedReleases = albumDecisions
                    .Where(x => x.Approved)
                    .OrderByDescending(x => x.Item.TrackCount);
                foreach (var decision in acceptedReleases.Skip(1))
                {
                    decision.Reject(new Rejection("Multiple versions of an album not supported"));
                }

                foreach (var releaseDecision in albumDecisions)
                {
                    foreach (var localTrack in releaseDecision.Item.LocalTracks)
                    {
                        if (releaseDecision.Approved)
                        {
                            decisions.AddIfNotNull(GetDecision(localTrack, itemInfo.DownloadClientItem));
                        }
                        else
                        {
                            decisions.Add(new ImportDecision<LocalTrack>(localTrack, releaseDecision.Rejections.ToArray()));
                        }
                    }
                }
            }

            if (cacheKey != null)
            {
                _decisionCache.Set(cacheKey, decisions, _decisionCacheTtl);
            }

            return decisions;
        }

        // Cache key reflects every input that influences the decision result. File
        // path + length + mtime catches both "file changed" and "file replaced". Config
        // flags catch the toggles (NewDownload, IncludeExisting, etc.) that change spec
        // outcomes. We deliberately ignore itemInfo.DownloadClientItem and
        // itemInfo.ParsedAlbumInfo: the same physical files keyed identically should
        // yield the same decisions regardless of which queue record asked.
        private static string BuildDecisionCacheKey(List<IFileInfo> files, IdentificationOverrides overrides, ImportDecisionMakerConfig config)
        {
            var sb = new StringBuilder();

            foreach (var f in files.OrderBy(x => x.FullName, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append(f.FullName).Append('|')
                  .Append(f.Length).Append('|')
                  .Append(f.LastWriteTimeUtc.Ticks).Append('\n');
            }

            sb.Append("cfg|")
              .Append((int)config.Filter).Append('|')
              .Append(config.NewDownload ? '1' : '0').Append('|')
              .Append(config.SingleRelease ? '1' : '0').Append('|')
              .Append(config.IncludeExisting ? '1' : '0').Append('|')
              .Append(config.AddNewArtists ? '1' : '0');

            using var sha = SHA1.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToHexString(hash);
        }

        private void EnsureData(LocalAlbumRelease release)
        {
            if (release.AlbumRelease != null && release.AlbumRelease.Album.Value.Artist.Value.QualityProfileId == 0)
            {
                var rootFolder = _rootFolderService.GetBestRootFolder(release.LocalTracks.First().Path);
                var qualityProfile = _qualityProfileService.Get(rootFolder.DefaultQualityProfileId);

                var artist = release.AlbumRelease.Album.Value.Artist.Value;
                artist.QualityProfileId = qualityProfile.Id;
                artist.QualityProfile = qualityProfile;
            }
        }

        private bool IsLenientForClassical(Rejection rejection)
        {
            if (rejection == null)
            {
                return false;
            }

            // For classical music, ignore track count mismatches as classical albums often
            // have variations in track counts due to different performances/editions.
            // Also apply leniency for "unmatched tracks" when most tracks do match—
            // a few extra/bonus tracks shouldn't block importing the core album.
            var lenientReasons = new[]
            {
                "Track count mismatch",
                "Tracks don't match",
                "Has unmatched tracks"
            };

            return lenientReasons.Any(reason => rejection.Reason.Contains(reason, StringComparison.OrdinalIgnoreCase));
        }

        private Rejection GetCompilationRejection(LocalAlbumRelease localAlbumRelease)
        {
            // We used to reject folders whose names matched patterns like "anthology",
            // "collection", "essentials", "compilation" — that was throwing away tons of
            // legitimate single-album imports just because the album's title happened to
            // include one of those words ("The Essential Bowie", "Greatest Hits Collection",
            // etc.). Trust MusicBrainz: if we can identify the folder as a release,
            // import it regardless of marketing-flavored title words.
            //
            // True multi-album discography folders ("Artist - Discography 1970-2020") with
            // YYYY-Album subfolders are still handled correctly: each subfolder becomes
            // its own LocalAlbumRelease via Lidarr's normal grouping. A discography folder
            // with no audio files in the root never reaches this code.
            //
            // The one case we still want to suppress is a discography folder where the
            // root contains audio but no per-album subfolders — that's truly multiple
            // albums dumped in one directory and we cannot identify it as a single
            // release. Those will already fail MB lookup and get a "Couldn't find similar
            // album" rejection from the next step, which is the correct message.
            return null;
        }

        private ImportDecision<LocalAlbumRelease> GetDecision(LocalAlbumRelease localAlbumRelease, DownloadClientItem downloadClientItem)
        {
            ImportDecision<LocalAlbumRelease> decision = null;

            // Check for compilation/discography patterns first
            var compilationRejection = GetCompilationRejection(localAlbumRelease);
            if (compilationRejection != null)
            {
                decision = new ImportDecision<LocalAlbumRelease>(localAlbumRelease, compilationRejection);
            }
            else if (localAlbumRelease.AlbumRelease == null)
            {
                decision = new ImportDecision<LocalAlbumRelease>(localAlbumRelease, new Rejection($"Couldn't find similar album for {localAlbumRelease}"));
            }
            else
            {
                var reasons = _albumSpecifications.Select(c => EvaluateSpec(c, localAlbumRelease, downloadClientItem))
                    .Where(c => c != null);

                decision = new ImportDecision<LocalAlbumRelease>(localAlbumRelease, reasons.ToArray());

                // Apply lenient matching: allow unmatched tracks if most tracks do match
                // (bonus tracks, live recordings, etc shouldn't block importing the core album)
                var hasUnmatchedTracksError = decision.Rejections.Any(r => r.Reason.Contains("Has unmatched tracks", StringComparison.OrdinalIgnoreCase));
                if (hasUnmatchedTracksError && decision.Rejections.Count == 1)
                {
                    _logger.Debug("Album has unmatched tracks but matched release found, allowing import of core album");
                    decision = new ImportDecision<LocalAlbumRelease>(localAlbumRelease, Array.Empty<Rejection>());
                }

                // For classical music, apply more lenient matching criteria
                if (localAlbumRelease.IsLikelyClassical && decision.Rejections.Any())
                {
                    _logger.Debug("Album appears to be classical music, applying lenient matching");
                    decision = new ImportDecision<LocalAlbumRelease>(localAlbumRelease, reasons.Where(r => !IsLenientForClassical(r)).ToArray());
                }
            }

            if (decision == null)
            {
                _logger.Error("Unable to make a decision on {0}", localAlbumRelease);
            }
            else if (decision.Rejections.Any())
            {
                _logger.Debug("Album rejected for the following reasons: {0}", string.Join(", ", decision.Rejections));
            }
            else
            {
                _logger.Debug("Album accepted");
            }

            return decision;
        }

        private ImportDecision<LocalTrack> GetDecision(LocalTrack localTrack, DownloadClientItem downloadClientItem)
        {
            ImportDecision<LocalTrack> decision = null;

            if (localTrack.Tracks.Empty())
            {
                decision = localTrack.Album != null ? new ImportDecision<LocalTrack>(localTrack, new Rejection($"Couldn't parse track from: {localTrack.FileTrackInfo}")) :
                    new ImportDecision<LocalTrack>(localTrack, new Rejection($"Couldn't parse album from: {localTrack.FileTrackInfo}"));
            }
            else
            {
                var reasons = _trackSpecifications.Select(c => EvaluateSpec(c, localTrack, downloadClientItem))
                    .Where(c => c != null);

                decision = new ImportDecision<LocalTrack>(localTrack, reasons.ToArray());
            }

            if (decision == null)
            {
                _logger.Error("Unable to make a decision on {0}", localTrack.Path);
            }
            else if (decision.Rejections.Any())
            {
                _logger.Debug("File rejected for the following reasons: {0}", string.Join(", ", decision.Rejections));
            }
            else
            {
                _logger.Debug("File accepted");
            }

            return decision;
        }

        private Rejection EvaluateSpec<T>(IImportDecisionEngineSpecification<T> spec, T item, DownloadClientItem downloadClientItem)
        {
            try
            {
                var result = spec.IsSatisfiedBy(item, downloadClientItem);

                if (!result.Accepted)
                {
                    return new Rejection(result.Reason);
                }
            }
            catch (Exception e)
            {
                _logger.Error(e, "Couldn't evaluate decision on {0}", item);
                return new Rejection($"{spec.GetType().Name}: {e.Message}");
            }

            return null;
        }
    }
}
