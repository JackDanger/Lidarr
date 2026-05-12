using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Crypto;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles.TrackImport.Manual.Suggestions;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.MediaFiles.TrackImport.Manual
{
    public interface IManualImportService
    {
        List<ManualImportItem> GetMediaFiles(string path, string downloadId, Artist artist, FilterFilesType filter, bool replaceExistingFiles);
        List<ManualImportItem> UpdateItems(List<ManualImportItem> item);
    }

    public class ManualImportService : IExecute<ManualImportCommand>, IManualImportService
    {
        private readonly IDiskProvider _diskProvider;
        private readonly IParsingService _parsingService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IDiskScanService _diskScanService;
        private readonly IMakeImportDecision _importDecisionMaker;
        private readonly ICustomFormatCalculationService _formatCalculator;
        private readonly IArtistService _artistService;
        private readonly IAlbumService _albumService;
        private readonly IReleaseService _releaseService;
        private readonly ITrackService _trackService;
        private readonly IAudioTagService _audioTagService;
        private readonly IImportApprovedTracks _importApprovedTracks;
        private readonly ITrackedDownloadService _trackedDownloadService;
        private readonly IDownloadedTracksImportService _downloadedTracksImportService;
        private readonly IProvideImportItemService _provideImportItemService;
        private readonly IImportSuggestionService _suggestionService;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        public ManualImportService(IDiskProvider diskProvider,
                                   IParsingService parsingService,
                                   IRootFolderService rootFolderService,
                                   IDiskScanService diskScanService,
                                   IMakeImportDecision importDecisionMaker,
                                   ICustomFormatCalculationService formatCalculator,
                                   IArtistService artistService,
                                   IAlbumService albumService,
                                   IReleaseService releaseService,
                                   ITrackService trackService,
                                   IAudioTagService audioTagService,
                                   IImportApprovedTracks importApprovedTracks,
                                   ITrackedDownloadService trackedDownloadService,
                                   IDownloadedTracksImportService downloadedTracksImportService,
                                   IProvideImportItemService provideImportItemService,
                                   IImportSuggestionService suggestionService,
                                   IEventAggregator eventAggregator,
                                   Logger logger)
        {
            _diskProvider = diskProvider;
            _parsingService = parsingService;
            _rootFolderService = rootFolderService;
            _diskScanService = diskScanService;
            _importDecisionMaker = importDecisionMaker;
            _formatCalculator = formatCalculator;
            _artistService = artistService;
            _albumService = albumService;
            _releaseService = releaseService;
            _trackService = trackService;
            _audioTagService = audioTagService;
            _importApprovedTracks = importApprovedTracks;
            _trackedDownloadService = trackedDownloadService;
            _downloadedTracksImportService = downloadedTracksImportService;
            _provideImportItemService = provideImportItemService;
            _suggestionService = suggestionService;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        public List<ManualImportItem> GetMediaFiles(string path, string downloadId, Artist artist, FilterFilesType filter, bool replaceExistingFiles)
        {
            if (downloadId.IsNotNullOrWhiteSpace())
            {
                var trackedDownload = _trackedDownloadService.Find(downloadId);

                if (trackedDownload == null)
                {
                    return new List<ManualImportItem>();
                }

                if (trackedDownload.ImportItem == null)
                {
                    trackedDownload.ImportItem = _provideImportItemService.ProvideImportItem(trackedDownload.DownloadItem, trackedDownload.ImportItem);
                }

                path = trackedDownload.ImportItem.OutputPath.FullPath;
            }

            if (!_diskProvider.FolderExists(path))
            {
                if (!_diskProvider.FileExists(path))
                {
                    return new List<ManualImportItem>();
                }

                var files = new List<IFileInfo> { _diskProvider.GetFileInfo(path) };

                var config = new ImportDecisionMakerConfig
                {
                    Filter = FilterFilesType.None,
                    NewDownload = true,
                    SingleRelease = false,
                    IncludeExisting = !replaceExistingFiles,
                    AddNewArtists = false
                };

                var decisions = _importDecisionMaker.GetImportDecisions(files, null, null, config);

                if (decisions.Any())
                {
                    var result = MapItem(decisions.First(), downloadId, replaceExistingFiles, false);
                    return new List<ManualImportItem> { result };
                }

                return new List<ManualImportItem>
                {
                    new ManualImportItem()
                    {
                        Id = HashConverter.GetHashInt31(path),
                        DownloadId = downloadId,
                        Path = path,
                        Name = Path.GetFileNameWithoutExtension(path),
                        Size = _diskProvider.GetFileSize(path),
                        Rejections = new List<Rejection> { new Rejection("Unable to process file") },
                        ReplaceExistingFiles = replaceExistingFiles
                    }
                };
            }

            return ProcessFolder(path, downloadId, artist, filter, replaceExistingFiles);
        }

        private List<ManualImportItem> ProcessFolder(string folder, string downloadId, Artist artist, FilterFilesType filter, bool replaceExistingFiles)
        {
            DownloadClientItem downloadClientItem = null;
            var directoryInfo = new DirectoryInfo(folder);

            try
            {
                artist ??= _parsingService.GetArtist(directoryInfo.Name);
            }
            catch (MultipleArtistsFoundException e)
            {
                _logger.Warn(e, "Unable to find artist from title");
            }

            if (downloadId.IsNotNullOrWhiteSpace())
            {
                var trackedDownload = _trackedDownloadService.Find(downloadId);
                downloadClientItem = trackedDownload.DownloadItem;

                if (artist == null)
                {
                    artist = trackedDownload.RemoteAlbum?.Artist;
                }
            }

            var artistFiles = _diskScanService.GetAudioFiles(folder).ToList();

            // No audio files at all → don't return empty (the modal would say "No audio
            // files found" and the user has no idea what's actually in the folder).
            // Surface every non-audio file with a descriptive rejection so the user can
            // see whether it's an archive that needs extracting, a Blu-ray ISO, a video,
            // a `.nzb` leftover, etc. — and decide what to do.
            if (artistFiles.Count == 0)
            {
                return BuildNonAudioInventory(folder, downloadId, replaceExistingFiles);
            }

            if (artist == null && artistFiles.Count > 100)
            {
                _logger.Warn("Unable to determine artist from folder name and found more than 100 files. Skipping parsing");
                return ProcessDownloadDirectory(folder, artistFiles);
            }

            var idOverrides = new IdentificationOverrides
            {
                Artist = artist
            };
            var itemInfo = new ImportDecisionMakerInfo
            {
                DownloadClientItem = downloadClientItem,
                ParsedAlbumInfo = Parser.Parser.ParseAlbumTitle(directoryInfo.Name)
            };
            var config = new ImportDecisionMakerConfig
            {
                Filter = filter,
                NewDownload = true,
                SingleRelease = false,
                IncludeExisting = !replaceExistingFiles,
                AddNewArtists = false
            };

            var decisions = _importDecisionMaker.GetImportDecisions(artistFiles, idOverrides, itemInfo, config);

            // paths will be different for new and old files which is why we need to map separately
            var newFiles = artistFiles.Join(decisions,
                                            f => f.FullName,
                                            d => d.Item.Path,
                                            (f, d) => new { File = f, Decision = d },
                                            PathEqualityComparer.Instance);

            var newItems = newFiles.Select(x => MapItem(x.Decision, downloadId, replaceExistingFiles, false)).ToList();
            var existingDecisions = decisions.Except(newFiles.Select(x => x.Decision));
            var existingItems = existingDecisions.Select(x => MapItem(x, null, replaceExistingFiles, false)).ToList();

            var combined = newItems.Concat(existingItems).ToList();
            AttachSuggestions(combined);
            return combined;
        }

        // Compute one MB suggestion per (ArtistTitle, AlbumTitle) tag tuple
        // among unmatched items and broadcast it to every item in that group.
        // Single MB lookup per group keeps modal-open latency bounded even for
        // boxed-set folders. Items that already matched cleanly (no rejection)
        // are skipped — Lidarr's auto-identification got it right and there's
        // nothing to suggest. Suggestion lookup failures are swallowed: the
        // feature degrades to today's behaviour rather than breaking the modal.
        private void AttachSuggestions(List<ManualImportItem> items)
        {
            if (items.Count == 0)
            {
                return;
            }

            // Trigger when ANY rejection is present — we want to surface a
            // suggestion both for "couldn't find anything" (Album == null) and
            // for "found something but match was way off" (Album populated,
            // rejection from CloseAlbumMatchSpecification). The latter is the
            // user's stated target: "if the match is way off, then we all
            // automatically look at music brains right when the modal opens".
            // Frontend decides how to render in each case.
            var groups = items
                .Where(i => i.Tags != null && i.Rejections != null && i.Rejections.Any())
                .GroupBy(i => (
                    artist: i.Tags.ArtistTitle ?? string.Empty,
                    album: i.Tags.AlbumTitle ?? string.Empty),
                    StringTupleComparer);

            foreach (var group in groups)
            {
                if (string.IsNullOrWhiteSpace(group.Key.album))
                {
                    continue;
                }

                var localTracks = group
                    .Select(MakeLocalTrackForScoring)
                    .Where(t => t != null)
                    .ToList();

                if (localTracks.Count == 0)
                {
                    continue;
                }

                Suggestions.ImportSuggestion suggestion;
                try
                {
                    suggestion = _suggestionService.FindForTracks(localTracks);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Suggestion lookup threw for '{0}' / '{1}'", group.Key.artist, group.Key.album);
                    continue;
                }

                if (suggestion == null)
                {
                    continue;
                }

                foreach (var item in group)
                {
                    item.Suggestion = suggestion;
                }
            }
        }

        // We can't pass ManualImportItem to a scoring service that wants
        // LocalTrack — build a thin one from the tags. Duration ends up as 0
        // for items where the tag reader didn't capture it; the scorer copes.
        private static LocalTrack MakeLocalTrackForScoring(ManualImportItem item)
        {
            if (item.Tags == null)
            {
                return null;
            }

            return new LocalTrack
            {
                Path = item.Path,
                Size = item.Size,
                FileTrackInfo = item.Tags,
            };
        }

        private static readonly StringTupleComparerImpl StringTupleComparer = new StringTupleComparerImpl();

        private sealed class StringTupleComparerImpl : IEqualityComparer<(string artist, string album)>
        {
            public bool Equals((string artist, string album) x, (string artist, string album) y)
            {
                return string.Equals(x.artist, y.artist, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.album, y.album, StringComparison.OrdinalIgnoreCase);
            }

            public int GetHashCode((string artist, string album) obj)
            {
                return StringComparer.OrdinalIgnoreCase.GetHashCode(obj.artist ?? string.Empty)
                    ^ StringComparer.OrdinalIgnoreCase.GetHashCode(obj.album ?? string.Empty);
            }
        }

        private List<ManualImportItem> ProcessDownloadDirectory(string folder, List<IFileInfo> audioFiles)
        {
            var items = new List<ManualImportItem>();

            foreach (var file in audioFiles)
            {
                var localTrack = new LocalTrack();
                localTrack.Path = file.FullName;
                localTrack.Quality = new QualityModel(Quality.Unknown);
                localTrack.Size = file.Length;

                items.Add(MapItem(new ImportDecision<LocalTrack>(localTrack), null, false, false));
            }

            return items;
        }

        // Folder had no audio files. Walk the directory and surface every other file
        // with a descriptive rejection so the user can see what they're dealing with —
        // archive that needs extracting, Blu-ray ISO, video file, .nzb leftover, etc.
        // Cheap: a single recursive directory listing, no tag reading, no MB lookups.
        // Returns at most ~200 entries to keep the modal usable for pathological folders
        // (Blu-ray BDMV trees can have hundreds of small files).
        private List<ManualImportItem> BuildNonAudioInventory(string folder, string downloadId, bool replaceExistingFiles)
        {
            const int maxItems = 200;

            List<string> allFiles;
            try
            {
                allFiles = _diskProvider.GetFiles(folder, true).ToList();
            }
            catch (DirectoryNotFoundException)
            {
                return new List<ManualImportItem>();
            }
            catch (UnauthorizedAccessException)
            {
                return new List<ManualImportItem>();
            }

            if (allFiles == null || allFiles.Count == 0)
            {
                _logger.Debug("Folder {0} contains no files at all", folder);
                return new List<ManualImportItem>();
            }

            var items = new List<ManualImportItem>(Math.Min(allFiles.Count, maxItems));
            var truncatedCount = Math.Max(0, allFiles.Count - maxItems);

            foreach (var path in allFiles.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (items.Count >= maxItems)
                {
                    items.Add(new ManualImportItem
                    {
                        Id = HashConverter.GetHashInt31(folder + "/__truncated__"),
                        DownloadId = downloadId,
                        Path = folder,
                        Name = $"... and {truncatedCount} more files (not shown)",
                        Size = 0,
                        Rejections = new List<Rejection> { new Rejection("Truncated for display") },
                        ReplaceExistingFiles = replaceExistingFiles
                    });
                    break;
                }

                long size;
                try
                {
                    size = _diskProvider.GetFileSize(path);
                }
                catch
                {
                    size = 0;
                }

                items.Add(new ManualImportItem
                {
                    Id = HashConverter.GetHashInt31(path),
                    DownloadId = downloadId,
                    Path = path,
                    Name = Path.GetFileName(path),
                    Size = size,
                    Rejections = new List<Rejection> { new Rejection(DescribeNonAudioFile(path)) },
                    ReplaceExistingFiles = replaceExistingFiles
                });
            }

            _logger.Debug("Folder {0} had no audio files; returning {1} non-audio entries for manual inspection", folder, items.Count);
            return items;
        }

        private static string DescribeNonAudioFile(string path)
        {
            var ext = Path.GetExtension(path);

            if (FileExtensions.ArchiveExtensions.Contains(ext))
            {
                return $"Archive file ({ext}) — extract before importing";
            }

            if (FileExtensions.ExecutableExtensions.Contains(ext))
            {
                return $"Executable file ({ext}) — not safe to import";
            }

            if (string.Equals(ext, ".iso", StringComparison.OrdinalIgnoreCase))
            {
                return "ISO disc image — extract or mount before importing";
            }

            if (_videoExtensions.Contains(ext))
            {
                return $"Video file ({ext}) — Lidarr only imports audio";
            }

            if (_metadataExtensions.Contains(ext))
            {
                return $"Metadata/sidecar file ({ext}) — nothing to import";
            }

            return string.IsNullOrEmpty(ext)
                ? "File has no extension — Lidarr can't determine its type"
                : $"Unsupported file type: {ext}";
        }

        private static readonly HashSet<string> _videoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".avi", ".mov", ".m4v", ".webm", ".wmv", ".flv", ".mpg", ".mpeg", ".ts", ".m2ts", ".vob", ".bdmv", ".clpi", ".mpls"
        };

        private static readonly HashSet<string> _metadataExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".nzb", ".torrent", ".par2", ".sfv", ".nfo", ".m3u", ".m3u8", ".cue", ".log", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".pdf", ".txt", ".xml", ".bdjo", ".bdj"
        };

        public List<ManualImportItem> UpdateItems(List<ManualImportItem> items)
        {
            var replaceExistingFiles = items.All(x => x.ReplaceExistingFiles);
            var groupedItems = items.Where(x => !x.AdditionalFile).GroupBy(x => x.Album?.Id);
            _logger.Debug($"UpdateItems, {groupedItems.Count()} groups, replaceExisting {replaceExistingFiles}");

            var result = new List<ManualImportItem>();

            foreach (var group in groupedItems)
            {
                _logger.Debug("UpdateItems, group key: {0}", group.Key);

                var disableReleaseSwitching = group.First().DisableReleaseSwitching;

                var files = group.Select(x => _diskProvider.GetFileInfo(x.Path)).ToList();
                var idOverride = new IdentificationOverrides
                {
                    Artist = group.First().Artist,
                    Album = group.First().Album,
                    AlbumRelease = group.First().Release
                };
                var config = new ImportDecisionMakerConfig
                {
                    Filter = FilterFilesType.None,
                    NewDownload = true,
                    SingleRelease = true,
                    IncludeExisting = !replaceExistingFiles,
                    AddNewArtists = false
                };
                var decisions = _importDecisionMaker.GetImportDecisions(files, idOverride, null, config);

                var existingItems = group.Join(decisions,
                                               i => i.Path,
                                               d => d.Item.Path,
                                               (i, d) => new { Item = i, Decision = d },
                                               PathEqualityComparer.Instance);

                foreach (var pair in existingItems)
                {
                    var item = pair.Item;
                    var decision = pair.Decision;

                    if (decision.Item.Artist != null)
                    {
                        item.Artist = decision.Item.Artist;
                    }

                    if (decision.Item.Album != null)
                    {
                        item.Album = decision.Item.Album;
                        item.Release = decision.Item.Release;
                    }

                    if (decision.Item.Tracks.Any())
                    {
                        item.Tracks = decision.Item.Tracks;
                    }

                    if (item.Quality?.Quality == Quality.Unknown)
                    {
                        item.Quality = decision.Item.Quality;
                    }

                    if (item.ReleaseGroup.IsNullOrWhiteSpace())
                    {
                        item.ReleaseGroup = decision.Item.ReleaseGroup;
                    }

                    item.Rejections = decision.Rejections;
                    item.Size = decision.Item.Size;

                    result.Add(item);
                }

                var newDecisions = decisions.Except(existingItems.Select(x => x.Decision));
                result.AddRange(newDecisions.Select(x => MapItem(x, null, replaceExistingFiles, disableReleaseSwitching)));
            }

            return result;
        }

        private ManualImportItem MapItem(ImportDecision<LocalTrack> decision, string downloadId, bool replaceExistingFiles, bool disableReleaseSwitching)
        {
            var item = new ManualImportItem();

            item.Id = HashConverter.GetHashInt31(decision.Item.Path);
            item.Path = decision.Item.Path;
            item.Name = Path.GetFileNameWithoutExtension(decision.Item.Path);
            item.DownloadId = downloadId;

            if (decision.Item.Artist != null)
            {
                item.Artist = decision.Item.Artist;

                item.CustomFormats = _formatCalculator.ParseCustomFormat(decision.Item);
            }

            if (decision.Item.Album != null)
            {
                item.Album = decision.Item.Album;
                item.Release = decision.Item.Release;
            }

            if (decision.Item.Tracks.Any())
            {
                item.Tracks = decision.Item.Tracks;
            }

            item.Quality = decision.Item.Quality;
            item.IndexerFlags = (int)decision.Item.IndexerFlags;
            item.Size = _diskProvider.GetFileSize(decision.Item.Path);
            item.Rejections = decision.Rejections;
            item.Tags = decision.Item.FileTrackInfo;
            item.AdditionalFile = decision.Item.AdditionalFile;
            item.ReplaceExistingFiles = replaceExistingFiles;
            item.DisableReleaseSwitching = disableReleaseSwitching;

            return item;
        }

        public void Execute(ManualImportCommand message)
        {
            _logger.ProgressTrace("Manually importing {0} files using mode {1}", message.Files.Count, message.ImportMode);

            var imported = new List<ImportResult>();
            var importedTrackedDownload = new List<ManuallyImportedFile>();
            var albumIds = message.Files.GroupBy(e => e.AlbumId).ToList();
            var fileCount = 0;

            foreach (var importAlbumId in albumIds)
            {
                var albumImportDecisions = new List<ImportDecision<LocalTrack>>();

                // turn off anyReleaseOk if specified
                if (importAlbumId.First().DisableReleaseSwitching)
                {
                    var album = _albumService.GetAlbum(importAlbumId.First().AlbumId);
                    album.AnyReleaseOk = false;
                    _albumService.UpdateAlbum(album);
                }

                foreach (var file in importAlbumId)
                {
                    _logger.ProgressTrace("Processing file {0} of {1}", fileCount + 1, message.Files.Count);

                    var artist = _artistService.GetArtist(file.ArtistId);
                    var album = _albumService.GetAlbum(file.AlbumId);
                    var release = _releaseService.GetRelease(file.AlbumReleaseId);
                    var tracks = _trackService.GetTracks(file.TrackIds);
                    var fileTrackInfo = _audioTagService.ReadTags(file.Path) ?? new ParsedTrackInfo();
                    var fileInfo = _diskProvider.GetFileInfo(file.Path);

                    var localTrack = new LocalTrack
                    {
                        ExistingFile = artist.Path.IsParentPath(file.Path),
                        Tracks = tracks,
                        FileTrackInfo = fileTrackInfo,
                        Path = file.Path,
                        Size = fileInfo.Length,
                        Modified = fileInfo.LastWriteTimeUtc,
                        Quality = file.Quality,
                        IndexerFlags = (IndexerFlags)file.IndexerFlags,
                        Artist = artist,
                        Album = album,
                        Release = release
                    };

                    var importDecision = new ImportDecision<LocalTrack>(localTrack);
                    if (_rootFolderService.GetBestRootFolder(artist.Path) == null)
                    {
                        _logger.Warn($"Destination artist folder {artist.Path} not in a Root Folder, skipping import");
                        importDecision.Reject(new Rejection($"Destination artist folder {artist.Path} is not in a Root Folder"));
                    }

                    albumImportDecisions.Add(importDecision);
                    fileCount += 1;
                }

                var downloadId = importAlbumId.Select(x => x.DownloadId).FirstOrDefault(x => x.IsNotNullOrWhiteSpace());
                if (downloadId.IsNullOrWhiteSpace())
                {
                    imported.AddRange(_importApprovedTracks.Import(albumImportDecisions, message.ReplaceExistingFiles, null, message.ImportMode));
                }
                else
                {
                    var trackedDownload = _trackedDownloadService.Find(downloadId);
                    var importResults = _importApprovedTracks.Import(albumImportDecisions, message.ReplaceExistingFiles, trackedDownload.DownloadItem, message.ImportMode);

                    imported.AddRange(importResults);

                    foreach (var importResult in importResults)
                    {
                        importedTrackedDownload.Add(new ManuallyImportedFile
                        {
                            TrackedDownload = trackedDownload,
                            ImportResult = importResult
                        });
                    }
                }
            }

            _logger.ProgressTrace("Manually imported {0} files", imported.Count);

            foreach (var groupedTrackedDownload in importedTrackedDownload.GroupBy(i => i.TrackedDownload.DownloadItem.DownloadId).ToList())
            {
                var trackedDownload = groupedTrackedDownload.First().TrackedDownload;
                var importArtist = groupedTrackedDownload.First().ImportResult.ImportDecision.Item.Artist;
                var outputPath = trackedDownload.ImportItem.OutputPath.FullPath;

                if (_diskProvider.FolderExists(outputPath))
                {
                    if (_downloadedTracksImportService.ShouldDeleteFolder(
                            _diskProvider.GetDirectoryInfo(outputPath), importArtist) &&
                        trackedDownload.DownloadItem.CanMoveFiles)
                    {
                        _diskProvider.DeleteFolder(outputPath, true);
                    }
                }

                var remoteTrackCount = Math.Max(1, trackedDownload.RemoteAlbum?.Albums.Sum(x => x.AlbumReleases.Value.Where(y => y.Monitored).Sum(z => z.TrackCount)) ?? 1);

                var importResults = groupedTrackedDownload.Select(c => c.ImportResult).ToList();
                var importedTrackCount = importResults.Where(c => c.Result == ImportResultType.Imported)
                    .SelectMany(c => c.ImportDecision.Item.Tracks)
                    .Count();

                var allTracksImported = (importResults.Any() && importResults.All(c => c.Result == ImportResultType.Imported)) || importedTrackCount >= remoteTrackCount;

                if (allTracksImported)
                {
                    trackedDownload.State = TrackedDownloadState.Imported;
                    _eventAggregator.PublishEvent(new DownloadCompletedEvent(trackedDownload, importArtist.Id));
                }
            }
        }
    }
}
