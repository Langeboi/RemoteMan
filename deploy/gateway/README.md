# RemoteMan gateway

Reach the database and the machines in other networks (school lab, a friend's homelab, a site behind CG-NAT) from
anywhere, without a VPN client and without opening ports on those networks. It works like Twingate: every piece
connects **out** on 443, and one small relay in the middle pairs them up.

```
RemoteMan (laptop, anywhere) ──443──►  relay (your home, remoteman.mydomain.dk)  ◄──443── connector (school, CG-NAT)
                                                                                           └─► 10.20.0.0/16: RDP, SSH, web, PostgreSQL…
```

- **Relay**: the only public piece. Runs at home (or any box with a public IP) behind Caddy, which gets a
  Let's Encrypt certificate by itself. It knows the sites and devices and splices tunnels. It never sees the
  traffic: RemoteMan and the connector encrypt end to end (ECDH P-256 + AES-256-GCM, connector key pinned).
- **Connector**: one per remote network, in Docker. Outbound only, reconnects by itself, and only opens what its
  `ALLOW` list covers (all private ranges by default). Two connectors on one site share the load and take over
  from each other.
- **RemoteMan**: paste an invite once per PC (More > Gateway settings). Then a folder, an entry or the data source names a
  site. RemoteMan tries the hosts directly first (you're on that network, or on a VPN) and goes through the gateway
  only when they don't answer, unless you tick *Always*.

The relay doesn't have to be where the database or the devices are: they just need a connector next to them.

## Quick start (one command, then the web console)

On the Linux box that will run the relay:

```bash
curl -fsSL https://raw.githubusercontent.com/langeboi/remoteman/test/all-features/deploy/gateway/install-relay.sh | sudo sh
```

It asks for the relay's public name and whether it gets its own certificate (Caddy) or sits behind your proxy,
installs Docker if needed, starts the relay and prints a one-time link. Open it, create your admin, and do the rest
in the **admin console** at `https://<your relay>/rmrelay/`:

1. **Add site** (one per network). It opens straight into **Add connector**: a one-line install
   (`curl … | sudo sh`, installs Docker on that machine if missing), a `docker run` line, or a `compose.yaml`, each
   with a fresh token for that connector. Paste it inside the network and the dialog turns green when it's online.
2. **Invite someone**: makes a link (`https://<relay>/enroll#…`). In RemoteMan, **More > Gateway settings**, paste
   the link, Save. The PC joins by itself as its own device. (Opened in a browser, the link gives an `rmgw1.` code
   for older RemoteMan builds.)

Prefer to do it by hand? The sections below are the same thing step by step, and every console action has a CLI
command too.

## Admin console

`https://<your relay>/rmrelay/`, served by the relay itself (same port, nothing extra to run or proxy).

- **Overview**: sites and connectors online, tunnels open now (who, where, how long, how much), traffic, recent
  activity, and a *Get started* list until the first site, connector and device exist.
- **Sites**: per site its connectors live (online since, from which address, version, its own `ALLOW`, open
  tunnels, key) and offline ones with when they were last seen; one token per connector, each revocable on its
  own (revoking disconnects it); **Resources**, the addresses and ports the site's connectors may open, pushed to
  them at once (they open only what both the resources and their own `ALLOW` allow); which devices have access;
  forget keys; remove.
- **People & devices**: every device with its owner, sites, status (tunnels open, active, paused), last seen and
  last address. Edit access, pause (refused until turned back on) or remove (refused at once, open tunnels close).
  Invite links with an owner, sites, lifetime and how many PCs they may add; open ones can be revoked.
- **Activity**: connectors coming and going, closed tunnels with duration and traffic, device and invite changes,
  sign-ins (also failed ones) and every change made in the console, with who made it. Kept in `activity.jsonl`.
- **Settings**: change your password, turn on an authenticator code (QR code, any TOTP app), see and sign out
  your other sessions, add or remove admins.

**Security.** The console is internet-facing like the relay, so: the first admin can only be made with the
one-time setup link from the relay's log (or `gateway admin setup`); passwords are PBKDF2-SHA256 (210,000
rounds); sessions are random tokens in an HttpOnly, SameSite=Strict cookie (Secure behind HTTPS), 12 hours idle at
most; every change needs the console's own request header, so another site can't post to it; five wrong sign-ins
lock that name and that address for 30 s, doubling up to 15 min, and are logged; pages carry a strict CSP. Narrow
who can reach it with `ADMIN_ALLOW` (e.g. `ADMIN_ALLOW: 192.168.0.0/16,203.0.113.7`; behind your proxy, the last
`X-Forwarded-For` address counts) or turn it off with `ADMIN=off`. RemoteMan, connectors and invite links work
either way.

**Locked out?** `docker exec -it remoteman-relay gateway admin reset <name>` sets a new password and turns the
code off. `gateway admin list | add <name> | remove <name>` manage admins from the shell.

**Already running a relay?** `docker compose pull && docker compose up -d`, then `docker logs remoteman-relay`
shows the setup link (or run `docker exec remoteman-relay gateway admin setup`). Existing sites, devices and
connectors carry on unchanged; the site's original token shows as *Site token* in the console. Update the
connectors too (same command where they run) so they report their version and follow the site's resources.

## Hosted workspaces (optional)

With `HOSTED_DB` set, the console also makes **workspaces**: shared RemoteMan databases on a PostgreSQL server of
their own, which show up in your friends' RemoteMan under Data sources once they sign in with the account you made
them. No invite links or connection strings. See [deploy/hosted](../hosted/README.md).

## 1. Relay (once)

1. DNS: an A record, e.g. `remoteman.mydomain.dk`, pointing at your public IP.
2. Router: forward TCP **443** (and 80, optional) to the machine that runs the relay.
3. Save [`relay.compose.yaml`](relay.compose.yaml) there, change `remoteman.mydomain.dk` in both places, and run:

```bash
docker compose -f relay.compose.yaml up -d
docker logs remoteman-relay-caddy     # "certificate obtained successfully"
curl https://remoteman.mydomain.dk    # "RemoteMan gateway relay…"
```

Already have a reverse proxy or want another port? See [Behind your own proxy](#behind-your-own-proxy-or-on-another-port).

No public IP of your own (your home is behind CG-NAT too)? Run the relay anywhere that has one (a small VPS), or
publish it through a free Cloudflare Tunnel: drop the `caddy` service and point the tunnel at `http://relay:8080`.

## Behind your own proxy, or on another port

The relay speaks plain HTTP and WebSocket on `PORT` (default 8080), so anything that can forward WebSockets can sit in
front of it and hold the certificate. Use [`relay-behind-proxy.compose.yaml`](relay-behind-proxy.compose.yaml): no
Caddy, the relay published on 47200 (change `PORT` and the `ports:` line together for another).

`PUBLIC_URL` is what RemoteMan and the connectors dial, so set it to the proxy's public address, port and path
included when they aren't the defaults: `https://remoteman.mydomain.dk`, `https://mydomain.dk:8443`,
`https://mydomain.dk/remoteman`. It goes into the invites and the connector compose files `gateway site add` prints.

What the proxy needs: pass WebSocket upgrades, and don't time out idle connections quickly (both ends ping every
20 s, so anything above a minute works; tunnels can last hours).

**Nginx Proxy Manager**: new proxy host, domain `remoteman.mydomain.dk`, scheme `http`, forward to the VM's IP and
port `47200`, tick **Websockets Support**, and on the SSL tab request a Let's Encrypt certificate with Force SSL.

**Nginx**:

```nginx
server {
    listen 443 ssl;                      # or any port: then PUBLIC_URL=https://remoteman.mydomain.dk:<port>
    server_name remoteman.mydomain.dk;
    ssl_certificate     /etc/letsencrypt/live/remoteman.mydomain.dk/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/remoteman.mydomain.dk/privkey.pem;

    location / {
        proxy_pass http://10.0.0.20:47200;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_read_timeout 1d;
        proxy_send_timeout 1d;
        proxy_buffering off;
    }
}
```

Under a path instead (`location /remoteman/ { proxy_pass http://10.0.0.20:47200/; … }`), set
`PUBLIC_URL=https://mydomain.dk/remoteman`.

**Traefik** (labels on the relay service, with your existing `websecure` entrypoint and certificate resolver):

```yaml
    labels:
      - traefik.enable=true
      - traefik.http.routers.remoteman.rule=Host(`remoteman.mydomain.dk`)
      - traefik.http.routers.remoteman.entrypoints=websecure
      - traefik.http.routers.remoteman.tls.certresolver=letsencrypt
      - traefik.http.services.remoteman.loadbalancer.server.port=47200
```

**Caddy you already run**: `remoteman.mydomain.dk { reverse_proxy 10.0.0.20:47200 }`.

**Only NAT, no proxy**: the relay can do TLS itself. Mount a certificate and key and set `TLS_CERT=/certs/fullchain.pem`
and `TLS_KEY=/certs/privkey.pem`; it then serves HTTPS on `PORT`. Forward any outside port to it on the router and put
that port in `PUBLIC_URL` (`https://remoteman.mydomain.dk:47200`). Plain `http://` works too for testing, and the
traffic inside is still end-to-end encrypted, but the tokens would cross the internet readable, so use TLS for real.

## 2. A site per remote network

On the relay (`-it` so it can wait and ask):

```bash
docker exec -it remoteman-relay gateway site add school
```

It prints one `docker run` line with the relay address and the site's token filled in, then waits:

```text
docker run -d --name remoteman-connector --restart unless-stopped --network host -e RELAY_URL=https://remoteman.mydomain.dk -e SITE_TOKEN=… -e NAME=school-1 -v remoteman-connector:/data ghcr.io/langeboi/remoteman-gateway:test connector

Waiting for school's connector to come online (Ctrl+C to stop waiting; the connector can come later) |
```

Paste the line on any Docker host in that network (a VM, a Pi, the NAS). Nothing needs opening there. Within seconds
the relay answers:

```text
✓ school is online: connector school-1 from 203.0.113.7, key 64a4f6bc37b249bc

Next, RemoteMan. Name the PC to invite (e.g. laptop), or press Enter to skip: lange-laptop
```

Name your PC and it prints the invite for step 3 right there. Want a `compose.yaml` instead of the one-liner?
`gateway site add school --compose`. Set up the connector later? `--no-wait`, and `gateway site wait school` watches
for it whenever you like.

Limit what it may open with `ALLOW` (comma-separated): `10.20.0.0/16`, `192.168.1.10`, `10.20.5.0/24:22|3389`
(only those ports), `private` (the default: 10/8, 172.16/12, 192.168/16, 100.64/10, link-local, IPv6 ULA) or `*`.

## 3. A device per PC

```bash
docker exec remoteman-relay gateway device add lange-laptop               # every site
docker exec remoteman-relay gateway device add anna-pc --sites school     # only these
```

It prints an invite (`rmgw1.…`). In RemoteMan: **More > Gateway settings**, paste it, Save. The same window shows the sites
the PC may use and whether their connectors are online.

## 4. Use it

With one site there's nothing to set: the database and every entry try directly first and go through the site when
that doesn't answer. Once RemoteMan notices it's off-site, it skips the direct try (and its timeout) until a direct
connection works again. With several sites, or to pin things down:

- **Entries and folders**: Edit > Advanced > Connection > **Gateway**: pick the site. On a folder it covers everything
  inside (an entry or sub-folder can say *None (direct only)* or another site). *Always* skips the direct try.
- **The database**: Data sources > your source > **Gateway site**. Entries in that data source then use the same site
  too, unless a folder or entry picks another one or *None (direct only)*. Each server gets its own tunnel, so PostgreSQL
  failover keeps working; with *Always* off, a PC on the same network as the database still connects directly.
  Takes effect on the next start.

What goes through the tunnel is a plain TCP connection, so RDP (certificate warnings still skipped), SSH (host keys
are saved under the real host, not 127.0.0.1), SFTP, VNC, web entries and PostgreSQL / SQL Server / MySQL all work.
Web entries open as `https://127.0.0.1:<port>`, so a site that needs its own host name (a reverse proxy with several
sites on one address) shows its default site; give those entries their own IP or port.

## Day to day

| On the relay (`docker exec remoteman-relay …`) | |
| --- | --- |
| `gateway status` | Connectors online per site, tunnels open, traffic |
| `gateway site list` | Sites and their connector key fingerprints |
| `gateway site wait <site>` | Wait until the site's connector is online |
| `gateway site token <site>` | New token and setup line for a site (the old token stops working; re-run the connector) |
| `gateway site reset-key <site>` | Forget the site's connector keys (after replacing a connector on purpose) |
| `gateway site remove <site>` | |
| `gateway device list` / `gateway device remove <name>` | A removed device is refused at once |
| `gateway invite [--sites a,b] [--owner name] [--days 7] [--uses 1]` | Invite link to paste into RemoteMan |
| `gateway admin setup` / `add` / `reset` / `remove` / `list` | Admin console accounts |

Upgrade: `docker compose pull && docker compose up -d` on the relay and on each connector.

## Security notes

- Tokens are 256 random bits, stored on the relay only as SHA-256 hashes. A device token is kept in RemoteMan
  under Windows DPAPI.
- Each tunnel does its own key exchange; the connector signs it with its long-term key, and RemoteMan only accepts
  keys the relay recorded for that site. A new connector key is logged on the relay and listed by `site list`.
  The relay is the root of trust: someone who controls the relay could register their own connector, so keep that
  box patched and its volume private.
- The connector never opens anything outside `ALLOW`, and resolves names on its own network (so `nas.lan` works).

## Build it yourself

```bash
docker build -f deploy/gateway/Dockerfile -t remoteman-gateway .     # from the repository root
```

GitHub Actions publishes it to `ghcr.io/langeboi/remoteman-gateway` (`:test` from the test branch, `:latest` from
releases). A new package on GitHub starts private: make it public once under the repository's Packages, Package
settings, or `docker login ghcr.io` on the relay and connector hosts.
