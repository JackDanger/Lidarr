using System.Formats.Tar;
using System.IO;
using System.IO.Compression;

namespace NzbDrone.Core.MediaFiles.Extraction
{
    // Handles .tar (and .tar.gz / .tgz) via .NET 8's built-in System.Formats.Tar
    // — no external binary dependency for the most common case in the user's
    // library (~100 .tar files at time of writing).
    public class TarExtractor : IArchiveExtractor
    {
        public string Name => "tar";

        public bool IsAvailable() => true;

        public bool CanHandle(string archivePath)
        {
            var lower = archivePath.ToLowerInvariant();
            return lower.EndsWith(".tar")
                || lower.EndsWith(".tar.gz")
                || lower.EndsWith(".tgz");
        }

        public bool Extract(string archivePath, string destinationFolder)
        {
            using var fileStream = File.OpenRead(archivePath);
            Stream readStream = fileStream;

            var lower = archivePath.ToLowerInvariant();
            if (lower.EndsWith(".tar.gz") || lower.EndsWith(".tgz"))
            {
                readStream = new GZipStream(fileStream, CompressionMode.Decompress);
            }

            // idempotent re-run over a partial extraction
            TarFile.ExtractToDirectory(readStream, destinationFolder, overwriteFiles: true);
            return true;
        }
    }
}
