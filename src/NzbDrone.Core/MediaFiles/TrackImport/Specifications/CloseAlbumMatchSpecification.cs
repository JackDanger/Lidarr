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
        // Strict default: reject if distance > 0.50 (i.e. < 50% match). Used when the
        // candidate's artist disagrees with the file's artist tag — a bad sign that we'd
        // be filing the album under the wrong artist entirely.
        private const double _strictAlbumThreshold = 0.50;
        private const double _strictTrackThreshold = 0.50;

        // Lenient threshold for the album-level check when the artist matched (artist not
        // in distance reasons). Most "Album match is not close enough" rejections here are
        // bonus tracks / live recordings / region variants of an album by a known artist —
        // import them anyway, per the user's "anything that improves the library" rule.
        // Distance > 0.80 (i.e. < 20% match) still rejects, to avoid garbage imports.
        private const double _artistKnownAlbumThreshold = 0.80;

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
                var albumThreshold = SelectAlbumThreshold(reasons, item);
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

                // When the artist matched (we're in lenient mode), the album-level decision
                // above is authoritative — don't let a single weird track block the whole
                // album. Bonus tracks, live-recording variants, and tag-mismatched files
                // are common; per-track import has its own track-level decision logic that
                // will quietly skip individual files that genuinely don't fit.
                //
                // Only enforce the worst-track wall when we're already in strict mode
                // (artist mismatched the candidate). There a bad track is real evidence
                // that Lidarr picked the wrong release entirely.
                if (IsArtistUncertain(reasons, item))
                {
                    var maxTrackDist = worstTrackMatch.Distance.NormalizedDistance();
                    var trackReasons = worstTrackMatch.Distance.Reasons;
                    if (maxTrackDist > _strictTrackThreshold)
                    {
                        _logger.Debug($"Worst track match: {maxTrackDist} vs {_strictTrackThreshold} {trackReasons}. Skipping {item}");
                        return Decision.Reject($"Worst track match: {1 - maxTrackDist:P1} vs {1 - _strictTrackThreshold:P0} {trackReasons}");
                    }
                }
            }

            // otherwise importing existing files in library
            else
            {
                // get album distance ignoring whether tracks are missing
                dist = item.Distance.NormalizedDistanceExcluding(new List<string> { "missing_tracks", "unmatched_tracks" });
                reasons = item.Distance.Reasons;
                var albumThreshold = SelectAlbumThreshold(reasons, item);
                if (dist > albumThreshold)
                {
                    _logger.Debug($"Album match is not close enough: {dist} vs {albumThreshold} {reasons}. Skipping {item}");
                    return Decision.Reject($"Album match is not close enough: {1 - dist:P1} vs {1 - albumThreshold:P0} {reasons}");
                }
            }

            _logger.Debug($"Accepting release {item}: dist {dist} vs threshold, reasons {reasons}");
            return Decision.Accept();
        }

        private static double SelectAlbumThreshold(string reasons, LocalAlbumRelease item)
        {
            // Relax the album threshold unless we're genuinely unsure which artist this
            // belongs to. See IsArtistUncertain.
            return IsArtistUncertain(reasons, item) ? _strictAlbumThreshold : _artistKnownAlbumThreshold;
        }

        // "artist" in the distance reasons means the per-file artist tag fuzzily disagreed
        // with the candidate's canonical artist name. On its own that is a weak signal:
        // compilations, "feat." credits, romanization/locale spellings and box-set tagging
        // routinely trip it for releases that genuinely belong to a known artist. So we
        // only treat the artist as *uncertain* when the tag disagrees AND the matched
        // album's artist is not an existing library artist.
        //
        // When identification has mapped the release onto an artist already in the library
        // (Id > 0) — which is the case for everything Lidarr grabbed for a monitored
        // album — the identity is certain and the tag fuzz is noise. Relax then, per the
        // user's "anything that improves the library" rule.
        private static bool IsArtistUncertain(string reasons, LocalAlbumRelease item) =>
            ContainsArtist(reasons) && !MatchedToLibraryArtist(item);

        private static bool ContainsArtist(string reasons) =>
            reasons != null && reasons.Contains("artist");

        private static bool MatchedToLibraryArtist(LocalAlbumRelease item) =>
            item?.AlbumRelease?.Album?.Value?.Artist?.Value?.Id > 0;
    }
}
