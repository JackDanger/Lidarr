using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Core.MediaFiles.TrackImport.Identification;
using NzbDrone.Core.Music;

namespace NzbDrone.Core.Parser.Model
{
    public class LocalAlbumRelease
    {
        public LocalAlbumRelease()
        {
            LocalTracks = new List<LocalTrack>();

            // A dummy distance, will be replaced
            Distance = new Distance();
            Distance.Add("album_id", 1.0);
        }

        public LocalAlbumRelease(List<LocalTrack> tracks)
        {
            LocalTracks = tracks;

            // A dummy distance, will be replaced
            Distance = new Distance();
            Distance.Add("album_id", 1.0);
        }

        public List<LocalTrack> LocalTracks { get; set; }
        public int TrackCount => LocalTracks.Count;

        public TrackMapping TrackMapping { get; set; }
        public Distance Distance { get; set; }
        public AlbumRelease AlbumRelease { get; set; }
        public List<LocalTrack> ExistingTracks { get; set; }
        public bool NewDownload { get; set; }

        public bool IsLikelyClassical => DetectClassicalMusic();

        public void PopulateMatch()
        {
            if (AlbumRelease != null)
            {
                LocalTracks = LocalTracks.Concat(ExistingTracks).DistinctBy(x => x.Path).ToList();
                foreach (var localTrack in LocalTracks)
                {
                    localTrack.Release = AlbumRelease;
                    localTrack.Album = AlbumRelease.Album.Value;
                    localTrack.Artist = localTrack.Album.Artist.Value;

                    if (TrackMapping.Mapping.ContainsKey(localTrack))
                    {
                        var track = TrackMapping.Mapping[localTrack].Item1;
                        localTrack.Tracks = new List<Track> { track };
                        localTrack.Distance = TrackMapping.Mapping[localTrack].Item2;
                    }
                }
            }
        }

        private bool DetectClassicalMusic()
        {
            try
            {
                if (LocalTracks == null || !LocalTracks.Any())
                {
                    return false;
                }

                var classicalPatterns = new[]
                {
                    @"\bconcert[o|a]?\b",
                    @"\bsymphon",
                    @"\bquartet",
                    @"\bsonata",
                    @"\bopus\b",
                    @"\bop\.\s*\d+",
                    @"\bwwv",
                    @"\bk\.\s*\d+",
                    @"\bno\.\s*\d+",
                    @"\betude",
                    @"\bprelude",
                    @"\bfugue",
                    @"\bvariation"
                };

                var folderPath = Path.GetDirectoryName(LocalTracks.First().Path);
                if (!string.IsNullOrEmpty(folderPath))
                {
                    var folderName = Path.GetFileName(folderPath).ToLower();
                    foreach (var pattern in classicalPatterns)
                    {
                        if (Regex.IsMatch(folderName, pattern, RegexOptions.IgnoreCase))
                        {
                            return true;
                        }
                    }
                }

                var trackTitles = string.Join(" ", LocalTracks.Select(x => x.FileTrackInfo?.Title ?? "")).ToLower();
                var classicalMatches = 0;

                foreach (var pattern in classicalPatterns)
                {
                    if (Regex.IsMatch(trackTitles, pattern))
                    {
                        classicalMatches++;
                        if (classicalMatches >= 2)
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        public override string ToString()
        {
            return "[" + string.Join(", ", LocalTracks.Select(x => Path.GetDirectoryName(x.Path)).Distinct()) + "]";
        }
    }

    public class TrackMapping
    {
        public TrackMapping()
        {
            Mapping = new Dictionary<LocalTrack, Tuple<Track, Distance>>();
        }

        public Dictionary<LocalTrack, Tuple<Track, Distance>> Mapping { get; set; }
        public List<LocalTrack> LocalExtra { get; set; }
        public List<Track> MBExtra { get; set; }
    }
}
