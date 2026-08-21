using System.Linq;
using NLog;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.TrackImport.Specifications
{
    public class MoreTracksSpecification : IImportDecisionEngineSpecification<LocalAlbumRelease>
    {
        private readonly Logger _logger;

        public MoreTracksSpecification(Logger logger)
        {
            _logger = logger;
        }

        public Decision IsSatisfiedBy(LocalAlbumRelease item, DownloadClientItem downloadClientItem)
        {
            var existingRelease = item.AlbumRelease.Album.Value.AlbumReleases.Value.Single(x => x.Monitored);
            var existingTrackCount = existingRelease.Tracks.Value.Count(x => x.HasFile);

            // Generous import: a release with fewer tracks than the currently-monitored
            // one used to be rejected outright. We now import it — the track-level
            // UpgradeSpecification still refuses to overwrite an existing better file,
            // so this only ever adds tracks, never replaces good ones with a partial rip.
            if (item.AlbumRelease.Id != existingRelease.Id &&
                item.TrackCount < existingTrackCount)
            {
                _logger.Debug($"Generous import: release has fewer tracks ({item.TrackCount}) than existing {existingRelease} ({existingTrackCount}); importing anyway (overwrites still guarded by UpgradeSpecification). {item}");
            }

            _logger.Trace("Accepting release {0}", item);
            return Decision.Accept();
        }
    }
}
