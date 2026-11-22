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
| `sudo remoteman-db user …` | Personal logins, see below |

## Logins: one per person

Setup creates one shared login, `remoteman`. Give each person their own instead, so RemoteMan shows
their name on what they change and on who's connected, and so you can shut one person out without
changing everyone's password. Run these on the **primary** (logins reach the standby by themselves):

| Command | What it does |
| --- | --- |
| `sudo remoteman-db user add anna` | New login that can edit. Prints a password and Anna's connection string |
| `sudo remoteman-db user add bo --read-only` | New login that can see everything but change nothing |
| `sudo remoteman-db user list` | Everyone, their access, whether they're connected now, and their last change |
| `sudo remoteman-db user passwd anna` | New password (prints a new connection string) |
| `sudo remoteman-db user remove anna` | Removes the login and closes their open connections. Their name stays on past changes |

Add `--password <pw>` to `add` or `passwd` to choose the password instead of getting a random one.

Each person puts their own connection string into RemoteMan's data source. The saved passwords in
RemoteMan are still encrypted with the team vault passphrase, so everyone also needs that. A
read-only person gets a short "can only read" message if they try to save.

When everyone has their own login, turn the shared one off with `sudo remoteman-db user remove remoteman`.
It keeps owning the data but can't log in any more; `user passwd remoteman` turns it back on.

**Already set up before logins existed?** Copy the new `remoteman-db.sh` to both servers and run
`sudo bash remoteman-db.sh update` on each. It updates the `remoteman-db` command and lets the new
logins through.

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

## Shared sessions across networks (relay)

RemoteMan's shared sessions (right-click an RDP or SSH entry > Shared session…) connect teammates
straight to the host when they're on the same LAN or VPN. For teammates on other networks, install
the relay on the database servers:

```bash
sudo remoteman-db relay install     # on both servers
remoteman-db relay status
```

It listens on TCP 47120 and only pipes bytes between the two RemoteMans; the session itself is
encrypted end to end and pinned to the host's certificate, so the relay can't read it. RemoteMan
looks for it on the database servers by itself, so there's nothing to set in the app. Make sure
teammates can reach port 47120 (firewall, port forward). It needs only `python3`.
