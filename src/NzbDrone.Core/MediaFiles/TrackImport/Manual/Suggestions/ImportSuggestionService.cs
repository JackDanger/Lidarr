using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.MediaFiles.TrackImport.Identification;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.MediaFiles.TrackImport.Manual.Suggestions
{
    // Asks MusicBrainz "given these file tags, what's the best album we know
    // about?" — used when the in-library identification path returned nothing
    // matchable. Results are filtered through ImportListExclusion so we never
    // suggest an artist or album the user has actively excluded.
    public class ImportSuggestionService : IImportSuggestionService
    {
        // Search for the user's existing tuning: bumping this between releases
        // is exactly the workflow this fork was built for.
        private const double DefaultThreshold = 0.7;

        // SkyHookProxy's album search is generous — we cap candidates to keep
        // scoring time bounded for downloads that produce a busy hit list.
        private const int MaxCandidatesToScore = 8;

        private readonly ISearchForNewAlbum _albumSearch;
        private readonly IImportListExclusionService _exclusionService;
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public ImportSuggestionService(ISearchForNewAlbum albumSearch,
                                       IImportListExclusionService exclusionService,
                                       IConfigService configService,
                                       Logger logger)
        {
            _albumSearch = albumSearch;
            _exclusionService = exclusionService;
            _configService = configService;
            _logger = logger;
        }

        public ImportSuggestion FindForTracks(IReadOnlyList<LocalTrack> localTracks)
        {
            if (localTracks == null || localTracks.Count == 0)
            {
                return null;
            }

            // Pull the most common (artist, album) tuple from the file tags —
            // unless the folder is genuinely heterogeneous, one tuple covers
            // all the files and a single MB lookup answers the whole folder.
            var artistTitle = MostCommon(localTracks, t => t.FileTrackInfo?.ArtistTitle);
            var albumTitle = MostCommon(localTracks, t => t.FileTrackInfo?.AlbumTitle);

            if (albumTitle.IsNullOrWhiteSpace())
            {
                return null;
            }

            var threshold = GetThreshold();
            if (threshold >= 1.0)
            {
                // Setting effectively turns the feature off.
                return null;
            }

            List<Album> candidates;
            try
            {
                candidates = _albumSearch.SearchForNewAlbum(albumTitle, artistTitle ?? string.Empty) ?? new List<Album>();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Suggestion lookup failed for '{0}' / '{1}'", artistTitle, albumTitle);
                return null;
            }

            if (candidates.Count == 0)
            {
                return null;
            }

            // Bulk-load exclusions for every MBID we'd consider. Single repo round
            // trip beats N FindByForeignId calls for boxy hit lists.
            var allForeignIds = candidates
                .Take(MaxCandidatesToScore)
                .SelectMany(a => new[] { a?.ForeignAlbumId, a?.ArtistMetadata?.Value?.ForeignArtistId })
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct()
                .ToList();

            var excludedIds = allForeignIds.Count == 0
                ? new HashSet<string>()
                : _exclusionService.FindByForeignId(allForeignIds)
                    .Select(e => e.ForeignId)
                    .ToHashSet();

            ImportSuggestion best = null;
            var localTrackCount = localTracks.Count;

            foreach (var album in candidates.Take(MaxCandidatesToScore))
            {
                if (album == null)
                {
                    continue;
                }

                var artistMeta = album.ArtistMetadata?.Value;
                var albumMbid = album.ForeignAlbumId;
                var artistMbid = artistMeta?.ForeignArtistId;

                if (albumMbid.IsNotNullOrWhiteSpace() && excludedIds.Contains(albumMbid))
                {
                    continue;
                }

                if (artistMbid.IsNotNullOrWhiteSpace() && excludedIds.Contains(artistMbid))
                {
                    continue;
                }

                var score = ScoreCandidate(album, artistTitle, albumTitle, localTrackCount, out var mbTrackCount);
                if (best == null || score > best.Score)
                {
                    best = new ImportSuggestion
                    {
                        ArtistName = artistMeta?.Name,
                        ArtistMBID = artistMbid,
                        AlbumName = album.Title,
                        AlbumMBID = albumMbid,
                        Score = score,
                        MbTrackCount = mbTrackCount,
                        LocalTrackCount = localTrackCount,
                    };
                }
            }

            if (best == null || best.Score < threshold)
            {
                return null;
            }

            _logger.Debug(
                "Suggesting {0} - {1} (score {2:F2}) for tagged '{3}' / '{4}'",
                best.ArtistName,
                best.AlbumName,
                best.Score,
                artistTitle,
                albumTitle);
            return best;
        }

        // Score against the file tags using the same Distance machinery
        // IdentificationService uses internally. Returns a match score in [0, 1]
        // where 1.0 is a perfect match.
        private static double ScoreCandidate(
            Album album,
            string fileArtist,
            string fileAlbum,
            int fileTrackCount,
            out int mbTrackCount)
        {
            var dist = new Distance();

            // Album title — straightforward fuzzy.
            dist.AddString("album", fileAlbum ?? string.Empty, album.Title ?? string.Empty);

            // Artist — take the best similarity across the canonical name and
            // every known alias. This is what lets "The Spooky Kids" match
            // Marilyn Manson without the user having to know the alias.
            var artistDistance = BestArtistDistance(fileArtist, album.ArtistMetadata?.Value);
            dist.Add("artist", artistDistance);

            // Track count — if we can read it from the first release. Cheap
            // sanity check that distinguishes "9-track demo" from "20-track
            // expanded edition" of the same titled album.
            var firstRelease = album.AlbumReleases?.Value?.FirstOrDefault();
            mbTrackCount = firstRelease?.TrackCount ?? 0;
            if (mbTrackCount > 0 && fileTrackCount > 0)
            {
                dist.AddNumber("tracks", fileTrackCount, mbTrackCount);
            }

            var normalized = dist.NormalizedDistance();
            return Math.Max(0.0, 1.0 - normalized);
        }

        private static double BestArtistDistance(string fileArtist, ArtistMetadata mbMeta)
        {
            if (fileArtist.IsNullOrWhiteSpace() || mbMeta == null)
            {
                return 0.0;
            }

            var cleanFile = NormalizeForCompare(fileArtist);
            if (cleanFile.IsNullOrWhiteSpace())
            {
                return 0.0;
            }

            var bestSim = 0.0;
            var candidates = new List<string> { mbMeta.Name };
            if (mbMeta.Aliases != null)
            {
                candidates.AddRange(mbMeta.Aliases);
            }

            foreach (var c in candidates)
            {
                if (c.IsNullOrWhiteSpace())
                {
                    continue;
                }

                var cleanCandidate = NormalizeForCompare(c);
                if (cleanCandidate.IsNullOrWhiteSpace())
                {
                    continue;
                }

                var sim = cleanFile.LevenshteinCoefficient(cleanCandidate);
                if (sim > bestSim)
                {
                    bestSim = sim;
                }
            }

            return Math.Max(0.0, 1.0 - bestSim);
        }

        // Mirrors Distance.Clean — strip diacritics, lowercase, keep
        // alphanumerics only. Kept private rather than reused via reflection
        // because that helper is itself private.
        private static string NormalizeForCompare(string input)
        {
            if (input == null)
            {
                return string.Empty;
            }

            var arr = input.ToLowerInvariant().RemoveAccent().ToCharArray();
            arr = Array.FindAll(arr, char.IsLetterOrDigit);
            return new string(arr);
        }

        private static string MostCommon(IReadOnlyList<LocalTrack> tracks, Func<LocalTrack, string> selector)
        {
            return tracks
                .Select(selector)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault();
        }

        private double GetThreshold()
        {
            var raw = (double)_configService.ManualImportSuggestionThreshold;
            if (raw <= 0)
            {
                return DefaultThreshold;
            }

            return Math.Min(raw, 1.0);
        }
    }
}
