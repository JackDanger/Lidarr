using System;
using System.IO.Compression;
using NLog;

namespace NzbDrone.Core.MediaFiles.Extraction
{
    // Handles .zip via System.IO.Compression. No external binary needed.
    public class ZipExtractor : IArchiveExtractor
    {
        private readonly Logger _logger;

        public ZipExtractor(Logger logger)
        {
            _logger = logger;
        }

        public string Name => "zip";

        public bool IsAvailable() => true;

        public bool CanHandle(string archivePath)
        {
            return archivePath.ToLowerInvariant().EndsWith(".zip");
        }

        public bool Extract(string archivePath, string destinationFolder)
        {
            try
            {
                ZipFile.ExtractToDirectory(archivePath, destinationFolder, overwriteFiles: true);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "ZipExtractor: failed extracting {0}", archivePath);
                return false;
            }
        }
    }
}
