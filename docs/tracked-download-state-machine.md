# TrackedDownload State Machine

How a single download moves through Lidarr's tracking pipeline, where every
state transition lives in code, and the invariants that the import loop
relies on. Keep this in sync with the codebase — if a transition gets added
or moved, update this file.

## States

`TrackedDownloadState` is defined in
`src/NzbDrone.Core/Download/TrackedDownloads/TrackedDownload.cs:38`.

| State | Trackable¹ | Meaning |
|---|---|---|
| `Downloading` | yes | Download client says it's still grabbing, or it just finished and we haven't classified it yet |
| `DownloadFailedPending` | yes | Download client reported failure; Lidarr is queued to publish a `DownloadFailedEvent` |
| `DownloadFailed` | **no** | Failure event has been published. Terminal. |
| `ImportBlocked` | yes | Auto-import can't proceed — needs user intervention or external change (artist mismatch, parse failure, no MB match) |
| `ImportPending` | yes | Ready for the next call to `CompletedDownloadService.Import` |
| `Importing` | yes | `ImportApprovedTracks.Import` is running on the file set right now (transient — only meaningful between L137 and L147 of `Import()`) |
| `ImportFailed` | yes | Import attempted, didn't succeed for a reason that *might* be fixable later. Auto-retried on every refresh by `ResetFailedImportsForRetry`. |
| `Imported` | **no** | Import succeeded (or content is already in the library). Terminal; `RemoveCompletedDownloads` publishes `DownloadCanBeRemovedEvent`. |
| `Ignored` | **no** | User explicitly ignored. Terminal. No automatic transitions in. |

¹ "Trackable" per `DownloadMonitoringService.DownloadIsTrackable` (lines 138-141).
Untrackable items don't appear in the queue API, don't get re-checked, and
have no path back into the active pipeline short of being re-grabbed under
a new `downloadId`.

## Transitions

Every assignment to `.State` in the `NzbDrone.Core` tree, in the order they
fire during a typical lifecycle. **`▶`** marks a transition we (this fork)
added; **`✗`** marks a known violation/wart documented below.

```
                  ┌───────────────────────────────────────────┐
                  │                                           │
                  ▼                                           │
          ┌───────────────┐                                   │
   init──►│  Downloading  │                                   │
   from   └───┬───────┬───┘                                   │
   history    │       │                                       │
              │       │ FailedDownloadService.Check (L73-89)  │
              │       │   DC reports Failed/Encrypted         │
              │       ▼                                       │
              │  ┌────────────────────────┐                   │
              │  │  DownloadFailedPending │                   │
              │  └─────────────┬──────────┘                   │
              │                │ FailedDownloadService.       │
              │                │   ProcessFailed (L118)       │
              │                ▼                              │
              │         ┌──────────────┐ (terminal)           │
              │         │DownloadFailed│                      │
              │         └──────────────┘                      │
              │                                               │
              │ CompletedDownloadService.Check                │
              │   ├─ artist mismatch        → ImportBlocked   │
              │   ├─ unable to parse        → ImportBlocked  ─┘
              │   └─ ok                     → ImportPending
              ▼
       ┌───────────────┐  ▶ V11: our IsBlockedByPersistentFailure guard
       │ ImportBlocked │     at CompletedDownloadService.Check L80
       └──────┬────────┘     short-circuits when the existing StatusMessages
              │              (Title or Messages) match our "unfindable" or
              │              "persistent import failure" prefix buckets
              │              (V3 empty folder, V5 archive/exec/parse-fail,
              │              V10 NotParentException, etc). Without it, Check
              │              would always reset → ImportPending.
              │
              ▼ Check passes → ImportPending
       ┌───────────────┐
       │ ImportPending │◄────────────────────────────────┐
       └──────┬────────┘                                 │
              │                                          │
              │ DownloadProcessingService.Execute        │
              │   for each ImportPending tracked         │
              │   download → CompletedDownloadService.   │
              │                Import (L120-211)         │
              ▼                                          │
       ┌───────────────┐                                 │
       │   Importing   │ ✗ V1: transient. Always over-   │
       └──────┬────────┘    written by L147 unless       │
              │             VerifyImport short-circuits  │
              │  ImportApprovedTracks.Import runs        │
              ▼                                          │
       Result classification (CompletedDownloadService.ClassifyAndSetState L173-238)
       │                                                                │
       ├─ VerifyImport=true (full import OR all-in-history)             │
       │     → Imported, DownloadCompletedEvent                         │
       │                                                                │
       ├─ importResults empty (Blu-ray ISO / SACD video / empty folder) │
       │     → ImportBlocked, "No files found are eligible for import"  │
       │       (was V3 — fixed: no longer loops in ImportPending)       │
       │                                                                │
       ├─ count==1, single rejection with no Item (.iso / corrupt tags) │
       │     → ImportBlocked + the rejection text                       │
       │       (was V5 — fixed)                                         │
       │                                                                │
       ├─ all results Imported but VerifyImport=false (track count short)
       │     → Imported, DownloadCompletedEvent                         │
       │       (was V4 — fixed: was forcing ImportBlocked due to header │
       │        always-on; now correctly recognizes successful import)  │
       │                                                                │
       ├─ ANY result not Imported (mixed or all-rejected)               │
       │     │                                                          │
       │     ├─ ▶ TryHandleNonActionable                                │
       │     │     ├─ all-bucket "we already have it"                   │
       │     │     │      → Imported, DownloadCompletedEvent            │
       │     │     └─ all-bucket "couldn't find" (+/- have-it)          │
       │     │            → ImportBlocked + per-file detail             │
       │     │                                                          │
       │     └─ otherwise → ImportFailed                                │
       │                       │                                        │
       │                       │ ▶ ResetFailedImportsForRetry           │
       │                       │   ONCE per startup (hook on first      │
       │                       │   TrackedDownloadRefreshedEvent), not  │
       │                       │   every Execute pass — V2 fixed by     │
       │                       │   trigger-based retry (option B).      │
       │                       ▼                                        │
       │                ImportPending  ───────────────────────────────►─┤

       ┌───────────────┐  (terminal, untrackable)
       │   Imported    │  also: RemoveCompletedDownloads triggers
       └───────────────┘  DownloadCanBeRemovedEvent → DC cleanup

       ┌───────────────┐  (terminal, untrackable, no auto-entry)
       │    Ignored    │
       └───────────────┘
```

## Initialization

`TrackedDownloadService.TrackDownload` (L100-228) creates a `TrackedDownload`
when the download monitor first sees a downloadId. The initial state comes
from `GetStateFromHistory`:

| Most-recent history event | Initial state |
|---|---|
| `DownloadImportIncomplete` | `ImportFailed` |
| `DownloadImported` | `Imported` |
| `DownloadFailed` | `DownloadFailed` |
| `DownloadIgnored` | `Ignored` |
| (none, or any other event) | `Downloading` |

If an `existingItem` is found and **not** in `Downloading` state, `TrackDownload`
re-uses it (does NOT re-init from history). This means once a download has
moved past `Downloading`, its state is "owned" by the import pipeline.

## External "tickle" sources

Three places reset state from outside the normal Import → result classification flow:

| Caller | Transition | Trigger |
|---|---|---|
| `DownloadProcessingService.Execute` "normalize Importing" | `Importing` (DC=Completed) → `ImportPending` | Top of every `Execute` pass — recovers items left mid-Import by a previous crash |
| `DownloadProcessingService.Execute` catch | `Importing` → `ImportPending` | Exception thrown inside `Import()` |
| `AutoRetryFailedImportsOnStartupHandler.Handle(TrackedDownloadRefreshedEvent)` | `ImportFailed` → `ImportPending` | Once per process start, on the **first** `TrackedDownloadRefreshedEvent` after `ApplicationStartedEvent`. Guarded by `Interlocked.Exchange` — subsequent refreshes are no-ops. Replaces the old per-Execute reset (V2 / option B). |

## Known violations / warts

- **V1: `Importing` is not a reliable indicator at quiescence.** Set briefly at
  `Import` L154 immediately before `ProcessPath`, and exits the method via
  `ClassifyAndSetState` which always assigns a terminal state. The state is
  only "really" Importing for the duration of `ProcessPath`; an exception
  during that call is the only legitimate exit with `State == Importing`,
  which `DownloadProcessingService.Execute`'s catch block normalizes back
  to `ImportPending` and the "normalize Importing on startup" block
  recovers across crashes. Documented as an invariant at the top of
  `Import` so future edits don't introduce a "leaks Importing" path.

- **~~V2~~ (fixed): `ImportFailed` was being unconditionally retried every
  refresh.** Replaced with trigger-based reset — once per process start,
  on the first `TrackedDownloadRefreshedEvent` (when the cache is populated
  from the download client). Rationale: the items that currently land in
  `ImportFailed` (artist mismatch, score-too-low, .iso, etc.) won't pass
  the same matching code on retry. They'd only succeed if either (a) we
  changed matching code, or (b) MusicBrainz gained an entry. (a) is bound
  to a Lidarr restart in our workflow (`lidarr-deploy` always restarts);
  (b) is rare enough that "wait until the next restart" is a fine cadence.
  See option B in the V2 design discussion.

- **~~V3~~ (fixed):** "Empty importResults" used to leave state at
  `ImportPending` and loop forever. `ClassifyAndSetState` now sets
  `ImportBlocked` when nothing in the download is parseable as audio
  (Blu-ray ISO, SACD video, etc.).

- **~~V4~~ (fixed):** `statusMessages` was initialized with the "One or more
  tracks ..." header at construction time, making `statusMessages.Any()`
  always true and forcing the "all imported but VerifyImport=false" case
  to `ImportBlocked`. `BuildPerFileStatusMessages` now only emits the header
  when there's per-file detail to group. The "all imported but count short"
  case is now correctly `Imported`.

- **~~V5~~ (fixed):** Single result, rejection, no parsed `LocalTrack`
  (.iso, corrupt tags) used to leave state at `ImportPending` → loop.
  `ClassifyAndSetState` now sets `ImportBlocked`.

- **~~V6~~ (fixed):** `ImportBlocked` was being re-`Check`ed every refresh.
  Fixed in `294c73936` by `IsBlockedByUnfindableMetadata` guard at
  `CompletedDownloadService.Check` L80 — but only for our "unfindable" case.
  The original `ImportBlocked` cases (artist mismatch, unable to parse) are
  still re-checked, which is desired (user might fix them).

- **~~V7~~ (fixed):** AutoRetryFailedImportsOnStartupHandler now listens for
  `TrackedDownloadRefreshedEvent` (which fires immediately *after* the cache
  is populated and immediately *before* the next `Execute` pass) instead of
  trying to do its work on `ApplicationStartedEvent` (when the cache is still
  empty). The handler still reacts to `ApplicationStartedEvent` for the
  unrelated startup work (logging, RescanFolders, queueing the first
  RefreshMonitoredDownloads), but the reset itself is in the right place.

- **~~V8~~ (fixed):** The V11 guard (`IsBlockedByUnfindableMetadata`) only
  matched its prefixes against entries in `Messages`, not `Title`. The V3 path
  uses `Warn(format, args)` which puts the reason in `Messages` (with
  `Title=DownloadItem.Title`), but the V5 path uses
  `Warn(new TrackedDownloadStatusMessage(error, []))` which puts the reason in
  `Title` with empty `Messages`. The guard early-returned `false` whenever
  `Messages` was empty, so V5-classified items (`Found archive file`,
  `Caution: Found executable`, `Could not parse file for import`) bounced
  ImportBlocked → ImportPending forever via Check. **Fix:** renamed to
  `IsBlockedByPersistentFailure`, walks both Title and Messages, and adds a
  `_persistentImportFailurePrefixes` bucket covering V3/V5 messages so we
  recognise the full set of "won't change without user intervention" cases.

- **~~V9~~ (fixed; same fix as V8):** V3's `"No files found are eligible for
  import in {0}"` message wasn't in `_unfindableInMetadataPrefixes`, so empty
  download folders also bounced forever. Fixed by V8's expanded prefix bucket.

- **~~V10~~ (fixed):** Deterministic exceptions thrown inside `Import()`
  (specifically `NotParentException` from `RemoveExistingTrackFiles` when an
  artist's TrackFile path isn't under its current root folder) propagated up
  to `DownloadProcessingService.Execute`'s catch block, which reset State
  Importing → ImportPending and silently looped forever. Observed in
  production: Rod Stewart 14CD BoxSet (131 FLAC files, fully identified)
  threw the same exception every refresh because the artist record had been
  moved between root folders without moving the existing files. **Fix:**
  `Import()` now catches `NotParentException` explicitly, attaches the
  exception message as a status message, and sets ImportBlocked. The user
  sees the actual problem in the queue and can fix the artist's root folder
  via the UI; the V11 guard then keeps the item from being re-attempted
  every refresh.

- **~~V11~~ (fixed; cache hygiene):** The new in-memory import-decision
  cache (`ImportDecisionMaker._decisionCache`) holds direct references to
  Artist/Album/AlbumRelease records inside the cached `LocalTrack`s. When a
  user edits an artist (path, root folder, profile) the cached decisions
  become stale and re-using them can throw — observed as the Rod Stewart
  NotParentException continuing to fire even after the artist record was
  fixed, until Lidarr was restarted. **Fix:** `ImportDecisionMaker` now
  implements `IHandle<ArtistEditedEvent>`, `IHandle<ArtistMovedEvent>`,
  `IHandle<ArtistsDeletedEvent>`, `IHandle<AlbumEditedEvent>`, and
  `IHandle<AlbumDeletedEvent>`, all of which call `_decisionCache.Clear()`.
  Cost is one round of identification on the next Import call (~25s cold per
  album folder), which is rare relative to user-edit frequency.
