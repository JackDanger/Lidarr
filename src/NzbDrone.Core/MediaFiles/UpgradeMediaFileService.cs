using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.MediaFiles.TrackImport;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles
{
    public interface IUpgradeMediaFiles
    {
        TrackFileMoveResult UpgradeTrackFile(TrackFile trackFile, LocalTrack localTrack, bool copyOnly = false);
    }

    public class UpgradeMediaFileService : IUpgradeMediaFiles
    {
        private readonly IRecycleBinProvider _recycleBinProvider;
        private readonly IMediaFileService _mediaFileService;
        private readonly IAudioTagService _audioTagService;
        private readonly IMoveTrackFiles _trackFileMover;
        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;

        public UpgradeMediaFileService(IRecycleBinProvider recycleBinProvider,
                                       IMediaFileService mediaFileService,
                                       IAudioTagService audioTagService,
                                       IMoveTrackFiles trackFileMover,
                                       IDiskProvider diskProvider,
                                       Logger logger)
        {
            _recycleBinProvider = recycleBinProvider;
            _mediaFileService = mediaFileService;
            _audioTagService = audioTagService;
            _trackFileMover = trackFileMover;
            _diskProvider = diskProvider;
            _logger = logger;
        }

        public TrackFileMoveResult UpgradeTrackFile(TrackFile trackFile, LocalTrack localTrack, bool copyOnly = false)
        {
            var moveFileResult = new TrackFileMoveResult();
            var existingFiles = localTrack.Tracks
                                            .Where(e => e.TrackFileId > 0)
                                            .Select(e => e.TrackFile.Value)
                                            .Where(e => e != null)
                                            .GroupBy(e => e.Id)
                                            .ToList();

            var rootFolder = _diskProvider.GetParentFolder(localTrack.Artist.Path);

            // If there are existing track files and the root folder is missing, throw, so the old file isn't left behind during the import process.
            if (existingFiles.Any() && !_diskProvider.FolderExists(rootFolder))
            {
                throw new RootFolderNotFoundException($"Root folder '{rootFolder}' was not found.");
            }

            // No-op short-circuit: if there's exactly one existing file, the source path is
            // also that file (or matches it byte-for-byte by size+mtime), there's nothing
            // to do. Without this guard, repeated import attempts (which happen freely
            // when one downloadId expands to many queue records) would delete and re-copy
            // the same content over and over, generating spurious "Existing track file
            // missing from disk" warnings as later iterations race against earlier ones.
            if (existingFiles.Count == 1)
            {
                var existing = existingFiles[0].First();
                if (IsSameFileOnDisk(existing.Path, localTrack.Path, localTrack.Size))
                {
                    _logger.Debug("Skipping upgrade: {0} already present at destination with same size/mtime", existing.Path);
                    moveFileResult.TrackFile = existing;
                    return moveFileResult;
                }
            }

            foreach (var existingFile in existingFiles)
            {
                var file = existingFile.First();
                var trackFilePath = file.Path;
                var subfolder = rootFolder.GetRelativePath(_diskProvider.GetParentFolder(trackFilePath));

                if (_diskProvider.FileExists(trackFilePath))
                {
                    _logger.Debug("Removing existing track file: {0}", file);
                    _recycleBinProvider.DeleteFile(trackFilePath, subfolder);
                }
                else
                {
                    // Demoted from Warn to Debug: this fires when an earlier iteration in
                    // the same import batch already deleted the file (multiple queue
                    // records pointing at the same physical content). Not actionable for
                    // the user; the new file lands at the destination either way.
                    _logger.Debug("Existing track file already gone from disk (likely deleted earlier in same import pass): {0}", trackFilePath);
                }

                moveFileResult.OldFiles.Add(file);
                _mediaFileService.Delete(file, DeleteMediaFileReason.Upgrade);
            }

            if (copyOnly)
            {
                moveFileResult.TrackFile = _trackFileMover.CopyTrackFile(trackFile, localTrack);
            }
            else
            {
                moveFileResult.TrackFile = _trackFileMover.MoveTrackFile(trackFile, localTrack);
            }

            _audioTagService.WriteTags(trackFile, true);

            return moveFileResult;
        }

        private bool IsSameFileOnDisk(string existingPath, string sourcePath, long sourceSize)
        {
            // Same path → trivially the same file. Either way we just placed it; no work to do.
            if (string.Equals(existingPath, sourcePath, System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Different paths but same size + matching disk presence is a strong enough
            // signal for the duplicate-import case (we've already copied this file once
            // and a second queue record for the same downloadId is asking us to do it
            // again). We don't hash because tracks can be 30+ MB and this runs in the
            // hot import loop.
            if (!_diskProvider.FileExists(existingPath))
            {
                return false;
            }

            try
            {
                return _diskProvider.GetFileSize(existingPath) == sourceSize;
            }
            catch
            {
                return false;
            }
        }
    }
}
