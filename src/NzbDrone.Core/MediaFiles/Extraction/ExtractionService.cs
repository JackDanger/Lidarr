using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;

namespace NzbDrone.Core.MediaFiles.Extraction
{
    // Walks a download folder and unpacks any archives we recognise so the rest of
    // the import pipeline (DiskScanService.GetAudioFiles, ImportDecisionMaker, etc.)
    // sees the audio that was inside.
    //
    // Why intercept at import-time instead of relying on the download client to
    // unpack: SAB extracts most .rar but ignores .tar; some custom packagers ship
    // music in .tar (observed: 103 .tar files in the user's complete folder, each
    // a single-tar single-album release). Lidarr previously ParkedImportBlocked
    // these forever with "Found archive file, might need to be extracted".
    //
    // Idempotency: each successfully-extracted archive gets a marker file
    // `<archive>.lidarr-extracted` next to it. Subsequent calls on the same folder
    // skip extraction for archives that already have a marker. The marker file
    // contains the extractor name + timestamp for forensics.
    //
    // Safety: the source archive is NOT deleted. If extraction fails part-way the
    // folder is left in whatever state the extractor left it; the marker is only
    // written on success. Adding a "delete after extract" toggle is left to a
    // follow-up that wires into config.
    public class ExtractionService : IExtractionService
    {
        public const string MarkerSuffix = ".lidarr-extracted";

        private readonly IDiskProvider _diskProvider;
        private readonly List<IArchiveExtractor> _extractors;
        private readonly Logger _logger;

        public ExtractionService(IDiskProvider diskProvider,
                                 IEnumerable<IArchiveExtractor> extractors,
                                 Logger logger)
        {
            _diskProvider = diskProvider;
            _extractors = extractors.Where(e => e.IsAvailable()).ToList();
            _logger = logger;

            if (_extractors.Count == 0)
            {
                _logger.Debug("ExtractionService: no archive extractors are available on this system");
            }
            else
            {
                _logger.Debug("ExtractionService: {0} extractors available ({1})",
                    _extractors.Count,
                    string.Join(", ", _extractors.Select(e => e.Name)));
            }
        }

        public bool ExtractIfNeeded(string folder)
        {
            if (_extractors.Count == 0)
            {
                return false;
            }

            if (!_diskProvider.FolderExists(folder))
            {
                return false;
            }

            // Recursive — some downloads put the archive in a nested subfolder. Cap
            // the cost by skipping marker files in the file iteration.
            var allFiles = _diskProvider.GetFiles(folder, true)
                .Where(p => !p.EndsWith(MarkerSuffix))
                .ToList();

            var extracted = false;
            foreach (var file in allFiles)
            {
                if (File.Exists(file + MarkerSuffix))
                {
                    continue; // already done
                }

                var extractor = _extractors.FirstOrDefault(e => e.CanHandle(file));
                if (extractor == null)
                {
                    continue;
                }

                var destination = Path.GetDirectoryName(file);
                _logger.Info("Extracting {0} ({1})", file, extractor.Name);

                bool ok;
                try
                {
                    ok = extractor.Extract(file, destination);
                }
                catch (System.Exception ex)
                {
                    _logger.Warn(ex, "Extractor {0} threw while handling {1}", extractor.Name, file);
                    ok = false;
                }

                if (ok)
                {
                    WriteMarker(file, extractor.Name);
                    extracted = true;
                }
                else
                {
                    _logger.Warn("Extractor {0} failed for {1}; leaving archive in place", extractor.Name, file);
                }
            }

            return extracted;
        }

        private void WriteMarker(string archivePath, string extractorName)
        {
            try
            {
                File.WriteAllText(archivePath + MarkerSuffix,
                    $"{extractorName}\n{System.DateTime.UtcNow:o}\n");
            }
            catch (System.Exception ex)
            {
                // Failing to write the marker means we'll re-extract next pass — not
                // catastrophic, but worth a Debug line.
                _logger.Debug(ex, "Could not write extraction marker for {0}", archivePath);
            }
        }
    }
}
