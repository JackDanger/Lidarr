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
                var album = item.AlbumRelease?.Album?.Value;
                var existingRelease = album?.AlbumReleases.Value.SingleOrDefault(x => x.Monitored);

                if (existingRelease != null)
                {
                    // Get IDs of existing tracks that have files
                    var existingTrackIds = existingRelease.Tracks.Value
                        .Where(t => t.HasFile)
                        .Select(t => t.ForeignTrackId)
                        .ToHashSet();

                    // Get matched tracks (those with local files, not in MBExtra)
                    var matchedTracks = item.TrackMapping.Mapping.Values.Select(t => t.Item1).ToList();

                    // Check if any matched track is new to the library
                    var newTracks = matchedTracks.Where(t => !existingTrackIds.Contains(t.ForeignTrackId)).ToList();

                    if (newTracks.Any())
                    {
                        _logger.Debug("Release missing {0} MB tracks but imports {1} new track(s) we don't have. Accepting {2}",
                            item.TrackMapping.MBExtra.Count, newTracks.Count, item);
                        return Decision.Accept();
                    }
                }
                else
                {
                    // No existing release, all matched tracks are new
                    var matchedTracks = item.TrackMapping.Mapping.Values.Select(t => t.Item1).ToList();
                    if (matchedTracks.Any())
                    {
                        _logger.Debug("Release missing some MB tracks but all {0} matched tracks are new. Accepting {1}",
                            matchedTracks.Count, item);
                        return Decision.Accept();
                    }
                }

                _logger.Debug("This release is missing MB tracks and doesn't import any new tracks to the library. Skipping {0}", item);
                return Decision.Reject("Has missing tracks");
            }

            return Decision.Accept();
        }
    }
}
