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
- RDP embedded (Windows' own mstscax control), resizes with the tab, no certificate prompts.
- SSH in an xterm.js terminal: host keys accepted automatically (trust on first use, changes logged),
  modern and legacy algorithms, password / keyboard-interactive / key auth (OpenSSH, PEM, PuTTY .ppk).
- Web in embedded Chromium (WebView2) with certificate errors skipped and login auto-fill, or opened
  in Edge, Chrome or Firefox.
- Copy on select in SSH, RDP and web tabs (no Ctrl+C needed); right click pastes in SSH.
- RDP file transfer over the RDP clipboard: copy files on one side, paste on the other, or drop files on
  the tab's side strip and press Ctrl+V in the remote folder.
- SSH **Files** panel (Ctrl+Shift+F): SFTP over the same login, browse, upload/download files and whole
  folders, drop files on the terminal or panel to upload.
- VNC through an installed viewer (TigerVNC, TightVNC, RealVNC, UltraVNC).
- Data sources: local SQLite (default), or shared PostgreSQL, SQL Server or MySQL/MariaDB with
  multi-server failover, live updates and presence (who is viewing, editing or connected).

## Updates

Install RemoteMan with `RemoteMan-win-Setup.exe` from the [latest release](https://github.com/Langeboi/RemoteMan/releases/latest).
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
