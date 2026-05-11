using System.IO.Compression;

namespace NzbDrone.Core.MediaFiles.Extraction
{
    // Handles .zip via System.IO.Compression. No external binary needed.
    public class ZipExtractor : IArchiveExtractor
    {
        public string Name => "zip";

        public bool IsAvailable() => true;

        public bool CanHandle(string archivePath)
        {
            return archivePath.ToLowerInvariant().EndsWith(".zip");
        }

        public bool Extract(string archivePath, string destinationFolder)
        {
            ZipFile.ExtractToDirectory(archivePath, destinationFolder, overwriteFiles: true);
            return true;
        }
    }
}
