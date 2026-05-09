using NLog;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.TrackImport.Specifications
{
    public class CloseTrackMatchSpecification : IImportDecisionEngineSpecification<LocalTrack>
    {
        // Album-level CloseAlbumMatchSpecification has already gated the album. By the
        // time we're here, we've decided the file belongs to this album. The only thing
        // this spec is supposed to catch is tracks whose distance is so wildly bad that
        // they obviously shouldn't be filed here at all (e.g. the wrong audio file
        // accidentally landed in this folder).
        //
        // Old threshold of 0.40 (60% match required) was rejecting legitimate live
        // recordings, alternate takes, region variants, and tag-mismatched tracks where
        // recording_id / track_title don't match MB's idea of the album. Relaxed to 0.95
        // (5% match required) so we only block files that share essentially nothing with
        // any track in the identified album.
        private const double _threshold = 0.95;

        private readonly Logger _logger;

        public CloseTrackMatchSpecification(Logger logger)
        {
            _logger = logger;
        }

        public Decision IsSatisfiedBy(LocalTrack item, DownloadClientItem downloadClientItem)
        {
            var dist = item.Distance.NormalizedDistance();
            var reasons = item.Distance.Reasons;

            if (dist > _threshold)
            {
                _logger.Debug($"Track match is not close enough: {dist} vs {_threshold} {reasons}. Skipping {item}");
                return Decision.Reject($"Track match is not close enough: {1 - dist:P1} vs {1 - _threshold:P0} {reasons}");
            }

            _logger.Debug($"Track accepted: {dist} vs {_threshold} {reasons}.");
            return Decision.Accept();
        }
    }
}
