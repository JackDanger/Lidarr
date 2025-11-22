using System.Linq;
using NLog;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.TrackImport.Specifications
{
    public class NoMissingOrUnmatchedTracksSpecification : IImportDecisionEngineSpecification<LocalAlbumRelease>
    {
        private readonly Logger _logger;

        public NoMissingOrUnmatchedTracksSpecification(Logger logger)
        {
            _logger = logger;
        }

        public Decision IsSatisfiedBy(LocalAlbumRelease item, DownloadClientItem downloadClientItem)
        {
            if (item.NewDownload && item.TrackMapping.LocalExtra.Count > 0)
            {
                _logger.Debug("This release has track files that have not been matched. Skipping {0}", item);
                return Decision.Reject("Has unmatched tracks");
            }

            if (item.NewDownload && item.TrackMapping.MBExtra.Count > 0)
            {
                // Allow import if the incoming release has more tracks than currently exist (or none exist)
                var existingTrackCount = 0;

                var album = item.AlbumRelease?.Album?.Value;
                var existingRelease = album?.AlbumReleases.Value.SingleOrDefault(x => x.Monitored);
                if (existingRelease != null)
                {
                    existingTrackCount = existingRelease.Tracks.Value.Count(t => t.HasFile);
                }

                if (item.TrackCount > existingTrackCount)
                {
                    _logger.Debug("Release missing tracks but improves track count ({0} > {1}). Accepting {2}", item.TrackCount, existingTrackCount, item);
                    return Decision.Accept();
                }

                _logger.Debug("This release is missing tracks and does not improve existing track count ({0} <= {1}). Skipping {2}", item.TrackCount, existingTrackCount, item);
                return Decision.Reject("Has missing tracks");
            }

            return Decision.Accept();
        }
    }
}
