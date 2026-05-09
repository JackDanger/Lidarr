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
            // "LocalExtra" = files in the download that didn't map to any track in the
            // identified release. We used to reject outright, but a partial match is still
            // strictly better than nothing if the matched tracks are new to us — accept the
            // matched tracks and let Lidarr's per-track import handle the rest.
            if (item.NewDownload && item.TrackMapping.LocalExtra.Count > 0)
            {
                var matched = item.TrackMapping.Mapping.Values.Select(t => t.Item1).ToList();
                if (matched.Any() && AnyTrackIsNewToLibrary(matched, item))
                {
                    _logger.Debug(
                        "Release has {0} unmatched local file(s) but {1} matched track(s) are new to library. Accepting {2}",
                        item.TrackMapping.LocalExtra.Count,
                        matched.Count,
                        item);
                    return Decision.Accept();
                }

                _logger.Debug("Release has unmatched local files and no new content to gain. Skipping {0}", item);
                return Decision.Reject("Has unmatched tracks");
            }

            // "MBExtra" = tracks the MusicBrainz release lists that we have no local file
            // for. Accept anyway as long as the import would gain at least one new track —
            // the user's "strictly better" rule. Otherwise reject with a message that says
            // *what* the actual problem is (duplicate vs missing).
            if (item.NewDownload && item.TrackMapping.MBExtra.Count > 0)
            {
                var matchedTracks = item.TrackMapping.Mapping.Values.Select(t => t.Item1).ToList();

                if (matchedTracks.Any() && AnyTrackIsNewToLibrary(matchedTracks, item))
                {
                    _logger.Debug(
                        "Release missing {0} MB tracks but imports {1} matched track(s), at least one new to library. Accepting {2}",
                        item.TrackMapping.MBExtra.Count,
                        matchedTracks.Count,
                        item);
                    return Decision.Accept();
                }

                if (!matchedTracks.Any())
                {
                    _logger.Debug("Release has no tracks mapped to MB at all. Skipping {0}", item);
                    return Decision.Reject("No tracks could be matched to a release");
                }

                _logger.Debug("Release adds nothing new — all matched tracks already in library. Skipping {0}", item);
                return Decision.Reject("All matched tracks already in library");
            }

            return Decision.Accept();
        }

        private static bool AnyTrackIsNewToLibrary(System.Collections.Generic.List<NzbDrone.Core.Music.Track> matchedTracks, LocalAlbumRelease item)
        {
            // No existing monitored release means everything we'd import is new by definition.
            var existingRelease = item.AlbumRelease?.Album?.Value?.AlbumReleases?.Value
                ?.SingleOrDefault(x => x.Monitored);
            if (existingRelease == null)
            {
                return true;
            }

            var existingTrackIds = existingRelease.Tracks.Value
                .Where(t => t.HasFile)
                .Select(t => t.ForeignTrackId)
                .ToHashSet();

            return matchedTracks.Any(t => !existingTrackIds.Contains(t.ForeignTrackId));
        }
    }
}
