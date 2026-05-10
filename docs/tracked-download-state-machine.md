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
       ┌───────────────┐  ▶ V11: our IsBlockedByUnfindableMetadata
       │ ImportBlocked │     guard at CompletedDownloadService.Check L80
       └──────┬────────┘     short-circuits when the existing messages
              │              say the previous run already classified
              │              this as unfindable. Without it, Check
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
       │                       │   every Execute pass                   │
       │                       ▼                                        │
       │                ImportPending  ───────────────────────────────►─┤
       │                ✗ V2: unbounded retry, no backoff (open issue)  │

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
| `DownloadProcessingService.ResetFailedImportsForRetry` (L47) | `ImportFailed` → `ImportPending` | Every `Execute` pass — top of `ProcessMonitoredDownloadsCommand` |
| `DownloadProcessingService.Execute` "normalize Importing" (L94-101) | `Importing` (DC=Completed) → `ImportPending` | Top of every `Execute` pass — recovers items left mid-Import by a previous crash |
| `DownloadProcessingService.Execute` catch (L118-122) | `Importing` → `ImportPending` | Exception thrown inside `Import()` |
| `AutoRetryFailedImportsOnStartupHandler.ResetFailedImportsForRetry` (L85) | `ImportFailed` → `ImportPending` | `ApplicationStartedEvent` — **fires before TrackedDownload cache is populated, so usually a no-op**; the in-loop reset above is what actually drains ImportFailed after a deploy |

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

- **V2: `ImportFailed` retries with no backoff.** Every refresh,
  `ResetFailedImportsForRetry` moves all `ImportFailed` → `ImportPending`.
  Items that fail for a "fixable" reason (album-match-not-close-enough at 17%,
  destination already exists, etc.) almost certainly fail the same way next
  time. With our `UpgradeTrackFile` idempotency this is CPU-only, but it's
  still ~10 items × identification work × every refresh forever. **Open —
  needs a per-item attempt counter or last-attempt timestamp.**

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

- **V7: AutoRetryFailedImportsOnStartupHandler is timing-sensitive.** Fires on
  `ApplicationStartedEvent` but the `TrackedDownloadService` cache is empty
  until the first `RefreshMonitoredDownloads` populates it. The
  `DownloadProcessingService` in-loop reset is what actually does the work;
  the startup-handler call is now a no-op safety net (left in place because
  in some restart-timing scenarios the cache *is* warm and it does fire).
