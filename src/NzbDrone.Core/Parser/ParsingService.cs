using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Parser
{
    public interface IParsingService
    {
        Artist GetArtist(string title);
        Artist GetArtistFromTag(string file);
        RemoteAlbum Map(ParsedAlbumInfo parsedAlbumInfo, SearchCriteriaBase searchCriteria = null);
        RemoteAlbum Map(ParsedAlbumInfo parsedAlbumInfo, int artistId, IEnumerable<int> albumIds);
        List<Album> GetAlbums(ParsedAlbumInfo parsedAlbumInfo, Artist artist, SearchCriteriaBase searchCriteria = null);

        // Music stuff here
        Album GetLocalAlbum(string filename, Artist artist);
    }

    public class ParsingService : IParsingService
    {
        private readonly IArtistService _artistService;
        private readonly IAlbumService _albumService;
        private readonly ITrackService _trackService;
        private readonly IMediaFileService _mediaFileService;
        private readonly Logger _logger;

        public ParsingService(ITrackService trackService,
                              IArtistService artistService,
                              IAlbumService albumService,
                              IMediaFileService mediaFileService,
                              Logger logger)
        {
            _albumService = albumService;
            _artistService = artistService;
            _trackService = trackService;
            _mediaFileService = mediaFileService;
            _logger = logger;
        }

        public Artist GetArtist(string title)
        {
            var parsedAlbumInfo = Parser.ParseAlbumTitle(title);

            if (parsedAlbumInfo != null && !parsedAlbumInfo.ArtistName.IsNullOrWhiteSpace())
            {
                title = parsedAlbumInfo.ArtistName;
            }

            var artistInfo = _artistService.FindByName(title);

            if (artistInfo == null)
            {
                _logger.Debug("Trying inexact artist match for {0}", title);
                artistInfo = _artistService.FindByNameInexact(title);
            }

            return artistInfo;
        }

        public Artist GetArtistFromTag(string file)
        {
            var parsedTrackInfo = Parser.ParseMusicPath(file);

            var artist = new Artist();

            if (parsedTrackInfo.ArtistMBId.IsNotNullOrWhiteSpace())
            {
                artist = _artistService.FindById(parsedTrackInfo.ArtistMBId);

                if (artist != null)
                {
                    return artist;
                }
            }

            if (parsedTrackInfo == null || parsedTrackInfo.ArtistTitle.IsNullOrWhiteSpace())
            {
                return null;
            }

            artist = _artistService.FindByName(parsedTrackInfo.ArtistTitle);

            if (artist == null)
            {
                _logger.Debug("Trying inexact artist match for {0}", parsedTrackInfo.ArtistTitle);
                artist = _artistService.FindByNameInexact(parsedTrackInfo.ArtistTitle);
            }

            return artist;
        }

        public RemoteAlbum Map(ParsedAlbumInfo parsedAlbumInfo, SearchCriteriaBase searchCriteria = null)
        {
            var remoteAlbum = new RemoteAlbum
            {
                ParsedAlbumInfo = parsedAlbumInfo,
            };

            var artist = GetArtist(parsedAlbumInfo, searchCriteria);

            if (artist == null)
            {
                return remoteAlbum;
            }

            remoteAlbum.Artist = artist;
            remoteAlbum.Albums = GetAlbums(parsedAlbumInfo, artist, searchCriteria);

            return remoteAlbum;
        }

        public List<Album> GetAlbums(ParsedAlbumInfo parsedAlbumInfo, Artist artist, SearchCriteriaBase searchCriteria = null)
        {
            var albumTitle = parsedAlbumInfo.AlbumTitle;
            var result = new List<Album>();

            if (parsedAlbumInfo.AlbumTitle == null)
            {
                return new List<Album>();
            }

            Album albumInfo = null;

            if (parsedAlbumInfo.Discography)
            {
                if (parsedAlbumInfo.DiscographyStart > 0)
                {
                    return _albumService.ArtistAlbumsBetweenDates(artist,
                        new DateTime(parsedAlbumInfo.DiscographyStart, 1, 1),
                        new DateTime(parsedAlbumInfo.DiscographyEnd, 12, 31),
                        false);
                }

                if (parsedAlbumInfo.DiscographyEnd > 0)
                {
                    return _albumService.ArtistAlbumsBetweenDates(artist,
                        new DateTime(1800, 1, 1),
                        new DateTime(parsedAlbumInfo.DiscographyEnd, 12, 31),
                        false);
                }

                return _albumService.GetAlbumsByArtist(artist.Id);
            }

            if (searchCriteria != null)
            {
                albumInfo = searchCriteria.Albums.ExclusiveOrDefault(e => e.Title == albumTitle);
            }

            if (albumInfo == null)
            {
                // TODO: Search by Title and Year instead of just Title when matching
                albumInfo = _albumService.FindByTitle(artist.ArtistMetadataId, parsedAlbumInfo.AlbumTitle);
            }

            if (albumInfo == null)
            {
                _logger.Debug("Trying inexact album match for {0}", parsedAlbumInfo.AlbumTitle);
                albumInfo = _albumService.FindByTitleInexact(artist.ArtistMetadataId, parsedAlbumInfo.AlbumTitle);
            }

            if (albumInfo != null)
            {
                result.Add(albumInfo);
            }
            else
            {
                _logger.Debug("Unable to find {0}", parsedAlbumInfo);
            }

            return result;
        }

        public RemoteAlbum Map(ParsedAlbumInfo parsedAlbumInfo, int artistId, IEnumerable<int> albumIds)
        {
            return new RemoteAlbum
            {
                ParsedAlbumInfo = parsedAlbumInfo,
                Artist = _artistService.GetArtist(artistId),
                Albums = _albumService.GetAlbums(albumIds)
            };
        }

        private Artist GetArtist(ParsedAlbumInfo parsedAlbumInfo, SearchCriteriaBase searchCriteria)
        {
            Artist artist = null;

            if (searchCriteria != null)
            {
                if (searchCriteria.Artist.CleanName == parsedAlbumInfo.ArtistName.CleanArtistName())
                {
                    return searchCriteria.Artist;
                }
            }

            try
            {
                artist = _artistService.FindByName(parsedAlbumInfo.ArtistName);
            }
            catch (MultipleArtistsFoundException)
            {
                // Two or more local artists share the same CleanName (e.g. two MB
                // entries both named "Djo"). Disambiguate by checking which candidate
                // owns an album whose title matches the parsed release's album. If
                // exactly one matches, that's our artist; otherwise we can't tell
                // from the indexer string alone — log and skip the release rather
                // than dropping it on a thrown exception that pollutes the log every
                // RSS sync. The user can manually disambiguate via search later.
                artist = ResolveAmbiguousArtist(parsedAlbumInfo);
            }

            if (artist == null)
            {
                _logger.Debug("Trying inexact artist match for {0}", parsedAlbumInfo.ArtistName);
                artist = _artistService.FindByNameInexact(parsedAlbumInfo.ArtistName);
            }

            if (artist == null)
            {
                _logger.Debug("No matching artist {0}", parsedAlbumInfo.ArtistName);
                return null;
            }

            return artist;
        }

        // Pick the right artist when the local DB has multiple entries sharing a
        // CleanName. Strategy:
        //   1. If parsedAlbumInfo names an album, find which candidate owns it
        //      (exact title match, then inexact). One winner → return it.
        //   2. If still ambiguous, return null — the release isn't actionable.
        // Returning null lets the rest of the parse fall through to "No matching
        // artist", which is the same outcome the throw produced but without the
        // per-release error log.
        private Artist ResolveAmbiguousArtist(ParsedAlbumInfo parsedAlbumInfo)
        {
            var candidates = _artistService.FindAllByName(parsedAlbumInfo.ArtistName);
            if (candidates.Count <= 1)
            {
                return candidates.FirstOrDefault();
            }

            var displayName = candidates.First().Name;

            if (parsedAlbumInfo.AlbumTitle.IsNullOrWhiteSpace())
            {
                _logger.Debug("Multiple '{0}' artists in library and no album title to disambiguate; skipping", displayName);
                return null;
            }

            var matches = new List<Artist>();
            foreach (var candidate in candidates)
            {
                var album = _albumService.FindByTitle(candidate.ArtistMetadataId, parsedAlbumInfo.AlbumTitle)
                            ?? _albumService.FindByTitleInexact(candidate.ArtistMetadataId, parsedAlbumInfo.AlbumTitle);
                if (album != null)
                {
                    matches.Add(candidate);
                }
            }

            if (matches.Count == 1)
            {
                _logger.Debug("Disambiguated '{0}' (album '{1}') → MB id {2}", displayName, parsedAlbumInfo.AlbumTitle, matches[0].Metadata?.Value?.ForeignArtistId);
                return matches[0];
            }

            _logger.Debug("Multiple '{0}' artists in library, album '{1}' matched {2} candidates; skipping", displayName, parsedAlbumInfo.AlbumTitle, matches.Count);
            return null;
        }

        public Album GetLocalAlbum(string filename, Artist artist)
        {
            if (Path.HasExtension(filename))
            {
                filename = Path.GetDirectoryName(filename);
            }

            var tracksInAlbum = _mediaFileService.GetFilesByArtist(artist.Id)
                .FindAll(s => Path.GetDirectoryName(s.Path) == filename)
                .DistinctBy(s => s.AlbumId)
                .ToList();

            return tracksInAlbum.Count == 1 ? _albumService.GetAlbum(tracksInAlbum.First().AlbumId) : null;
        }
    }
}
