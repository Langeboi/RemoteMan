# Architecture

## Stack, and why

| Choice | Why |
| --- | --- |
| **C# / .NET 10, WPF** | Native Windows UI, starts fast with ReadyToRun, and is the only stack that can host the Windows RDP ActiveX control and WebView2 directly. No Electron, so memory stays low. |
| **mstscax (MsRdpClient9)** | The same RDP engine as mstsc: every RDP feature, NLA/CredSSP, gateway, dynamic resolution. Bound late via IDispatch, so no generated interop DLLs. |
| **SSH.NET 2026** | Pure managed SSH with the widest algorithm range in .NET: post-quantum mlkem768x25519 down to DH group1-sha1, ed25519 to ssh-rsa, AES-GCM/ChaCha20 to 3DES-CBC. |
| **xterm.js in WebView2** | The terminal used by VS Code: fast GPU rendering, full VT support, copy on select. |
| **WebView2** | Embedded Chromium for web entries; one shared browser process for all tabs. |
| **SQLite / PostgreSQL / SQL Server / MySQL** | Local by default; shared databases are optional. |

## Projects

```
src/RemoteMan.Core       model, EntryStore (in-memory mirror + sync), EndpointResolver, AES-GCM vault
src/RemoteMan.Data       SQL data sources (one shared implementation, four dialects), failover wrapper
src/RemoteMan.Protocols  SSH session, RDP control, external browsers, web auto-fill, VNC launcher
src/RemoteMan.App        WPF shell, theme, views, dialogs
src/RemoteMan.Gateway    gateway tunnel protocol, end-to-end crypto, client forwarders, connector, relay pairing
src/RemoteMan.Gateway.Server  relay host (Kestrel) and connector as one CLI/Docker image (deploy/gateway)
tests/RemoteMan.Tests    sync, conflict, presence, fallback, crypto tests (run on any OS)
```

## Data and multi-user sync

One table holds every entry. Protocol settings are a JSON payload, so adding a field never needs a
schema migration. Indexed columns: `id`, `parent_id`, `kind`, `name`, `revision`, `deleted`.

- **Revisions.** Every write increments a one-row counter (`remoteman_meta`) inside the same transaction.
  The row lock is held until commit, so revisions become visible strictly in order and a client asking
  "what changed after revision N?" can never miss a change that committed late.
- **Change detection.** PostgreSQL pushes: writes call `pg_notify` and every client's `LISTEN`
  connection gets it the moment the transaction commits (milliseconds). SQL Server and MySQL poll the
  counter row every 500 ms, a primary-key point read that costs the server next to nothing. SQLite
  polls every second.
- **Pull.** On a signal, the client runs `SELECT … WHERE revision > @last ORDER BY revision` and
  patches its in-memory tree. Nothing reloads in full.
- **Deletes** are tombstones so they sync like any other change.
- **Conflicts.** Saves carry the revision the editor started from; if someone saved in between, the
  save is refused and the user is told, rather than silently overwriting.
- **Presence.** Each client upserts one row (user, machine, entry, viewing/editing/connected) every
  10 s; rows older than 30 s are ignored. PostgreSQL also notifies on presence changes.
- **Redundancy.** A data source can list several servers. Calls go to the first; on a connection
  failure the next becomes active and the tree resyncs. Replication between them is the database's
  job (Postgres streaming replication, SQL Server Always On, MySQL group replication).

- **Identity and history.** On a shared server the user is the database login (`session_user`,
  `SUSER_SNAME()`, `USER()`), so nobody can claim another name. Each row stores `created_at/by` and
  `updated_at/by`, written by the server-side login.
- **Offline mode.** Connection failures never surface as errors. `EntryStore` goes offline (red Q),
  keeps serving entries from memory or from a DPAPI-encrypted cache (`%APPDATA%\RemoteMan\cache`),
  refuses edits with a clear message and probes every 15 s. The first successful probe pulls every
  revision missed meanwhile (a full reload when it started from the cache) and resumes presence.

**Recommendation for teams: PostgreSQL**, because it is the only one of the four that pushes changes
instead of being polled.

## Secrets

- Local SQLite: passwords and keys encrypted with Windows DPAPI (bound to your Windows account).
- Shared databases: AES-256-GCM with a key derived (PBKDF2-SHA256, 210k rounds) from a team vault
  passphrase. Each user enters it once; it is cached locally under DPAPI. The database server never
  sees plaintext.

## RDP performance with many sessions

- 32-bit colour plus `NetworkConnectionType = 7` (auto-detect) and `BandwidthDetection` make the client
  offer the RDP 8+ graphics pipeline (GFX). If the server allows H.264/AVC (group policy "Prioritize
  H.264/AVC 444 graphics mode", and "Configure H.264/AVC hardware encoding" for GPU encoding on the
  host), frames arrive video-coded and Windows decodes them on the GPU.
- Background tabs: the ActiveX window is hidden (`SetForeground(false)`), resizes are deferred until the
  tab is shown, and copy-on-select's low-level mouse hook is removed, so only the visible session costs
  anything locally.
- Airspace: the main window is opaque, with no Effects or Opacity above session hosts, so WPF and the
  hosted RDP/WebView2 windows stay hardware-rendered.

## Single sign-on (optional)

- Database: "Use my Windows login" rewrites the connection string: SQL Server `Integrated Security`,
  PostgreSQL GSSAPI/SSPI (no password; user defaults to the Windows user), MySQL
  `authentication_windows_client` / MariaDB GSSAPI.
- RDP: the entry's credential "(my Windows login)" sends no password and lets CredSSP pass your
  logged-on credentials. Windows only allows that when the "Allow delegating default credentials"
  policy lists the server (`TERMSRV/*`); RemoteMan shows a hint when it doesn't.
- Web: a separate WebView2 profile with `--auth-server-allowlist` set to the entry's hosts answers
  Kerberos/NTLM challenges, and Entra ID sites sign in with the Windows account. Form auto-fill is off.
- SSH: not supported (SSH.NET has no GSSAPI); use keys.

## Connecting

1. `EndpointResolver` TCP-probes the primary host (2 s timeout), then each fallback in order.
2. The linked credential is decrypted just for this session.
3. A tab opens: RDP (ActiveX), SSH (SSH.NET + xterm.js), web (WebView2), or an external VNC viewer
   or browser. Hidden tabs stay alive; switching tabs never reconnects.

Certificate and host key handling: RDP uses `AuthenticationLevel = 0` (connect without the warning),
WebView2 answers `ServerCertificateErrorDetected` with *AlwaysAllow*, Chrome/Edge get
`--ignore-certificate-errors` on an isolated profile, and SSH accepts unknown host keys while
recording them in `%APPDATA%\RemoteMan\known_hosts.json` (a changed key is shown as a warning). Each of
these can be turned off per entry.

## SSH jump hosts

An SSH entry can name another SSH entry as its jump host (`SshSettings.JumpHostId`), which can have its
own, and so on (loops are refused). `SshSession` connects the first hop directly (probed like any entry),
then for each next hop opens a `ForwardedPortLocal` on a free loopback port of the previous hop
(direct-tcpip, like OpenSSH ProxyJump) and connects through it, trying that hop's hosts in order. Host keys
are checked against the real host name, not the loopback forward. SFTP reuses the final connection info,
so it rides the same tunnel. The target isn't TCP-probed from this PC, because behind a jump host it
usually can't be reached directly.

## Snippets

Snippets are entries (`EntryKind.Snippet`, `SnippetData`) in the same table as everything else, so they
sync, export and back up like connections, but they are kept out of the tree. The SSH tab's Snippets
panel lists them by group and sends one to any set of open SSH tabs; newlines become CR so multi-line
snippets type line by line. A snippet's group is `Category/Group` (`SnippetData.Category`/`SubGroup`);
the panel shows one category at a time with collapsible groups, so a big library stays tidy.
`BuiltInSnippets` is the starter library (Debian, FortiOS 7.6, MikroTik 7.25), added once per data
source. Its commands were written from vendor documentation, not run against each device.

## Import, export and backup

Importers (`Core/Import`) turn RDM, .rdp and PuTTY data into `ImportItem`s (an entry plus plain-text
secrets). `ImportPlanner` turns items into entries to save: new ids with links (credential, jump host)
remapped, parents first, same-named folders reused, duplicates skipped, secrets encrypted with the
target data source's protector. Restore in merge mode keeps ids instead.

`.rmx` (`ExportFile`): a JSON envelope `{format, version, kdf, iterations, salt, nonce, data}` around a
gzip-compressed bundle encrypted with AES-256-GCM, key from PBKDF2-SHA256 (600,000 rounds). Secrets inside
are plain text (the file is the encryption boundary), so a file opens into any data source. Automatic
backups write `RemoteMan <source> <date>.rmx` daily to `%APPDATA%\RemoteMan\backups` (or a chosen
folder), keep the newest N, and remember their password under DPAPI.
## AI apps (MCP)

RemoteMan doesn't talk to any AI provider itself. It is a Model Context Protocol server that AI apps start.

- **Transport**: the AI app runs `RemoteMan.exe --mcp` as a stdio server. That process (`McpBridge`, before
  Velopack or WPF start) passes newline-delimited JSON-RPC through to the running RemoteMan over a named pipe,
  `RemoteMan.Mcp.<domain>.<user>`, opened with `CurrentUserOnly`, so only the same Windows user can connect.
  The app only listens while "Let AI apps use RemoteMan" is on (`McpSettings.Enabled`, off by default).
- **RemoteMan closed**: the bridge answers initialize and tools/list itself (the tool list is fixed in
  `McpCatalog`) and every tool call returns an error saying to start RemoteMan. It connects as soon as the
  pipe is there; requests in flight when RemoteMan closes get an error instead of hanging.
- **Protocol** (`Core/Mcp/McpServer`): initialize (2024-11-05 to 2025-11-25), ping, tools/list, tools/call,
  empty resources and prompts lists. Tool failures come back as `isError` results the model can read.
- **Tools** (`MainViewModel.Mcp`, run on the UI thread like a click): list_entries, get_entry, list_sessions,
  read_terminal, type_in_terminal, open_connection, team_presence, zabbix_problems, config_backups,
  get_device_config. Output is built field by field, never by serializing an `Entry`: no passwords, keys,
  stored usernames or secret custom fields, and credential entries (and credential folders) aren't listed.
- **Permissions** (AI box): terminals off / read / type (no Enter, bracketed paste) / run; opening entries;
  Oxidized config text (off by default, configs hold hashes). Each tool call is listed in the box's activity.
- **Terminal output**: SSH tabs keep a plain-text tail (`TerminalTail`, escape codes stripped), returned
  inside `<terminal_output>` and marked as untrusted data in the tool description and server instructions.
- **Client setup** (`McpClients`): writes `mcpServers.remoteman` into Claude Desktop's config (normal and
  Microsoft Store installs, a `.bak` copy kept) and copies the `claude mcp add` command, VS Code `servers`
  JSON or the generic `mcpServers` JSON. The installed exe path (`…\current\RemoteMan.exe`) stays the same
  across updates.

## Shared sessions

Right-click an RDP or terminal entry > **Shared session…** (or a tab's menu > Share this session…).

- **Invites** are rows in `remoteman_shares` (shared databases only): who, which entry, the host's
  addresses, the relay, a one-time ticket and the SHA-256 of the host's throwaway TLS certificate.
  PostgreSQL notifies (`s`), the others poll. Invitees get a pop-up and a chat card; invites expire after 10 minutes.
- **The link** (`Core/Sharing`): the guest connects to the host directly (all addresses raced) or through
  the relay, then TLS pinned to that certificate. The host is the hub and keeps the control rules
  (`ShareControl`): one controller at a time, the host starts with it, host or controller can hand it
  on, the host can always take it back, read-only logins only watch.
- **Terminals**: the host fans out the shell output (plus the recent screen for late joiners) and only
  takes keystrokes from the controller; guests' xterm.js runs at the host's size. The host's own typing is
  held while someone else has control.
- **RDP**: Windows Desktop Sharing (rdpencomapi, the Remote Assistance engine). Only RemoteMan's windows and
  only the RDP tab's rectangle are shared; it pauses while the tab is hidden. Guests' viewers reach it
  through a tunnel inside the share link, so direct and relay work the same. The controller's viewer gets
  interactive control; since it is the same Windows desktop, the host's own mouse still works too.
- **Relay** (`deploy/relay/remoteman-relay.py`, installed with `remoteman-db relay install`): both sides
  connect out to it on TCP 47120 and it splices bytes; it can't read the TLS inside. RemoteMan looks for it
  on the database servers by itself (`ShareRelay` in settings overrides).

## Gateway (reach other networks over 443)

Twingate-style: a relay with a public address (Caddy + Let's Encrypt in front), a connector inside each remote
network, both connecting out only. Setup in [deploy/gateway](../deploy/gateway/README.md).

- **Pairing** (`Gateway/Relay.cs`): the connector holds a control WebSocket (`/v1/connector`, site token, its
  static ECDSA P-256 public key in a header). RemoteMan opens `/v1/connect?site=x` (device token); the relay
  asks the least busy connector for a tunnel (`open <id>` on the control socket), the connector opens
  `/v1/accept/<id>`, and the relay copies messages between the two sockets. Errors (bad token, site not
  allowed, offline, no answer) are HTTP statuses before the upgrade, so RemoteMan can say what's wrong.
- **End to end** (`Gateway/SecureChannel.cs`): ephemeral ECDH P-256, the connector signs the transcript hash
  with its static key, HKDF-SHA256 gives one AES-256-GCM key per direction, a 64-bit counter is the nonce.
  RemoteMan accepts only connector keys the relay recorded for that site (`/v1/sites`).
- **Inside a tunnel**: `{"targets":["10.0.0.5:3389","10.0.0.6:3389"]}` → the connector tries them in order
  (only addresses its `ALLOW` list covers) and answers `{"ok":true,"target":…}`; then raw TCP bytes.
- **In the app** (`Services/GatewayService.cs`): a site comes from the entry or the nearest folder that sets one
  (`EntryStore.GatewayFor`). After the direct probe fails (or straight away with *Always*), RemoteMan probes
  through the site, then points the session at a `LocalForwarder` on 127.0.0.1. Data sources route each
  server through its own forwarder (`DataSourceFactory.RouteHosts`) that tries direct first and remembers the
  choice for a minute. SSH host keys are saved under the real host.
- **Admin console** (`Gateway.Server/Admin`): served by the relay at `/admin` as embedded static files (vanilla JS,
  no build step) plus a JSON API under `/admin/api`. Admins are stored in `relay.json` with PBKDF2 hashes and an
  optional TOTP secret. The first admin comes from a one-time setup code that the relay logs, or from
  `gateway admin setup`. Sessions are kept in memory behind an HttpOnly, SameSite=Strict cookie, and every change
  needs the `X-RemoteMan-Admin` header. Each connector can have its own token (`SiteRecord.ConnectorTokens`).
  Site resources are sent to connectors as `allow <spec>` on the control socket, and a connector opens only what
  both the site's resources and its own `ALLOW` allow. Invite links (`/enroll#code`, `POST /v1/enroll`) create a
  device when they're used; `GatewayEnrollment` lets RemoteMan redeem one directly. Activity is appended to
  `activity.jsonl`.

## Known gaps / next steps

- Build and run on Windows to shake out XAML issues (this cut was compile-checked on Linux only).
- Dark styling for ComboBox and context menus.
- Drag and drop in the tree; import from mRemoteNG.
- RDM import was written from the export format, not tested against real RDM files yet.
- Embedded VNC; RDP gateway credentials UI.
- SQL Server Query Notifications as an optional push channel.
