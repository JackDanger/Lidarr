namespace NzbDrone.Core.MediaFiles.Extraction
{
    // One implementation per archive family. Registered via DI; ExtractionService
    // collects all and picks the first whose CanHandle returns true.
    public interface IArchiveExtractor
    {
        // Short identifier for log lines ("tar", "zip", "rar", ...).
        string Name { get; }

        // True iff this extractor recognises and can extract the file at archivePath.
        // Implementations should also return false for non-lead volumes of multi-volume
        // archives (e.g. .r01 is not a lead; .rar / .part01.rar / .part001.rar are).
        bool CanHandle(string archivePath);

        // Extract `archivePath` into `destinationFolder` (typically the directory the
        // archive lives in). Returns true on success. Implementations should not throw
        // on failure — log and return false so the rest of the pipeline can continue.
        bool Extract(string archivePath, string destinationFolder);

        // Quick health check on startup: does this extractor have what it needs to run
        // (binary on PATH, library available, etc). False means we won't even try to
        // hand archives to it — useful so a missing `unrar` doesn't crash imports.
        bool IsAvailable();
    }
}
