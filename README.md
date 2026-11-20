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

- Tree of folders, RDP, SSH, web, VNC and credential entries. Search box doubles as quick connect
  (`ssh host`, `rdp://host:3390`, `https://…`).
- Several hosts per entry: primary first, then fallbacks, each probed with a 2 s TCP check.
- **Ctrl+click** a credential while a connection is selected to link it.
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
  in Edge, Chrome or Firefox.
- Copy on select in SSH, RDP and web tabs (no Ctrl+C needed); right click pastes in SSH.
- RDP file transfer over the RDP clipboard: copy files on one side, paste on the other, or drop files on
  the tab's side strip and press Ctrl+V in the remote folder.
- SSH **Files** panel (Ctrl+Shift+F): SFTP over the same login, browse, upload/download files and whole
  folders, drop files on the terminal or panel to upload.
- VNC through an installed viewer (TigerVNC, TightVNC, RealVNC, UltraVNC).
- Data sources: local SQLite (default), or shared PostgreSQL, SQL Server or MySQL/MariaDB with
  multi-server failover, live updates and presence (who is viewing, editing or connected).
- Users are the database login (local files: your Windows account). Every entry records when it was
  created and last modified, and by whom (shown in the properties pane).
- Optional single sign-on: a shared database can use your Windows login (SQL Server integrated security,
  PostgreSQL/MySQL Kerberos), and RDP and web entries can log in as "(my Windows login)".
- Offline mode: if the database drops, the Q turns red, entries keep working from memory (or from an
  encrypted local cache at startup), edits wait, and RemoteMan retries every 15 s. When the database is
  back the Q turns green and everything missed shows up live.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the design.
