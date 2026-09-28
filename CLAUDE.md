# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

ValheimSync ("VSaver") — a Windows desktop app that passes a Valheim world around a friend group via a shared Google Drive folder. One person plays at a time (advisory cloud lock), saves auto-upload after Valheim finishes writing, everyone else pulls the latest before their turn. Valheim saves cannot be merged, so conflicts are *prevented* (lock), never resolved.

**Stack:** .NET 8, Avalonia 11 (Fluent theme, MVVM via CommunityToolkit.Mvvm source generators), Google Drive v3 API. Windows-only paths by design.

## Commands

```powershell
dotnet build ValheimSync.sln                                  # build everything
dotnet run --project src/ValheimSync.App                      # run the app (dev)
dotnet test tests/ValheimSync.Tests/ValheimSync.Tests.csproj  # run all tests
dotnet test tests/ValheimSync.Tests/ValheimSync.Tests.csproj --filter "FullyQualifiedName~SyncEngineTests"  # one class
pwsh scripts/release.ps1                                      # dry run: build exe + install zip
pwsh scripts/release.ps1 -Publish -Notes "What changed"       # publish a GitHub Release
```

Tests are xUnit in `tests/ValheimSync.Tests`. `SyncEngine` is tested through `Fakes/FakeCloudProvider` (in-memory `ICloudStorageProvider` that records call order and computes real MD5s). SyncEngine tests early-return if Valheim is actually running (the engine's static `ValheimProcess` checks are real), use unique world names, and clean up anything mirrored into the machine's real LocalLow folder. `MainWindowViewModel` internals (e.g. `NormalizeFolderId`) are reachable via `InternalsVisibleTo`. Never call `GoogleDriveStorageProvider.ClearCachedToken()` from a test — it deletes the developer's real cached OAuth token.

Build gotcha: if the app is running (often hidden in the system tray), the build fails with MSB3026/MSB3027 file-lock errors on `ValheimSync.Core.dll` — that's not a compile error. Quit the app from the tray, or redirect output: `dotnet test … --output <scratch dir>`.

## Architecture

Two projects, strict one-way dependency:

- **`src/ValheimSync.Core`** — all sync logic, zero UI dependencies. Models, `ICloudStorageProvider` + `GoogleDriveStorageProvider`, `SyncEngine`, world discovery/watching, self-updater.
- **`src/ValheimSync.App`** — Avalonia shell. Almost all behavior lives in `MainWindowViewModel`; `Program.cs` handles single-instance + auto-update before the UI starts.

### The sync protocol (invariants — do not break these)

`SyncEngine` (Core/Sync/SyncEngine.cs) coordinates everything; all sync passes serialize through one `SemaphoreSlim`.

1. **A world's whole file set travels as one deterministic zip.** Modern Valheim saves a world as a versioned `_main.<N>.db2`/`.fwl2`/`.chunks`/`.ok` record plus one `*.chunk` file per zone that's only rewritten when that zone actually changed (`WorldScanner.Scan` picks the highest revision whose four main files all exist; live `.chunk` files are trusted straight from the directory listing — Valheim itself prunes superseded ones). Only this modern format is supported; Valheim's old flat `.db`/`.fwl` pair is not recognized at all. Every sync pass packs the local world's current file set into a single archive named `<world>.zip` (`WorldZip.CreateAsync`, `WorldArchive.ZipName`) and transfers that one file — one Drive object, one API call each way, instead of one per chunk. `WorldZip` sorts entries by filename and pins every entry's timestamp, so two zips built from identical content always produce identical bytes; this determinism is what makes MD5 comparison still work (see below). **Trade-off:** unlike the old per-file scheme, an unchanged chunk is *not* skipped — any divergence anywhere re-transfers the entire archive. This was a deliberate choice to cut Drive API call volume at the cost of some redundant bytes on partial changes.
2. **Change detection is MD5 equality** between the freshly-built local zip and Drive's reported `md5Checksum` for `<world>.zip` (`Util/Hashing.cs`, lowercase hex). Identical hash → no transfer, ever. Because the whole save is one Drive object, there is no partial/"torn" state to detect the way the old scattered-files scheme needed a manifest for: Drive never exposes a half-written file to a reader, so a `<world>.zip` that exists at all is trusted as complete. The four `_main.<N>.*` main-record files are zipped under a revision-stripped entry name (`WorldRevision.NormalizeEntryName`, e.g. `_main.db2`) so this hash depends only on actual save content, never on the local, arbitrary revision counter — see point 4.
3. **Never upload mid-write:** the `FileSystemWatcher` (`DebouncedWorldWatcher`) fires only after files are quiet for `DebounceSeconds` (default 60 s). While Valheim runs, an in-game timer pushes the save every `InGameUploadMinutes` — but only if it has settled.
4. **Never download while Valheim is running.** Downloads go to a temp zip file, are hash-verified, unzipped to a temp folder (`WorldZip.ExtractAsync`), then the live world folder — `AppSettings.WorldsPath`, i.e. Valheim's LocalLow folder — is swapped in one move (keeping a `.synbak` copy of what was replaced under `backups/worlds/<world>.synbak` next to the exe — file-by-file via `File.Move`, not `Directory.Move`, since the temp folder and the worlds path can be on different volumes). Steam's Cloud `remote` folder is never read or written by any part of the sync engine — see *Save-folder discovery* above. Upload-vs-download direction prefers the Drive-stored revision number (see point 7) over local/remote timestamps specifically to avoid trusting a folder's mtime as a proxy for freshness.
5. **Locking:** `<World>.lock` is a JSON file (`WorldLock`) in the Drive folder. Whoever holds it may upload. Locks go stale after 12 h. Drive has no compare-and-swap — `TryAcquireLockAsync` re-reads after writing to shrink the race window; that's accepted for a friend group.
6. **One rolling remote backup per world:** the *first* upload of a play session server-side-copies the current `<world>.zip` to `<world>.zip.bak` (`CopyAsync`, `WorldArchive.BackupZipName`) — a single call, since there's only one file to back up. A "session" = one run of Valheim, detected by the not-running→running transition (`UpdateSessionState`); resetting on session *start*, not exit, keeps the final save-on-exit upload from clobbering the pre-session snapshot. The backed-up set is persisted (`BackupSessionState`, `sessionstate.json` next to the exe) so an app restart mid-session can't retrigger it either. Backups are best-effort and never block the upload.
7. Upload-vs-download direction: I upload if I hold the lock, or nothing is remote, or (no one holds the lock and my file isn't older). Otherwise download. "Older/newer" is decided by the world's `_main.<N>` revision number when Drive has one recorded for the remote zip (stored as upload-time `appProperties`, compared against the local revision) — only falling back to comparing local/remote timestamps when Drive has no revision on file, or the two revisions are equal.
8. **Sync failures upload a detailed error log.** Any exception that reaches `SafeSyncWorldAsync`'s catch blocks (a stalled transfer past `_syncTimeout`, a corrupt/hash-mismatched download, anything else) is written to a `.txt` (timestamp, player, world, provider, app version, full exception) and uploaded to the shared Drive folder as `errorlog_<world>_<player>_<UTC timestamp>.txt` (`SyncEngine.TryUploadErrorLogAsync`) — so a failure is diagnosable by anyone in the group, not just visible in one machine's local log. Best-effort and never fires for a real shutdown/cancellation, only genuine failures.

### Play flow (MainWindowViewModel)

Both Play buttons **sync with Drive and wait for completion before launching the game** (`SyncBeforeLaunchAsync`); if the sync fails the game is not launched. The per-world Play syncs *before* taking the lock (so it downloads the newest rather than uploading a stale local under the lock), then: acquire lock → launch via `steam://run/892970` → background-watch the process (`StartValheimSession`) → on game exit, upload final save and auto-release the lock. A `<world>.zip.bak` remote backup, a `<world>.lock` file, and a `<world>.synbak` local sibling folder are all inert to the engine's world-name matching (world identity comes from the exact `<world>.zip` filename remotely and the exact subfolder name locally).

The Drive folder id field accepts a full share URL; `NormalizeFolderId` trims it to the bare id.

### App lifecycle (Program.cs / App.axaml.cs)

- **Single instance** via named mutex; a second launch signals a named event so the running (possibly tray-hidden) instance surfaces its window, then exits. Note: that second-launch path skips the update check.
- **Auto-update** (`Core/Update/Updater.cs`) runs before the UI: compares assembly `<Version>` against the latest GitHub Release tag of `Ltechsystem/VSaver`, downloads asset `VSaver.exe`, swaps and relaunches. Skips itself under `dotnet run`. Everything best-effort/silent.
- Closing the window hides to the system tray; syncing continues. Quit is explicit via the tray menu.

### Save-folder discovery (`ValheimSaveLocations`)

Worlds live under `<user>\AppData\LocalLow\IronGate\Valheim\worlds_local` (or `worlds`) — Valheim's own "local storage" folder, the one the game itself reads and writes — each world as its own subfolder, probed and ranked by newest `.fwl2` (found recursively, one folder down inside each world). Steam's Cloud `remote` cache (`<Steam>\userdata\<account>\892970\remote`) is a separate copy Steam maintains on its own schedule and is **never auto-detected or touched** — reading it risks treating a stale cloud copy as current, and writing it behind Steam's back can make Manage Saves offer that stale copy against the fresh one, or let Steam's own cloud reconciliation silently overwrite what was written. If LocalLow can't be found (Valheim has never launched on this machine) `AppSettings.WorldsPath` throws a detailed error. `WorldsPathOverride` in settings.json forces a path instead, bypassing detection entirely.

## Releasing

1. Bump `<Version>` in `src/ValheimSync.App/ValheimSync.App.csproj` — the GitHub Release tag must be `v<version>` and drives auto-update; never reuse a tag.
2. `pwsh scripts/release.ps1 -Publish -Notes "..."` — builds the self-contained single-file `VSaver.exe` (assembly name is `VSaver`, matching the updater's asset name) and a `dist/VSaver-v<version>.zip` install bundle, attaches both to the release via `gh`.

## Secrets — never in source or releases

- `credentials.json` (real Google OAuth client) is gitignored; only `credentials.json.example` placeholders ship. The release zip bundles the *placeholder*.
- The shared Drive folder id is private to the group: entered in the app, stored only in the user's local `settings.json`. Never hardcode it anywhere, including tests and docs.
