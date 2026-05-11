using System;
using System.IO;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Processes;

namespace NzbDrone.Core.MediaFiles.Extraction
{
    // Handles .iso (and .img) via the `isomage` binary
    // (https://github.com/JackDanger/isomage). isomage parses ISO 9660 (with Joliet
    // and Rock Ridge extensions) and UDF, so it covers CD / DVD / Blu-ray images
    // without mounting them — useful in unprivileged LXC where loopback mounts
    // aren't an option.
    //
    // .img is overloaded: optical-disc images carry an ISO/UDF filesystem and
    // isomage handles them; raw block-device dumps don't, and isomage will exit
    // non-zero in that case. We let it try and fall through to ImportBlocked on
    // failure rather than guessing the .img sub-flavour up front.
    public class IsoExtractor : IArchiveExtractor
    {
        private readonly IProcessProvider _processProvider;
        private readonly Logger _logger;
        private readonly bool _available;

        public IsoExtractor(IProcessProvider processProvider, Logger logger)
        {
            _processProvider = processProvider;
            _logger = logger;
            _available = ProbeAvailable();
        }

        public string Name => "iso";

        public bool IsAvailable() => _available;

        public bool CanHandle(string archivePath)
        {
            var lower = archivePath.ToLowerInvariant();
            return lower.EndsWith(".iso") || lower.EndsWith(".img");
        }

        public bool Extract(string archivePath, string destinationFolder)
        {
            // `-x /` selects the disc root; `-o <dir>` is the destination. isomage
            // recreates the disc tree underneath that destination. No overwrite flag
            // is documented — the .lidarr-extracted marker prevents re-runs, so a
            // mid-extract failure is the only path that re-enters here, and a
            // half-extracted folder is acceptable for a personal fork (operator
            // deletes partial files and clears the marker manually).
            var args = $"-x / -o \"{destinationFolder}\" \"{archivePath}\"";
            var output = _processProvider.StartAndCapture("isomage", args);
            if (output.ExitCode == 0)
            {
                return true;
            }

            _logger.Warn(
                "isomage exited {0} for {1}: {2}",
                output.ExitCode,
                archivePath,
                output.Lines);
            return false;
        }

        // Same PATH-walk pattern as RarExtractor — cheaper than invoking the binary
        // and avoids spurious Error log lines from probe output.
        private static bool ProbeAvailable()
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathEnv))
            {
                return false;
            }

            var binaryName = OsInfo.IsWindows ? "isomage.exe" : "isomage";

            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir))
                {
                    continue;
                }

                try
                {
                    if (File.Exists(Path.Combine(dir, binaryName)))
                    {
                        return true;
                    }
                }
                catch (Exception)
                {
                    // Bad entry in PATH; ignore and continue.
                }
            }

            return false;
        }
    }
}
