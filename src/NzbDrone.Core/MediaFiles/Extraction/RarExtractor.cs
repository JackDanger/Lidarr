using System;
using System.IO;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Processes;

namespace NzbDrone.Core.MediaFiles.Extraction
{
    // Handles .rar via the `unrar` binary. No managed library because the .NET rar
    // libraries are split between proprietary and abandoned forks; shelling to unrar
    // is the same approach Lidarr uses elsewhere for media tools.
    //
    // Multi-volume handling: only the LEAD volume is offered to Extract (CanHandle
    // returns false for non-leads). Single .rar; .partNN.rar series; or split sets
    // .rar/.r00/.r01 — `unrar x` on the lead handles all volumes automatically.
    public class RarExtractor : IArchiveExtractor
    {
        private static readonly Regex PartVolumePattern = new Regex(@"\.part(\d+)\.rar$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex OldStyleVolumePattern = new Regex(@"\.r(\d{2,3})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly IProcessProvider _processProvider;
        private readonly Logger _logger;
        private readonly bool _available;

        public RarExtractor(IProcessProvider processProvider, Logger logger)
        {
            _processProvider = processProvider;
            _logger = logger;
            _available = ProbeAvailable();
        }

        public string Name => "rar";

        public bool IsAvailable() => _available;

        public bool CanHandle(string archivePath)
        {
            var lower = archivePath.ToLowerInvariant();

            // .partNN.rar — only the first part is the lead.
            var partMatch = PartVolumePattern.Match(lower);
            if (partMatch.Success)
            {
                return int.Parse(partMatch.Groups[1].Value) == 1;
            }

            // .r00 / .r01 / ... — never a lead. Lead is the .rar of the same name.
            if (OldStyleVolumePattern.IsMatch(lower))
            {
                return false;
            }

            return lower.EndsWith(".rar");
        }

        public bool Extract(string archivePath, string destinationFolder)
        {
            try
            {
                // `unrar x -y <archive> <dest>/` — extract with full paths, assume yes
                // to prompts, destination trailing-slash makes unrar treat it as dir.
                // Note: unrar-free (Debian default) doesn't recognise `-o+`, but it
                // overwrites by default; the proprietary unrar accepts both.
                var destWithSlash = destinationFolder.TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
                var args = $"x -y \"{archivePath}\" \"{destWithSlash}\"";
                var output = _processProvider.StartAndCapture("unrar", args);
                if (output.ExitCode == 0)
                {
                    return true;
                }

                _logger.Warn(
                    "unrar exited {0} for {1}: {2}",
                    output.ExitCode,
                    archivePath,
                    output.Lines);
                return false;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "RarExtractor: failed extracting {0}", archivePath);
                return false;
            }
        }

        // Cheap PATH walk to confirm `unrar` is callable. Avoids invoking the binary
        // (any invocation produces stderr that shows up as Error log lines) and
        // covers both proprietary `unrar` and Debian's `unrar-free`.
        private static bool ProbeAvailable()
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathEnv))
            {
                return false;
            }

            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir))
                {
                    continue;
                }

                try
                {
                    if (File.Exists(Path.Combine(dir, "unrar")))
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
