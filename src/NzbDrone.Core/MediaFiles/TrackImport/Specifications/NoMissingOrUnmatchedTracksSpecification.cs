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
                // Allow import if we have more local files than existing tracks
                // This implements "strictly better": we care about files we can actually import, not the complete release
                var existingTrackCount = 0;

                var album = item.AlbumRelease?.Album?.Value;
                var existingRelease = album?.AlbumReleases.Value.SingleOrDefault(x => x.Monitored);
                if (existingRelease != null)
                {
                    existingTrackCount = existingRelease.Tracks.Value.Count(t => t.HasFile);
                }

                // Count tracks that we actually have files for (total minus the missing MB tracks)
                var localMatchedTrackCount = item.TrackCount - item.TrackMapping.MBExtra.Count;

                if (localMatchedTrackCount > existingTrackCount)
                {
                    _logger.Debug("Release missing {0} tracks from MB but has {1} matched files vs {2} existing. Net gain detected, accepting {3}",
                        item.TrackMapping.MBExtra.Count, localMatchedTrackCount, existingTrackCount, item);
                    return Decision.Accept();
                }

                _logger.Debug("This release is missing MB tracks and matched file count ({0}) doesn't exceed existing ({1}). Skipping {2}",
                    localMatchedTrackCount, existingTrackCount, item);
                return Decision.Reject("Has missing tracks");
            }

            return Decision.Accept();
        }
    }
}
