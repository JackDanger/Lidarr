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
                // unrar x  -y     extract with full paths, assume yes to all prompts
                //          -o+    overwrite existing files
                // Path the destination ends with a slash so unrar treats it as a directory.
                var destWithSlash = destinationFolder.TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
                var args = $"x -y -o+ \"{archivePath}\" \"{destWithSlash}\"";
                var output = _processProvider.StartAndCapture("unrar", args);
                if (output.ExitCode == 0)
                {
                    return true;
                }

                _logger.Warn("unrar exited {0} for {1}: {2}",
                    output.ExitCode, archivePath, output.Lines);
                return false;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "RarExtractor: failed extracting {0}", archivePath);
                return false;
            }
        }

        private bool ProbeAvailable()
        {
            try
            {
                var probe = _processProvider.StartAndCapture("unrar", "-h");
                // unrar prints help on stderr and exits non-zero with no args; just
                // confirm the binary is on PATH (no exception thrown).
                return probe != null;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
