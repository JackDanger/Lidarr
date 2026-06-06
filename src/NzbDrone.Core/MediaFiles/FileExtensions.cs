using System;
using System.Collections.Generic;

namespace NzbDrone.Core.MediaFiles
{
    internal static class FileExtensions
    {
        private static List<string> _archiveExtensions = new List<string>
        {
            ".7z",
            ".bz2",
            ".gz",
            ".r00",
            ".rar",
            ".tar.bz2",
            ".tar.gz",
            ".tar",
            ".tb2",
            ".tbz2",
            ".tgz",
            ".zip",
            ".zipx"
        };

        private static List<string> _executableExtensions = new List<string>
        {
            ".exe",
            ".bat",
            ".cmd",
            ".sh"
        };

        // Video container/structure extensions. Lidarr only imports audio, so a download
        // made up of these (and no audio) is a music-video / concert grab — see
        // CompletedDownloadService's video-only handling and ManualImportService's
        // per-file "why can't I import this" reasons. Kept here so the two callers can't
        // drift out of sync.
        private static List<string> _videoExtensions = new List<string>
        {
            ".mp4", ".mkv", ".avi", ".mov", ".m4v", ".webm", ".wmv", ".flv",
            ".mpg", ".mpeg", ".ts", ".m2ts", ".vob", ".bdmv", ".clpi", ".mpls", ".divx", ".3gp"
        };

        public static HashSet<string> ArchiveExtensions => new HashSet<string>(_archiveExtensions, StringComparer.OrdinalIgnoreCase);
        public static HashSet<string> ExecutableExtensions => new HashSet<string>(_executableExtensions, StringComparer.OrdinalIgnoreCase);
        public static HashSet<string> VideoExtensions => new HashSet<string>(_videoExtensions, StringComparer.OrdinalIgnoreCase);
    }
}
