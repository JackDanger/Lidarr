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
using NzbDrone.Core.Profiles.Metadata;

namespace NzbDrone.Core.MediaFiles.TrackImport.Manual.Suggestions
{
    // Asks MusicBrainz "given these file tags, what's the best album we know
    // about?" — used when the in-library identification path returned nothing
    // matchable. Results are filtered through ImportListExclusion so we never
    // suggest an artist or album the user has actively excluded.
    //
    // Implementation note: we go via ISearchForNewArtist + IProvideArtistInfo
    // rather than ISearchForNewAlbum on purpose. The Lidarr-metadata-daemon
    // mirror this deploy runs against returns 0 for `type=album` searches
    // (only `type=artist` and the `/artist/{mbid}` detail endpoint behave) —
    // the artist→album-list path works on both that and the upstream Servarr
    // metadata service, so this is the portable choice.
    public class ImportSuggestionService : IImportSuggestionService
    {
        // Default tuned by gut; the user actively iterates this value via
        // commits on this branch as real downloads reveal score distributions.
        private const double DefaultThreshold = 0.7;

        // Cap on artist candidates we'll fetch full info for. Every artist
        // costs one metadata-API round trip, so we want this small. Most cases
        // resolve on the top result anyway.
        private const int MaxArtistsToFetch = 3;

        private readonly ISearchForNewArtist _artistSearch;
        private readonly IProvideArtistInfo _artistInfo;
        private readonly IMetadataProfileService _profileService;
        private readonly IImportListExclusionService _exclusionService;
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public ImportSuggestionService(ISearchForNewArtist artistSearch,
                                       IProvideArtistInfo artistInfo,
                                       IMetadataProfileService profileService,
                                       IImportListExclusionService exclusionService,
                                       IConfigService configService,
                                       Logger logger)
        {
            _artistSearch = artistSearch;
            _artistInfo = artistInfo;
            _profileService = profileService;
            _exclusionService = exclusionService;
            _configService = configService;
            _logger = logger;
        }

        // Pick the user's most-permissive metadata profile for the artist-info
        // fetch. SkyHookProxy.GetArtistInfo's FilterAlbums step would otherwise
        // drop everything not allowed by the FIRST profile in the user's list —
        // which on this deploy is "None" (zero allowed types), filtering all
        // 318 of an artist's albums out. For *suggesting* a match we want the
        // widest possible candidate pool regardless of which profile the user
        // would use to auto-fetch new releases.
        private int PickPermissiveProfileId()
        {
            var profiles = _profileService.All();
            if (profiles == null || profiles.Count == 0)
            {
                return 0;
            }

            return profiles
                .Select(p => new
                {
                    p.Id,
                    Score = (p.PrimaryAlbumTypes?.Count(x => x.Allowed) ?? 0)
                        + (p.SecondaryAlbumTypes?.Count(x => x.Allowed) ?? 0),
                })
                .OrderByDescending(x => x.Score)
                .First()
                .Id;
        }

        public ImportSuggestion FindForTracks(IReadOnlyList<LocalTrack> localTracks)
        {
            if (localTracks == null || localTracks.Count == 0)
            {
                return null;
            }

            var artistTitle = MostCommon(localTracks, t => t.FileTrackInfo?.ArtistTitle);
            var albumTitle = MostCommon(localTracks, t => t.FileTrackInfo?.AlbumTitle);

            // Need at least one of the two to have a shot at a useful query.
            // In practice both are present on Picard-tagged rips.
            if (artistTitle.IsNullOrWhiteSpace() || albumTitle.IsNullOrWhiteSpace())
            {
                return null;
            }

            var threshold = GetThreshold();
            if (threshold >= 1.0)
            {
                return null;
            }

            // Step 1: find candidate artists by fuzzy name search.
            List<Artist> artistCandidates;
            try
            {
                artistCandidates = _artistSearch.SearchForNewArtist(artistTitle) ?? new List<Artist>();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Suggestion: artist search failed for '{0}'", artistTitle);
                return null;
            }

            _logger.Debug("Suggestion: artist search '{0}' returned {1} candidates", artistTitle, artistCandidates.Count);
            if (artistCandidates.Count == 0)
            {
                return null;
            }

            // Step 2: bulk-fetch exclusions for the top-N artist MBIDs so we
            // can short-circuit excluded artists before paying for their album
            // list. We re-check album MBIDs against the same set later.
            var topArtists = artistCandidates.Take(MaxArtistsToFetch).ToList();
            var artistIds = topArtists
                .Select(a => a?.Metadata?.Value?.ForeignArtistId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .ToList();

            var excludedIds = artistIds.Count == 0
                ? new HashSet<string>()
                : _exclusionService.FindByForeignId(artistIds)
                    .Select(e => e.ForeignId)
                    .ToHashSet();

            ImportSuggestion best = null;
            var localTrackCount = localTracks.Count;

            foreach (var artistCandidate in topArtists)
            {
                var artistMeta = artistCandidate?.Metadata?.Value;
                var artistMbid = artistMeta?.ForeignArtistId;
                if (artistMbid.IsNullOrWhiteSpace())
                {
                    _logger.Debug("Suggestion: skipping candidate with no MBID ('{0}')", artistMeta?.Name);
                    continue;
                }

                if (excludedIds.Contains(artistMbid))
                {
                    _logger.Debug("Suggestion: skipping excluded artist {0} '{1}'", artistMbid, artistMeta?.Name);
                    continue;
                }

                Artist fullArtist;
                try
                {
                    fullArtist = _artistInfo.GetArtistInfo(artistMbid, PickPermissiveProfileId());
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Suggestion: artist info fetch failed for {0}", artistMbid);
                    continue;
                }

                var albums = fullArtist?.Albums?.Value;
                _logger.Debug(
                    "Suggestion: artist '{0}' ({1}) has {2} albums",
                    fullArtist?.Metadata?.Value?.Name,
                    artistMbid,
                    albums?.Count ?? 0);
                if (albums == null || albums.Count == 0)
                {
                    continue;
                }

                // Bulk-check the artist's album MBIDs against exclusions in
                // one round trip rather than one per album.
                var albumIds = albums
                    .Select(a => a.ForeignAlbumId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct()
                    .ToList();
                var excludedAlbumIds = albumIds.Count == 0
                    ? new HashSet<string>()
                    : _exclusionService.FindByForeignId(albumIds)
                        .Select(e => e.ForeignId)
                        .ToHashSet();

                foreach (var album in albums)
                {
                    var albumMbid = album.ForeignAlbumId;
                    if (albumMbid.IsNotNullOrWhiteSpace() && excludedAlbumIds.Contains(albumMbid))
                    {
                        continue;
                    }

                    // Hot path optimisation — pre-filter by a cheap cleaned
                    // string compare before paying for the full Distance pass.
                    // Don't bother scoring albums whose title shares no chars
                    // with the file tag.
                    if (!ShareSomeAlphanumerics(albumTitle, album.Title))
                    {
                        continue;
                    }

                    var score = ScoreCandidate(album, fullArtist, artistTitle, albumTitle, localTrackCount, out var mbTrackCount);
                    if (best == null || score > best.Score)
                    {
                        best = new ImportSuggestion
                        {
                            ArtistName = fullArtist?.Metadata?.Value?.Name ?? artistMeta?.Name,
                            ArtistMBID = artistMbid,
                            AlbumName = album.Title,
                            AlbumMBID = albumMbid,
                            Score = score,
                            MbTrackCount = mbTrackCount,
                            LocalTrackCount = localTrackCount,
                        };
                    }
                }
            }

            if (best == null || best.Score < threshold)
            {
                _logger.Debug(
                    "Suggestion: best candidate '{0} - {1}' scored {2:F2} (threshold {3:F2}) for tagged '{4}' / '{5}'",
                    best?.ArtistName,
                    best?.AlbumName,
                    best?.Score ?? 0,
                    threshold,
                    artistTitle,
                    albumTitle);
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

        private static bool ShareSomeAlphanumerics(string a, string b)
        {
            var ca = NormalizeForCompare(a);
            var cb = NormalizeForCompare(b);
            if (ca.Length == 0 || cb.Length == 0)
            {
                return false;
            }

            // Trigram-ish: do they share at least one 3-char run? Cheap, and
            // false-positives are fine (just means we waste a Distance call).
            for (var i = 0; i <= ca.Length - 3; i++)
            {
                if (cb.IndexOf(ca.Substring(i, 3), StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        // Score against the file tags using the same Distance machinery
        // IdentificationService uses internally. Returns a match score in [0, 1]
        // where 1.0 is a perfect match.
        private static double ScoreCandidate(
            Album album,
            Artist artist,
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
            var artistDistance = BestArtistDistance(fileArtist, artist?.Metadata?.Value);
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
