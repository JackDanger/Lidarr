namespace NzbDrone.Core.MediaFiles.Extraction
{
    public interface IExtractionService
    {
        // Walks the folder for archives we know how to handle and extracts each
        // in place. Idempotent: per-archive marker file (`<archive>.lidarr-extracted`)
        // means the second call no-ops cheaply. Returns true iff at least one new
        // extraction occurred (caller may want to re-scan for newly-appeared audio).
        bool ExtractIfNeeded(string folder);
    }
}
