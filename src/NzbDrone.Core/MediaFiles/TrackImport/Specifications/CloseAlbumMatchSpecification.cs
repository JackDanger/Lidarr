using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.TrackImport.Specifications
{
    public class CloseAlbumMatchSpecification : IImportDecisionEngineSpecification<LocalAlbumRelease>
    {
        // Strict default: reject if distance > 0.50 (i.e. < 50% match).
        private const double _strictAlbumThreshold = 0.50;
        private const double _strictTrackThreshold = 0.50;

        // Lenient threshold used when the artist clearly matched (artist not in distance
        // reasons). Most "Album match is not close enough" rejections here are bonus
        // tracks / live recordings / region variants of an album by a known artist —
        // import them anyway, per the user's "anything that improves the library" rule.
        // Distance > 0.80 (i.e. < 20% match) still rejects, to avoid garbage imports.
        private const double _artistKnownAlbumThreshold = 0.80;
        private const double _artistKnownTrackThreshold = 0.85;

        private readonly Logger _logger;

        public CloseAlbumMatchSpecification(Logger logger)
        {
            _logger = logger;
        }

        public Decision IsSatisfiedBy(LocalAlbumRelease item, DownloadClientItem downloadClientItem)
        {
            double dist;
            string reasons;

            // strict when a new download
            if (item.NewDownload)
            {
                dist = item.Distance.NormalizedDistance();
                reasons = item.Distance.Reasons;
                var albumThreshold = SelectAlbumThreshold(reasons);
                if (dist > albumThreshold)
                {
                    _logger.Debug($"Album match is not close enough: {dist} vs {albumThreshold} {reasons}. Skipping {item}");
                    return Decision.Reject($"Album match is not close enough: {1 - dist:P1} vs {1 - albumThreshold:P0} {reasons}");
                }

                var worstTrackMatch = item.LocalTracks.Where(x => x.Distance != null).MaxBy(x => x.Distance.NormalizedDistance());
                if (worstTrackMatch == null)
                {
                    _logger.Debug($"No tracks matched");
                    return Decision.Reject("No tracks matched");
                }
                else
                {
                    var maxTrackDist = worstTrackMatch.Distance.NormalizedDistance();
                    var trackReasons = worstTrackMatch.Distance.Reasons;
                    var trackThreshold = SelectTrackThreshold(reasons, trackReasons);
                    if (maxTrackDist > trackThreshold)
                    {
                        _logger.Debug($"Worst track match: {maxTrackDist} vs {trackThreshold} {trackReasons}. Skipping {item}");
                        return Decision.Reject($"Worst track match: {1 - maxTrackDist:P1} vs {1 - trackThreshold:P0} {trackReasons}");
                    }
                }
            }

            // otherwise importing existing files in library
            else
            {
                // get album distance ignoring whether tracks are missing
                dist = item.Distance.NormalizedDistanceExcluding(new List<string> { "missing_tracks", "unmatched_tracks" });
                reasons = item.Distance.Reasons;
                var albumThreshold = SelectAlbumThreshold(reasons);
                if (dist > albumThreshold)
                {
                    _logger.Debug($"Album match is not close enough: {dist} vs {albumThreshold} {reasons}. Skipping {item}");
                    return Decision.Reject($"Album match is not close enough: {1 - dist:P1} vs {1 - albumThreshold:P0} {reasons}");
                }
            }

            _logger.Debug($"Accepting release {item}: dist {dist} vs threshold, reasons {reasons}");
            return Decision.Accept();
        }

        private static double SelectAlbumThreshold(string reasons)
        {
            // If "artist" appears in the distance reasons, the artist tag/folder didn't match
            // the candidate's artist — we should not relax. Otherwise the user already had
            // this artist; album-name fuzziness is acceptable.
            return ContainsArtist(reasons) ? _strictAlbumThreshold : _artistKnownAlbumThreshold;
        }

        private static double SelectTrackThreshold(string albumReasons, string trackReasons)
        {
            // Same rule — but also keep strict if the worst track itself disagrees on
            // recording_id AND title (likely the wrong track entirely, not a variant).
            if (ContainsArtist(albumReasons))
            {
                return _strictTrackThreshold;
            }

            return _artistKnownTrackThreshold;
        }

        private static bool ContainsArtist(string reasons) =>
            reasons != null && reasons.Contains("artist");
    }
}
