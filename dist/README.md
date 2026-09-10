# VSaver — Setup (read me first)

VSaver keeps one Valheim world in sync between friends through a shared Google Drive
folder. One person plays at a time; saves upload automatically; everyone else pulls the
latest world before their turn.

This zip contains everything you need for your **first install**. After this, VSaver keeps
itself up to date automatically — you won't have to download it again.

---

## 1. Unzip — keep the two files together

Unzip this folder somewhere you'll find it again, e.g. a `VSaver` folder on your Desktop.

Inside you should see:

```
VSaver.exe          ← the app
credentials.json    ← the key that lets it reach Google Drive
README.md           ← this guide
```

> ⚠️ **`VSaver.exe` and `credentials.json` must stay in the SAME folder.** If you move the
> exe, move `credentials.json` with it. If they get separated, the app will show a
> "credentials.json not found" error on startup.

---

## 2. Run it

Double-click **`VSaver.exe`**.

- Windows may show a blue **"Windows protected your PC"** box (because the app isn't
  code-signed). Click **More info → Run anyway**. This is expected for a small friend-group
  app.

---

## 3. Connect

1. Type your **name** (used to show who's holding the world).
2. **Server folder ID:** if this box is already filled in, leave it. If it's empty, paste the
   shared folder ID your group's organizer sent you.
3. Click **Connect**.
4. A browser opens for **Google sign-in** (only the first time on this PC). You'll see a
   *"Google hasn't verified this app"* warning — that's normal for a private app. Click
   **Continue / Advanced → Go to VSaver** and allow access.
5. Tick the world(s) you want to sync.

> ⚠️ **Your world must already be in Valheim's current save format.** If a world you expect to
> see isn't listed, open it in an up-to-date Valheim once — it upgrades the save automatically —
> then reopen VSaver.

---

## 4. Playing — respect the lock

Valheim worlds **cannot be merged**, so only one person plays at a time:

- **Before you play:** click **Play** on the world. This takes the lock so nobody else
  overwrites your session (it can also launch Valheim for you).
- **When you stop:** click **Done**. This uploads your final save and releases the lock so
  the next person can take their turn.

If someone else holds the lock, the world shows as in use — wait for them to click Done.

---

## 5. It runs in the background

Clicking the window's **X** does not quit VSaver — it hides to the **system tray** (bottom-right,
by the clock) so syncing keeps running while you play.

- **Left-click** the tray icon to bring the window back.
- **Right-click → Quit** to actually exit.

Leaving it running in the tray is the intended mode — that's what uploads your world while
you play and pulls friends' changes.

---

## Troubleshooting

**"credentials.json not found"** — The app lists the exact folders it checked. Make sure:
- `credentials.json` is in the **same folder** as `VSaver.exe`.
- It's named **exactly** `credentials.json`. Windows often hides file extensions, so a file
  that looks like `credentials.json` might really be `credentials.json.txt`. Turn on
  **File Explorer → View → File name extensions** and rename it if needed.

**Sign-in asks again every week** — Google expires test-mode logins after 7 days. Ask your
group's organizer to click **Publish app** on their Google OAuth consent screen (keeps the
unverified warning, but stops the weekly re-login).

**Downloaded a world but it's not in Valheim's world list** — Open Valheim after the download
finishes; VSaver drops synced worlds into Valheim's own save folder so they appear in the
in-game list on the next launch.

**The app never seems to update** — Updates are applied on a **cold launch**, and only when no
copy is already running. Because VSaver stays in the system tray, re-launching the exe just
re-opens the tray copy without updating. To force an update: **right-click the tray icon →
Quit**, then open `VSaver.exe` again. Also make sure the exe is in a **writable** folder (not
inside the zip or a read-only location).

If it still won't update, check the log at
`%AppData%\ValheimSync\update.log` (paste `%AppData%\ValheimSync` into the File Explorer
address bar) — it records the running version, the latest release, and the exact reason an
update was skipped.
