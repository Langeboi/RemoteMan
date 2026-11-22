# RemoteMan

A fast, lean remote connection manager for Windows: RDP, SSH, web and VNC in one window,
with a shared team database as an option. Built for labs, homelabs and schoolwork.

## Build

Requires Windows 10/11 and the .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`).

```powershell
dotnet build RemoteMan.sln -c Release
dotnet run --project src/RemoteMan.App
```

The first build downloads xterm.js (MIT) into `src/RemoteMan.App/Assets/terminal/vendor`.
WebView2 runtime ships with Windows 11 and current Windows 10.

## What works in this first cut

- **Dashboard** tab, pinned first and open at startup: totals, entries by protocol, largest folders, Zabbix
  host states, open problems and a 24 hour trend, your connections per day and by weekday/hour (a local
  log of the sessions you open), entry growth, favorites and recent (double-click to connect), housekeeping checks
  (missing credentials, duplicate hosts, entries that ask for a login, unused credentials). Widgets button
  turns each one on or off.
- Tree of folders, RDP, SSH, web, VNC and credential entries. Search box doubles as quick connect
  (`ssh host`, `rdp://host:3390`, `https://…`).
- Several hosts per entry: primary first, then fallbacks, each probed with a 2 s TCP check.
- **Ctrl+click** a credential while a connection is selected to link it.
- Login per entry or folder: none, inherited from the folder, a saved credential, your Windows login, or typed
  into the entry. Credential types: user name + password (+ domain), SSH key (stored, generated, or a key file
  on disk), client certificate (.pfx), secret / API key; "ask for the password when connecting" is optional.
- Right-click menu: connect / connect with (mstsc, another credential, Windows login, admin session), edit,
  duplicate, move to, favorites, copy user / password / both / host, link credential, open with (mstsc,
  .rdp file, WinSCP, ping), properties and history, delete. Drag entries onto folders to move them.
- Duplicate (Ctrl+D) opens the editor on the copy so you change what differs before saving.
- Copy user name (Ctrl+Shift+U), password (Ctrl+Shift+P) or both (Ctrl+Shift+B: paste once for the user
  name, again for the password). Passwords stay out of clipboard history and are cleared after 30 s.
- RDP embedded (Windows' own mstscax control), resizes with the tab, no certificate prompts.
- Many RDP sessions stay light: the client offers the RDP 8+ graphics pipeline so servers with H.264/AVC
  enabled stream video-coded frames that Windows decodes on the GPU; background tabs are hidden at the
  window level and drop their mouse hook.
- SSH in an xterm.js terminal: host keys accepted automatically (trust on first use, changes logged),
  modern and legacy algorithms, password / keyboard-interactive / key auth (OpenSSH, PEM, PuTTY .ppk).
- Web in embedded Chromium (WebView2) with certificate errors skipped and automatic login (finds the user
  and password boxes, fills them, presses login; handles late-loading and two-step login pages, with
  optional CSS selectors per entry), or opened
  in Edge, Chrome or Brave in a separate window.
- Copy on select in SSH, RDP and web tabs (no Ctrl+C needed); right click pastes in SSH.
- RDP file transfer over the RDP clipboard: copy files on one side, paste on the other, or drop files on
  the tab's side strip and press Ctrl+V in the remote folder.
- SSH **Files** panel (Ctrl+Shift+F): SFTP over the same login, browse, upload/download files and whole
  folders, drop files on the terminal or panel to upload.
- VNC through an installed viewer (TigerVNC, TightVNC, RealVNC, UltraVNC).
- SSH **jump hosts**: pick another SSH entry to connect through (chains, each hop with its own login and
  fallback hosts). The terminal and the Files panel both go through it.
- **Snippets** panel in SSH tabs (Ctrl+Shift+S): saved commands sent to this tab or to several open SSH
  tabs at once, with `{host}`, `{user}` and `{name}` filled in per session. Snippets are filed as
  `Category/Group`; the panel shows one category at a time with collapsible groups, and the filter searches
  them all. 111 built-in snippets for Debian (40), FortiOS 7.6 (34) and MikroTik RouterOS 7.25 (37) are
  added on first run. Snippets are typed, not run: you check them, fill in an IP or interface where one is
  marked "fill in", and press Enter (tick "Also press Enter" to run one straight away). Snippets
  live in the data source, so a team shares them.
- **Import** (File > Import): Devolutions RDM exports (.rdm XML or JSON), .rdp files, PuTTY saved sessions
  (from the registry or a .reg export, with .ppk keys). Preview with tick boxes, duplicates skipped.
- **Export and backup** (File > Export and backup): password-encrypted `.rmx` files (AES-256-GCM, PBKDF2
  600k) that open on any PC into any data source; optional daily automatic backup keeping the newest N;
  restore into a new folder or merge back in place.
- Data sources: local SQLite (default), or shared PostgreSQL, SQL Server or MySQL/MariaDB with
  multi-server failover, live updates and presence (who is viewing, editing or connected).
- **Team chat** on shared databases: a contact list of groups and people, direct messages, and `#` links to
  entries, folders, credentials (name only), snippets and config backups that open in the reader's tree.
  Messages are stored encrypted with the team vault key.
- **AI assistant** (button at the top right, Ctrl+Shift+A): pick Claude, ChatGPT, Gemini, Mistral, a
  local Ollama server or any OpenAI-compatible endpoint. Signed in, it waits on standby; the button opens
  a chat box styled like the chosen provider. With an API key it can read the active SSH session's last
  50 lines (toggle per message) and type a suggested command into the terminal without running it.
  Account sign-in instead shows the provider's own chat page, without session access.
- Users are the database login (local files: your Windows account). Every entry records when it was
  created and last modified, and by whom (shown in the properties pane).
- Optional single sign-on: a shared database can use your Windows login (SQL Server integrated security,
  PostgreSQL/MySQL Kerberos), and RDP and web entries can log in as "(my Windows login)".
- Offline mode: if the database drops, the Q turns red, entries keep working from memory (or from an
  encrypted local cache at startup), edits wait, and RemoteMan retries every 15 s. When the database is
  back the Q turns green and everything missed shows up live.

## Install

Download `RemoteMan-win-Setup.exe` from the [latest release](https://github.com/Langeboi/RemoteMan/releases/latest)
and run it. It installs for your Windows user (no admin prompt) into `%LocalAppData%\RemoteMan`, adds
Start menu and desktop shortcuts, registers RemoteMan in **Settings > Apps > Installed apps** for uninstalling,
and installs the .NET 8 desktop runtime first if the PC doesn't have it. Your entries and settings in
`%AppData%\RemoteMan` are kept across updates.

To try a build before it's released, open a run of the
[build workflow](https://github.com/Langeboi/RemoteMan/actions/workflows/build.yml) and download the
`RemoteMan-Setup-…` artifact (sign in to GitHub first). Test builds are numbered below the next release
(`0.1.0-ci.42`), so an installed test build updates to the real release when it comes out.

## Updates

The installed app checks GitHub Releases 15 seconds after start and every 4 hours after that. When a new
version exists, a banner under the menu bar offers **Update**; it downloads in the background (only the
changes, when a delta is available) and then offers **Restart now**. Choosing **On next launch** applies it the next
time RemoteMan starts. **Help > Check for updates** checks on demand, and **Help > Check automatically**
turns the background checks off. A copy run from the build folder isn't installed and doesn't self-update.

To publish a release, push a version tag:

```
git tag v0.2.0
git push origin v0.2.0
```

The `release` workflow builds the installer and update packages with [Velopack](https://velopack.io),
writes release notes from the commits since the previous tag, and publishes the GitHub release. A tag with
a suffix such as `v0.2.0-beta.1` becomes a prerelease, which installed copies skip.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the design.
