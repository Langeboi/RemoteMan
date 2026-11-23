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
