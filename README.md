# ValheimSync

Keep one Valheim world in sync between a group of friends via a shared Google Drive folder. One person plays at a time (enforced with a cloud lock file), saves upload automatically after Valheim finishes writing them, and everyone else pulls the latest world before their turn.

**Stack:** .NET 8, Avalonia 11 (Fluent, MVVM via CommunityToolkit), Google Drive v3 API.

> **Requires Valheim's current save format.** Worlds saved in the old, flat `<World>.db` + `<World>.fwl` layout aren't recognized — open the world in an up-to-date Valheim once (it upgrades the on-disk save to the new per-world folder layout automatically) before adding it as a server here. There's no compatibility fallback for the old format.

```
ValheimSync/
├── src/
│   ├── ValheimSync.Core/          # No UI dependencies — all sync logic lives here
│   │   ├── Models/                # WorldSave, RemoteFile, WorldLock, SyncStatus
│   │   ├── Storage/               # ICloudStorageProvider + GoogleDriveStorageProvider
│   │   ├── Sync/                  # WorldScanner, DebouncedWorldWatcher, SyncEngine
│   │   └── Util/                  # MD5 hashing (matches Drive's md5Checksum)
│   └── ValheimSync.App/           # Avalonia desktop app
└── ValheimSync.sln
```

---

## How the sync works (30-second version)

1. A `FileSystemWatcher` watches `worlds_local`, recursively (each world is its own subfolder). When Valheim writes a save, we wait until the files have been **quiet for 60 s** (debounce) before uploading — never a mid-write upload. Additionally, **while Valheim is open the current save is pushed every 5 minutes** (configurable via `InGameUploadMinutes`) as long as it actually changed and has settled — so a crash never loses more than that interval and friends can watch progress without waiting for you to quit.
2. A **15-minute poll** (configurable) is the fallback and the download path: it compares every file's local MD5 against Drive's `md5Checksum`. Identical hash → nothing is transferred — including the terrain files Valheim didn't touch, since a world save is now many files (one per changed zone) rather than one giant file. **No duplicates, ever.**
3. Every changed file uploads, then a tiny `manifest.commit` marker uploads last — it's the "commit marker" for the whole set, so a half-finished upload never looks like a valid world.
4. Downloads go to a temp file, are hash-verified, a `.synbak` copy of your old world folder is kept, and only then is the new folder moved into place. Never while Valheim is running.
5. **Locking:** before playing, click **Play** — the app writes `<World>.lock` (your name + timestamp) to the Drive folder. Everyone else's app sees the lock and shows the world as in use. Click **Done** when you stop: it pushes your final save and releases the lock. Locks older than 12 h are treated as stale (crashed client). Note: Drive has no atomic compare-and-swap, so two people clicking Play in the same second could theoretically race — fine for a friend group, and the app re-checks after writing to shrink the window.

> ⚠️ Valheim worlds cannot be merged. The lock exists because if two people play "their" copy simultaneously, whoever uploads last silently destroys the other's progress. Respect the lock. If you want *simultaneous* multiplayer, run a dedicated server instead — this tool is for "pass the world around" play.

---

## Part 1 — Get your Google API credentials (one person does this, ~10 minutes)

> ## 🔑 You must download a `credentials.json` from Google — the app cannot connect without it.
> There is **no built-in API key** and none is shipped with the app. You create your own OAuth
> client in the Google Cloud Console (free) and **download the JSON file yourself**. The
> `credentials.json.example` in this repo is only a placeholder showing the expected shape — it
> contains **no real credentials** and will not work until you replace it with your own download.

Drive file access uses **OAuth**, which means a `credentials.json` file instead of a key string. One person in the group creates it; everyone else just receives the file.

1. Go to **https://console.cloud.google.com** and sign in with any Google account.
2. Top bar → project dropdown → **New Project**. Name it `ValheimSync`, click **Create**, then make sure it's selected.
3. Left menu → **APIs & Services → Library**. Search **Google Drive API** → **Enable**.
4. **APIs & Services → OAuth consent screen**:
   - User type: **External** → Create.
   - App name: `ValheimSync`, your email in both email fields. Skip logo/domains. Save through the Scopes page (add nothing).
   - On **Test users**: click **Add users** and enter the **Google email address of every friend** who will use the app (including yourself). This is important — while the app is in "Testing" mode, only listed test users can sign in.
5. **APIs & Services → Credentials → Create Credentials → OAuth client ID**:
   - Application type: **Desktop app**
   - Name: `ValheimSync Desktop`
   - Click **Create** → in the dialog click **⬇ Download JSON**. **This downloaded file is your `credentials.json`** — without it the app has nothing to authenticate with.
6. **Rename** the downloaded file to exactly **`credentials.json`** and place it:
   - **Building from source?** Put it in `src/ValheimSync.App/` (it gets copied next to the exe on build). It replaces the placeholder `credentials.json.example`.
   - **Running the released exe?** Put it in the **same folder as `VSaver.exe`**.
   - ✅ Sanity check: open the file — it should contain a real `client_id` ending in `.apps.googleusercontent.com`, **not** the `YOUR_CLIENT_ID` placeholder text.

### Things to know about "Testing" mode
- Everyone will see an *"Google hasn't verified this app"* warning during the one-time sign-in. That's expected — click **Continue**. It's your own app; verification is only needed for public distribution.
- Google may expire OAuth tokens after **7 days** in Testing mode, which means re-consenting weekly. To avoid that: on the OAuth consent screen page click **Publish app** (moves it to "In production"). You'll keep the unverified warning but tokens stop expiring. For a private friend group this is the pragmatic choice.
- The app requests the full `drive` scope, not the more restrictive `drive.file` — `drive.file` only sees files the app itself created, which would 404 on a folder someone else created for the group in the Drive web UI. Full scope means a wordier consent-screen warning, but it's what makes a pre-existing shared folder work at all. It still only ever touches the one folder you give it (see the "don't drag files in manually" note in Part 2, though — visibility isn't the same as the app recognizing what it sees).

### Is it OK to share credentials.json with friends?
For a **Desktop app** OAuth client, Google itself documents that the "client secret" is not treated as a secret (it ships inside every installed app). Each friend still signs in with *their own* Google account, and their consent — not the shared `credentials.json` — is what actually grants access (see above for what that access covers: the full `drive` scope, not just app-created files). So yes — bundling `credentials.json` with the app for your group is fine. Just don't commit it to a public GitHub repo (it's already in `.gitignore`).

---

## Part 2 — Create the shared folder (same person, 2 minutes)

1. In **drive.google.com**, create a folder, e.g. `ValheimSync`.
2. Right-click → **Share** → add each friend's Google email as **Editor**.
3. Open the folder and copy the ID from the URL:
   `https://drive.google.com/drive/folders/`**`1aBcD3FgHiJkLmNoP...`** ← that last part is the **folder ID**.
4. Give the folder ID to your friends. It is **never** baked into the app or a public release
   (the folder is private to your group). Each user provides it one of two ways:
   - **They paste it** into the app's **Server folder ID** field on first run, or
   - **You pre-seed it** into the private install zip's `settings.json` so they don't have to —
     pass it to the release script with `-DriveFolderId <id>` (see Part 3). Either way it's
     stored only in that user's local `settings.json`, never in the repo.

> Heads up: the *first* upload of each world has to happen through the app itself — don't manually drag save files into the folder through the Drive website. It's not a permissions issue (the app's full `drive` scope would see manually-added files fine); it's a naming one: a world's files are stored **flat**, all directly in the shared folder, with the world's name as a literal `<world>/` prefix in each filename (Drive allows `/` in a filename — it doesn't create a real subfolder). If you drag a folder into Drive through the website, Drive creates an actual nested folder there instead, and the app's listing (which only looks at files directly inside the shared folder) will never see what's in it.

---

## Part 3 — Build & distribute (with auto-update)

The app updates itself from GitHub Releases: on every launch it asks GitHub for the
latest release, and if that release is newer than the running build it silently downloads
the new exe, swaps it in, and relaunches — so friends always run the current version
without ever reinstalling.

**One-time setup:** create a **public** GitHub repo for this project and set its slug in
`src/ValheimSync.Core/Update/Updater.cs`:

```csharp
public const string Repo = "your-github-name/ValheimSync";
```

> The repo must be **public** so the app can download releases without a token. Keep
> `credentials.json` out of it — it's already in `.gitignore`; distribute it separately (see below).

### To cut a release (every time you publish an update)

1. **Bump the version** in `src/ValheimSync.App/ValheimSync.App.csproj`:
   ```xml
   <Version>1.0.1</Version>
   ```
2. **Build + package** with the release script (reads the version from the csproj):
   ```powershell
   pwsh scripts/release.ps1                                   # dry run: builds the exe + install zip
   pwsh scripts/release.ps1 -Publish -Notes "What changed"    # publishes the exe + install zip
   ```
   The script builds the self-contained single-file `VSaver.exe` and a
   `dist/VSaver-v1.0.1.zip` (exe + `credentials.json` + `settings.json` + `README.md`).
   **Both assets are safe to publish** — nothing secret is baked in:
   - **`VSaver.exe`** — the auto-updater target; downloaded by this exact name.
   - **`VSaver-v1.0.1.zip`** — the first-time-install bundle. Its `credentials.json` is the
     **placeholder template** (from `credentials.json.example`) and its `settings.json` ships a
     **blank** Drive folder id. Users supply the real `credentials.json` and folder id themselves.

   With `-Publish`, **both** the bare exe and the zip attach to the GitHub Release.

That's it — the next time anyone opens the app, it upgrades itself to `v1.0.1`.

> The tag drives updates: the app compares its own `<Version>` against the latest release's
> tag (leading `v` optional). If the tag isn't higher, nothing happens. Never reuse a tag.

> 🔒 The real `credentials.json` and the shared Drive folder id are **never** part of the
> build — credentials come from each group's own Google Cloud project, and the folder id is
> entered in the app and stored only in the user's local `settings.json`.

### First-time distribution (the only manual install)

New users can't auto-update *into* their first copy, so send a new friend the
**`VSaver-v*.zip`** — it's attached to every release. It contains:
- `VSaver.exe`
- `credentials.json` — the **placeholder template** (they replace it with your group's real one)
- `settings.json` — a blank Drive folder id they fill in via the app's **Server folder ID** field
- `README.md` — a friendly walkthrough (`dist/README.md` in this repo)

They unzip it to a normal folder (keeping the files **together**), drop in the real
`credentials.json` you send them, run the exe, and paste the shared folder id into the app's
**Server folder ID** field once. From then on updates are automatic — updates ship only the
exe, and the `credentials.json` + `settings.json` already sitting next to it keep working
across every future version.

## Part 3.5 — Running in the background

Clicking the window's **X** doesn't quit the app — it hides to the **system tray** so syncing
keeps running. From the tray icon: **left-click** (or right-click → **Open ValheimSync**) to bring
the window back, or right-click → **Quit** to actually exit. Leaving it running in the tray is the
intended mode — that's what keeps your world uploading while you play (see below).

## Part 4 — First run (every user)

1. Run `VSaver.exe`.
2. Enter your name and paste the shared **folder ID** → **Connect**.
3. A browser opens for Google sign-in (once per machine — the token is cached in `%APPDATA%\ValheimSync\token`). Click through the unverified-app warning.
4. Tick the world(s) you want to sync.
5. Before playing: click **Play** on the world (takes the lock). When you quit Valheim: click **Done** (final upload + lock release).

Settings persist in `settings.json` next to the exe.

### `settings.json` reference

`settings.json` lives next to the exe and is written on first run. You can also create it
ahead of time (copy `src/ValheimSync.App/settings.json.example`) to preset the shared folder
for friends. **It's plain JSON — no comments.** Fields:

| Field | Default | Meaning |
|-------|---------|---------|
| `DriveFolderId` | `""` | The shared Google Drive folder ID everyone syncs against. Blank on a fresh install — the user pastes it in the app's **Server folder ID** field (or you pre-seed it via the private zip). Never hardcoded in source. |
| `PlayerName` | `""` | Shown on locks; auto-filled from your Google email on first connect. |
| `WorldsPathOverride` | `null` | Force a specific Valheim worlds folder (else auto-detected). |
| `PollIntervalMinutes` | `5` | How often to check Drive for remote changes. |
| `DebounceSeconds` | `60` | How long a save must be quiet before it's trusted as complete. |
| `InGameUploadMinutes` | `5` | While Valheim is open, how often to push the in-progress save. |
| `SelectedWorlds` | `[]` | World names ticked for syncing. |

> Changing the Drive folder mid-stream points the app at a different world set — set it once
> for the group and leave it. Edits take effect on the next launch.

---

## Extending it

- **New cloud provider:** implement `ICloudStorageProvider` (7 methods) — the engine and UI don't change. Dropbox is a good second target (simpler auth for small groups).
- **Tests:** `SyncEngine` takes `ICloudStorageProvider` via constructor, so an in-memory fake makes the whole decision logic unit-testable — same pattern as your CBS project's SPI seams.

Already built in: **auto-launch Valheim** on Play (with auto lock-release on exit), **system tray** background mode (Part 3.5), **in-game periodic upload** (point 1 above), and **GitHub auto-update** (Part 3).

## Known limitations (by design, for a starter)

- Windows-only paths for the Valheim save folder (Linux/macOS would need path variants).
- Lock is advisory — the app enforces it in its own UI, but nothing stops someone from launching Valheim directly. Pairing Play with auto-launch (above) mitigates this socially.
- `FindByNameAsync` lists the whole shared folder per lookup, and every file upload/download/copy/delete does one — a world sync can mean a couple dozen files (main record + one per changed zone), so that's a couple dozen full-folder listings per sync. Fine for a friend group's handful of worlds; cache the listing per sync pass if this ever needs to scale up.
- No conflict *merge* — Valheim saves can't be merged, so conflicts are prevented (lock), not resolved.
