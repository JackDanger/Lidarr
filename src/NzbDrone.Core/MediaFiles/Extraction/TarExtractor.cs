using System;
using System.IO;
using System.Formats.Tar;
using System.IO.Compression;
using NLog;

namespace NzbDrone.Core.MediaFiles.Extraction
{
    // Handles .tar (and .tar.gz / .tgz). Uses .NET 8's built-in System.Formats.Tar
    // so there's no external binary dependency for the most common case in the
    // user's library (~100 .tar files at time of writing).
    public class TarExtractor : IArchiveExtractor
    {
        private readonly Logger _logger;

        public TarExtractor(Logger logger)
        {
            _logger = logger;
        }

        public string Name => "tar";

        public bool IsAvailable() => true; // built-in to .NET 8

        public bool CanHandle(string archivePath)
        {
            var lower = archivePath.ToLowerInvariant();
            return lower.EndsWith(".tar")
                || lower.EndsWith(".tar.gz")
                || lower.EndsWith(".tgz");
        }

        public bool Extract(string archivePath, string destinationFolder)
        {
            try
            {
                using var fileStream = File.OpenRead(archivePath);
                Stream readStream = fileStream;

                var lower = archivePath.ToLowerInvariant();
                if (lower.EndsWith(".tar.gz") || lower.EndsWith(".tgz"))
                {
                    readStream = new GZipStream(fileStream, CompressionMode.Decompress);
                }

                // overwriteFiles: true so re-running over a partial extraction completes
                // (we don't want to leave half-finished sets due to a stale half-file).
                TarFile.ExtractToDirectory(readStream, destinationFolder, overwriteFiles: true);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "TarExtractor: failed extracting {0}", archivePath);
                return false;
            }
        }
    }
}
