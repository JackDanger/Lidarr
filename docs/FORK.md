# What this fork carries

`jackdanger/wip` diverges from upstream Lidarr `develop` on purpose. This file
is the rebase contract: every behavior below was paid for with a real incident
(most have a matching entry in `docs/tracked-download-state-machine.md` or in
the bixby repo's `data/homelab.md`), and a rebase that silently drops one
reintroduces the incident. Verify each anchor still holds after any rebase.

## Import safety invariants (destructive if lost)

- **`ImportApprovedTracks`** (`src/NzbDrone.Core/MediaFiles/TrackImport/ImportApprovedTracks.cs`):
  `replaceExisting` wipes an album's existing files ONLY when the incoming
  decisions cover every track of the release. Upstream assumes full-album
  upgrades; this fork's relaxed specs accept partial releases, and a 22-of-67
  retry once recycled 45 good files and imported nothing.
- **No whole-root rescans**: the startup handler does not queue a whole-root
  `RescanFolders`, and refresh-triggered rescans are scoped to the refreshed
  artist's folder. A whole-root scan on this library never finishes, wedges the
  command queue, and its stale listing detaches TrackFile rows for anything
  imported while it ran.
- **`CloseAlbumMatchSpecification`**: falls back to the strict 0.50 distance
  threshold whenever the album-title reason is present — a different album by a
  known artist must never be filed over the wanted one.

## Queue / download lifecycle (`src/NzbDrone.Core/Download/CompletedDownloadService.cs`)

The single-assignment state machine and its history live in
`docs/tracked-download-state-machine.md` (V1–V13). Load-bearing behaviors:

- Blocklist any release whose import adds nothing (`MarkAsFailed(skipRedownload: true)`
  or Imported+blocklist for still-seeding torrents) — kills the re-grab loop
  that once hit an indexer 3–4×/hour with one NZB.
- Persistent-failure prefixes stop `Check` from re-running unwinnable imports
  every refresh.
- Archive extraction before import; video-only and extracted-no-audio downloads
  terminal-fail instead of parking forever.
- Unmatchable-but-real audio is orphan-imported to the artist's `.unmatched/`.
- V13: destination collisions self-heal via a scoped artist rescan when the
  on-disk files adopt; genuine naming collisions still park as ImportBlocked.
- `ImportDecisionMaker` caches import decisions and invalidates on
  artist/album edit/move/delete events (V11).

## Performance (upstream-PR candidates, all clean)

- **`MediaFileRepository.GetFileWithPath(List)`** filters by path in SQL instead
  of materializing the whole 435k-file × 12M-track join per rescan/import pass
  (commit 214464b91).
- **`AlbumControllerWithSignalR.MapToResource(List)`** uses per-artist
  statistics when the album list has one artist; `ArtistStatisticsRepository
  .MapResults` joins sizes via dictionary instead of an O(N×M) scan
  (commit 230dc2af4). Without these an artist page stalls ~2 min after any
  refresh on a big library.

## Search recall (upstream-PR candidates)

- **`NewznabRequestGenerator`**: apostrophe-stripped fallback tier — scene
  releases drop apostrophes and newznab indexers tokenize `D'Angelo` as
  `d + angelo`, returning nothing (commit 1ebb52be2).
- **`Parser.ParseAlbumTitleWithSearchCriteria`**: apostrophes in artist/album
  names match as optional separators so apostrophe-less release names parse.

## Search scoping (bixby's backfill depends on these)

- **`AlbumSearchCommand.IndexerIds`** → `SearchCriteriaBase.IndexerIds` →
  filtered in `ReleaseSearchService.Dispatch` (commit 3fae001d4). The bixby
  music backfill's per-lane budgets are impossible without it.
- **Unscoped sweep guards**: `MissingAlbumSearchCommand` with no artist and
  `CutoffUnmetAlbumSearchCommand` refuse runs over 1000 albums — one click
  would otherwise burn every indexer's daily API quota for days.

## Not in this repo, but part of the deployment

- Tubifarry plugin (runtime-loaded from `/var/lib/lidarr/plugins`), pointed at
  the self-hosted LMD metadata mirror.
- `/root/lidarr_build.sh`, `/root/lidarr-loop-guard.sh` on LXC 163, and the
  operational lore in the bixby repo's `data/homelab.md` § Music automation.

## Test debt

`.github/workflows/wip-ci.yml` runs the core unit suite on every `jackdanger/wip`
push. Sixteen upstream tests assert pre-fork behavior (import move/copy
semantics, tracked-download source-title matching, delete-once bookkeeping,
one config round-trip) and are excluded by name in the workflow's
`FORK_DRIFT_FILTER`. Each exclusion is debt: the right fix is updating the
test to assert the fork's invariant and removing it from the filter. Never
grow the filter without an entry here explaining which fork behavior the
test collides with. `should_parse_artist_name_and_album_title` and
`should_parse_year_or_year_range_from_discography` are excluded
wholesale — only its eight Discography-release cases drifted (the fork
changed discography handling), but a name filter cannot split a
parameterized test, so its healthy cases lost CI coverage too; restoring
them means fixing the eight cases.
