# RemoteMan server in Docker

The same redundant database as `deploy/postgres/remoteman-db.sh`, packed as one container per server:

- **PostgreSQL 17** with streaming replication: one primary, any number of hot standbys.
- **Automatic failover**: when the primary is gone, the most up-to-date standby takes over. When the old
  primary comes back it rewinds itself and rejoins as a standby. Planned switch-overs lose nothing.
- **Admin page** on `https://<server>:4433`: live dashboard, servers, users, login history.
- **Relay** for shared sessions (optional second service, same image).

RemoteMan's connection string lists every server with `Target Session Attributes=primary`, so the app always
talks to the current primary and a failover needs nothing from the users.

## First server

Save [`compose.yaml`](compose.yaml) on the first server and start it:

```bash
docker compose up -d
docker compose logs remoteman      # shows the admin page address and the remoteman password
```

Open `https://<server>:4433` (the certificate is self-signed, so the browser asks once) and sign in as
`remoteman` with the password from the log, or the `REMOTEMAN_PASSWORD` you set.

## More servers

On the admin page: **Servers > Add server**. Give the new server a name and its address; the page shows a
complete `compose.yaml` with a join key (valid 30 minutes, for that server only). Save it on the new machine
and run `docker compose up -d`. It copies the database and appears as a standby within a minute.

From a shell instead: `docker exec remoteman remoteman-db join-key db2 10.0.0.12`.

## RemoteMan

Data sources, Add, PostgreSQL, and paste the connection string from **Servers** (or
`docker exec remoteman remoteman-db conn`), with a person's own login and password:

```
Host=10.0.0.11:5432,10.0.0.12:5432;Database=remoteman;Username=anna;Password=…;Target Session Attributes=primary
```

Make each person their own login under **Users** (edit, read-only or admin). Admins can open the admin page
and manage logins in RemoteMan's Data sources > Users too. When everyone has one, turn the shared
`remoteman` login off there.

The Zabbix and Oxidized settings live in the database too, so the whole team gets them. Everyone can read them;
only admins can change them (in RemoteMan, More > Zabbix settings / Oxidized settings). The image sets this up
on start, so an existing server only needs the usual upgrade below.

## Oxidized: back up every SSH entry

Oxidized can't be told about new devices; it reads its whole device list from a *source*. The server offers one:
every SSH entry in RemoteMan, at `https://<server>:4433/oxidized/nodes.json`. Point Oxidized at it once and
anything added in RemoteMan is backed up without touching `router.db`.

1. On the admin page: **Servers > Oxidized > Set up Oxidized** (or `docker exec remoteman remoteman-db oxidized`).
   It shows the token and a ready `source:` section.
2. Replace the `source:` section of Oxidized's config (`~/.config/oxidized/config`) with it and restart Oxidized.
3. In RemoteMan, **More > Oxidized device list** has a tick per SSH entry: ticked entries go to Oxidized, and it
   shows which ones Oxidized already has and which are missing. Devices that are only in Oxidized are listed too;
   tick them to keep them there (before switching, or they drop out). With "Add new SSH entries automatically"
   off, only ticked entries are sent and new ones wait until you tick them.

What Oxidized gets per entry: the name (devices it already had keep their old name and group, so the history
carries on), the first host and port, the model and the login. The model comes from a custom field
`Oxidized model`, else a tag naming a model (`procurve`, `dnos`, `edgeos`, `aoscx`…), else the vendor icon
(Aruba `aoscx`, FortiGate `fortios`, MikroTik `routeros`, Cisco `ios`), else the default set in that window.
A secret field `Enable` becomes the enable password. Entries tagged `no-oxidized` are left out.

Logins are encrypted with the team vault passphrase, which the server never has. An admin's RemoteMan therefore
seals each entry's login for the server (RSA-OAEP + AES-GCM, with a key pair only the server can read) and keeps
them current. Entries without a login use the `username`/`password` at the top of Oxidized's config. The token gives
access to those logins: keep it in Oxidized's config only, and make a new one on the admin page if it leaks.
Oxidized picks up new entries when it reloads its list; RemoteMan asks it to reload when entries are added.

## Day to day

| Where | What |
| --- | --- |
| Overview | Live: which server is primary, delay, latency, CPU/memory/disk/network per server, transactions, who's connected and what they're doing, activity feed, sign-ins, size, relay, health checks |
| Servers | Make a standby the primary (switch over, or force when the primary is gone), rejoin, remove, failover settings, backups, logs |
| Users | Add logins, change access, new passwords, turn off, remove |
| Login history | Every sign-in and failed attempt on every server, kept 90 days, CSV export |

Inside the container (`docker exec remoteman remoteman-db …`): `status`, `conn`, `join-key`, `promote [--force]`,
`rejoin`, `backup`, `password <login>`, `logs`.

**Upgrade**: `docker compose pull && docker compose up -d`, one server at a time, standbys first.
The data stays in the `remoteman-data` volume.

**Backups**: every night at `BACKUP_AT` (default 03:00) the primary writes a `pg_dump` to
`/var/lib/remoteman/backups` in its volume and keeps the last `BACKUP_KEEP` (7). Restore with
`pg_restore -d remoteman <file>`.

## Settings (environment)

| Variable | Default | |
| --- | --- | --- |
| `NAME` | host name | This server's name in the cluster (letters, digits, `_`) |
| `JOIN` | | Join key from the admin page; only used the first time a standby starts |
| `REMOTEMAN_PASSWORD` | random | Password of the shared `remoteman` login, set on the first start |
| `ADVERTISE` | detected | Address RemoteMan and the other servers reach this one on |
| `AUTO_FAILOVER` / `FAILOVER_SECONDS` / `WITNESS` | `on` / `30` / `auto` | Starting values; change them later on the Servers page |
| `PG_PORT` / `ADMIN_PORT` / `RELAY_PORT` | 5432 / 4433 / 47120 | |
| `TLS_CERT` / `TLS_KEY` | self-signed | Your own certificate for the admin page (mount the files) |
| `BACKUP_AT` / `BACKUP_KEEP` | `03:00` / 7 | `BACKUP_AT=off` turns the nightly backup off |
| `LOGIN_HISTORY_DAYS` | 90 | |
| `TZ` | UTC | Time zone for the backup time |

Your own PostgreSQL settings go in `/var/lib/remoteman/postgresql.custom.conf` in the volume.

## How failover decides

- A standby takes over when no server has been primary for `FAILOVER_SECONDS`, it can still reach the
  witness (`auto` = the default gateway, so a standby that lost its own network stays put), and, with three
  or more servers, it can see most of them. Of the standbys that qualify, the one that received the most
  changes wins (ties go to the first name).
- A primary that finds another primary on a newer timeline steps down and rejoins as its standby, so two
  primaries never stay side by side.
- Firewalls: the servers must reach each other on 5432 and 4433; RemoteMan reaches them on 5432 (and the
  relay on 47120).

The container uses host networking because the servers and RemoteMan connect to each other's real
addresses. Docker Desktop on Windows/macOS: turn on host networking in its settings, or publish the ports and
set `ADVERTISE` to the machine's address.

## Existing installs from remoteman-db.sh

Keep using the script; it still works, and `sudo bash remoteman-db.sh update` picks up the newer relay.
To move to Docker: back up with `pg_dump -Fc remoteman`, start a fresh Docker cluster, and restore into it
with `pg_restore -d remoteman`. Logins are recreated on the Users page.

## Build it yourself

```bash
docker build -f deploy/docker/Dockerfile -t remoteman-server .   # from the repository root
```

GitHub Actions publishes it to `ghcr.io/langeboi/remoteman-server` (`:test` from the test branch, `:latest`
from releases). A new package on GitHub starts private: make it public once under the repository's
Packages, Package settings, or `docker login ghcr.io` on the servers.
