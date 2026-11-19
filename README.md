# Tether

A fast, lean remote connection manager for Windows: RDP, SSH, web and VNC in one window,
with a shared team database as an option. Built for labs, homelabs and schoolwork.

## Build

Requires Windows 10/11 and the .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`).

```powershell
dotnet build Tether.sln -c Release
dotnet run --project src/Tether.App
```

The first build downloads xterm.js (MIT) into `src/Tether.App/Assets/terminal/vendor`.
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
- VNC through an installed viewer (TigerVNC, TightVNC, RealVNC, UltraVNC).
- Data sources: local SQLite (default), or shared PostgreSQL, SQL Server or MySQL/MariaDB with
  multi-server failover, live updates and presence (who is viewing, editing or connected).

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the design.
