# Architecture

## Stack, and why

| Choice | Why |
| --- | --- |
| **C# / .NET 8, WPF** | Native Windows UI, starts fast with ReadyToRun, and is the only stack that can host the Windows RDP ActiveX control and WebView2 directly. No Electron, so memory stays low. |
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

**Recommendation for teams: PostgreSQL**, because it is the only one of the four that pushes changes
instead of being polled.

## Secrets

- Local SQLite: passwords and keys encrypted with Windows DPAPI (bound to your Windows account).
- Shared databases: AES-256-GCM with a key derived (PBKDF2-SHA256, 210k rounds) from a team vault
  passphrase. Each user enters it once; it is cached locally under DPAPI. The database server never
  sees plaintext.

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

## AI assistant

- `RemoteMan.Core/Ai`: one `IAiChatClient` per provider family, all streaming. Claude goes through the
  official Anthropic SDK (with server-side refusal fallback on); OpenAI, Mistral, Ollama and custom
  endpoints share a Chat Completions client; Gemini uses `streamGenerateContent`. Connecting lists the
  account's models first, which proves the key before anything is saved.
- Keys are DPAPI-encrypted per Windows user in `%APPDATA%\RemoteMan\ai-<provider>.key`. They are never
  written to a data source, so on a shared database every user brings their own key.
- Session context: SSH tabs keep a plain-text tail of the terminal (`TerminalTail`, escape codes
  stripped). When the context toggle is on, the last 50 lines go in front of the user's message, wrapped
  as untrusted data; the system prompt stays constant so providers can cache it. Nothing is sent until
  the user presses Send.
- Paste to terminal types the command without Enter; multi-line text is sent as a bracketed paste.
- The chat box is a small owned window, not an overlay, because WPF cannot draw over the RDP control or
  WebView2. It is not a layered window, so the account sign-in WebView2 renders inside it. Windows 11
  rounds its corners when the provider's theme is rounded.
- Account sign-in uses a separate WebView2 profile (`%LOCALAPPDATA%\RemoteMan\AiWeb`); signing out
  deletes that provider's cookies.

## Known gaps / next steps

- Build and run on Windows to shake out XAML issues (this cut was compile-checked on Linux only).
- Dark styling for ComboBox and context menus.
- Drag and drop in the tree; import from RDM (.rdm export) and mRemoteNG.
- Embedded VNC; SFTP side panel for SSH entries; RDP gateway credentials UI.
- SQL Server Query Notifications as an optional push channel.
