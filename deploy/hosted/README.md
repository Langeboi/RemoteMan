# Hosted workspaces

Optional. The relay can also hand out **workspaces**: shared RemoteMan databases that you make on the website and
that show up by themselves in your friends' RemoteMan, under Data sources, once they sign in. No invite links, no
connection strings, no database logins to pass around. Self-hosted databases keep working exactly as before, next
to them.

```
 RemoteMan (each PC) ──── PostgreSQL over TLS ────▶  database server (db.compose.yaml)
        │                                                     ▲
        └── sign-in, workspace list (HTTPS) ──▶ relay ────────┘ accounts, workspaces, backups
```

- **The relay** keeps the accounts and workspaces (`hosted.json`), makes and drops the databases and logins, and
  takes the backups. It is not in the data path: RemoteMan talks to the database server directly, so the gateway
  carries no more traffic than before.
- **The database server** is a plain PostgreSQL 17 on its own small box. Each workspace is one database
  (`rmw_<id>`), each account one login with its own random password that only the relay and the account's PCs know.
- **Access** is per workspace: *Admin* (also changes the workspace's shared settings), *Can edit*, *Read only*
  (sees and connects, can't change entries). The database enforces it, not the app.
- **Saved passwords** are encrypted in RemoteMan with the workspace's vault passphrase before they reach the
  server. The server, the relay and the backups never see them in the clear. The first admin to open a workspace
  chooses the passphrase and tells the others.

## Set it up

1. **Database server.** On the box that will hold the data: edit `DB_NAME`, `RELAY_IP` and `POSTGRES_PASSWORD` in
   [`db.compose.yaml`](db.compose.yaml), then `docker compose -f db.compose.yaml up -d`. The first start makes a
   TLS certificate for `DB_NAME`, requires TLS for every network sign-in, allows the superuser only from the relay,
   and slows down password guessing (`auth_delay`).
2. **Certificate to the relay.** `docker exec remoteman-hosted-db cat /var/lib/postgresql/data/server.crt > hosted-ca.pem`,
   and put the file in the relay's data volume (for example `docker cp hosted-ca.pem remoteman-relay:/data/`).
3. **Relay.** Add to the relay's `environment:` in `relay.compose.yaml`, then `docker compose up -d`:

   ```yaml
   HOSTED_DB: Host=db.remoteman.mydomain.dk;Username=postgres;Password=<POSTGRES_PASSWORD>;SSL Mode=VerifyFull;Root Certificate=/data/hosted-ca.pem
   HOSTED_DB_CA: /data/hosted-ca.pem          # given to the apps, so they check the server too
   HOSTED_DB_PUBLIC: db.remoteman.mydomain.dk:5432   # optional: what the apps connect to, if not the HOSTED_DB host
   BACKUP_AT: "03:00"                         # optional: nightly backup time (the container's TZ)
   ```

   The console at `https://<relay>/rmrelay/` now has **Workspaces**, **Accounts** and **Backups**.
4. **Accounts.** *Accounts › New account*: a username, a first password, and the workspaces it's in. Tell the person
   the server address, username and password yourself. There is no sign-up and no email.
5. **In RemoteMan.** *Data sources › Sign in…* (or *More › Sign in to RemoteMan…*): server, username, password. At
   the first sign-in they choose their own password. Their workspaces appear in the list and in the status bar's
   data source picker, under *RemoteMan account*. The first time one opens, RemoteMan asks for its vault passphrase.

Only console admins make workspaces and accounts. Console admins have an account too, with their console password.
Members can sign in to the website to see their workspaces, sign out a lost PC, change their password and turn on
an authenticator code.

## What happens when…

| | |
| --- | --- |
| You add someone to a workspace | It shows up in their RemoteMan within a minute (or at their next start). |
| You take someone out | It disappears from their RemoteMan within a minute, and their PC's copy is deleted. What they changed keeps their name. |
| You change someone's access | The database enforces it at their next connection. |
| You rename a workspace | Their data source is renamed. The vault passphrase and offline copy stay. |
| You give someone a new first password | Their PCs are signed out and their 2FA is turned off; they choose a new password at the next sign-in. |
| *Sign out everywhere* | Their PCs are signed out and their database password changes. |
| The database server is down | RemoteMan works from each PC's copy; edits wait and are saved when it's back. |
| The relay is down | Open workspaces keep working (RemoteMan talks to the database directly). New sign-ins and list changes wait. |

## Backups

- Every workspace, every night at `BACKUP_AT`, to the relay's volume under `backups/<workspace>/`, as gzipped SQL.
  Kept: the last 7 nights, Sundays for 4 weeks, the 1st of the month for 3 months. Manual and before-delete backups
  are kept 30 days.
- Every Sunday the newest backup of each workspace is restored into a scratch database and its row counts compared.
  The result shows on the Backups page.
- *Restore as copy* makes a new workspace from a backup, with the same members, next to the original. Nothing is
  overwritten.
- *Download* gives the SQL file. `gunzip -c file.sql.gz | psql -d remoteman` loads it into a new, empty database on
  a self-hosted server: a workspace can always move out.
- There is **no point-in-time restore**. The most you can lose is what changed since the last nightly backup (or
  the last *Back up now*). Copy the relay's volume somewhere else too if the relay box itself might die.

## Security notes

- Account passwords are PBKDF2-SHA256 on the relay. App tokens are random, stored hashed, and expire after 90 days
  unused. On the PC they are kept under Windows DPAPI, as is the database password.
- The database password of an account is random and is never shown to anyone. A stolen PC: sign it out on the
  website (or *Sign out everywhere*), which also changes the database password.
- Each login may hold 20 connections at most, can only connect to the workspaces it's a member of, and can't
  create databases or roles. `postgres` and `template1` are closed to them.
- Five wrong sign-ins lock that name and address for a while, as on the console.
