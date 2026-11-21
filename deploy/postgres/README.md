# Redundant database for RemoteMan (two Debian/Ubuntu servers)

Two PostgreSQL servers: the **first** is the primary, the **second** keeps a live copy (streaming
replication) and takes over if the first one dies. RemoteMan is given both hosts in one connection
string and always uses whichever is the primary, so a failover needs nothing from the users.

Why PostgreSQL: it's the only one of RemoteMan's databases that pushes changes to every client
instantly (LISTEN/NOTIFY), its replication is built in and free, and it's light enough for two small VMs.

## Set up

Copy `remoteman-db.sh` to both servers.

**Server 1**

```bash
sudo bash remoteman-db.sh first --peer 10.0.0.12        # IP of server 2
```

It installs PostgreSQL 17, creates the `remoteman` database and login, and prints two things: the
command to run on server 2 (with a one-time join key), and the connection string for RemoteMan.

**Server 2**: run the command server 1 printed, for example

```bash
sudo bash remoteman-db.sh second --primary 10.0.0.11 --join <key> --auto-failover
```

**RemoteMan**: the Data sources button at the bottom of the left icon rail, then Add, type PostgreSQL, and paste the connection string:

```
Host=10.0.0.11:5432,10.0.0.12:5432;Database=remoteman;Username=remoteman;Password=…;Target Session Attributes=primary
```

(Two lines, one per server, work too: RemoteMan joins them into the string above.)

## Day to day

Both servers get a `remoteman-db` command:

| Command | What it does |
| --- | --- |
| `remoteman-db status` | Which server is primary, and how far behind the standby is |
| `sudo remoteman-db promote` | Make the standby the primary (manual failover) |
| `sudo remoteman-db rejoin` | Turn this server into the standby of the other one |
| `remoteman-db conn` | The connection string for RemoteMan (without the password) |

## What happens when a server goes down

- **Standby down**: nothing changes for users. When it comes back it catches up by itself.
- **Primary down**, with `--auto-failover`: after 30 seconds the standby promotes itself, but only if it
  can still reach the default gateway, so a standby that lost its own network doesn't take over.
  RemoteMan shows offline for a few seconds and then carries on against the new primary.
  Without `--auto-failover`, run `sudo remoteman-db promote` on the standby.
- **Old primary comes back**: before PostgreSQL starts it sees that the other server took over, rewinds
  itself and starts as the new standby. A watcher also checks every 5 seconds, so two primaries never
  stay side by side.

## Notes

- Tested on Debian and Ubuntu with systemd. PostgreSQL comes from apt.postgresql.org so both servers get
  the same version; set `PG_VERSION=16` (on both) to use another one.
- The firewall must let the two servers reach each other, and RemoteMan reach both, on TCP 5432.
  An active `ufw` is opened automatically.
- Settings live in `/etc/postgresql/<version>/main/conf.d/remoteman.conf` and `/etc/remoteman-db.conf`
  (root only; it holds the replication password).
- Two servers can't tell "the other one is dead" from "the link between us is down" with certainty.
  The gateway check covers the common case. For a third opinion, pass `--witness <IP>` of another
  always-on machine on server 2.
