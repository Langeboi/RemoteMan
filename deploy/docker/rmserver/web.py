"""The admin page and its API (HTTPS, port 4433), plus the internal API the servers use to talk to each other."""
import base64
import collections
import csv
import hmac
import io
import json
import mimetypes
import os
import secrets
import ssl
import threading
import time
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import psycopg2

from . import common as C
from . import oxidized
from . import store
from .common import log

SESSION_IDLE = 4 * 3600
SESSION_MAX = 24 * 3600
PRESENCE = {0: "Viewing", 1: "Editing", 2: "Connected to"}
NO_ENTRY = "00000000-0000-0000-0000-000000000000"


def activity(r):
    """One presence row in words. The app keeps an all-zero entry id row while it's open: just online."""
    if r.get("entry_id") == NO_ENTRY:
        return "Online"
    return f"{PRESENCE.get(r['state'], 'Viewing')} {r['entry'] or 'an entry'}"
KINDS = {0: "folder", 1: "RDP", 2: "SSH", 3: "VNC", 4: "web", 5: "Telnet", 6: "serial", 7: "SFTP", 8: "FTP"}


class Http(Exception):
    def __init__(self, status, message):
        super().__init__(message)
        self.status = status


class Admin:
    def __init__(self, cluster):
        self.cl = cluster
        self.sessions = {}
        self.fails = collections.defaultdict(list)
        self.rings = collections.defaultdict(lambda: collections.deque(maxlen=120))
        self.cache = {"events": [], "logins": [], "at": 0}
        self.hist_cache = {}
        cluster.on_poll = self.on_poll
        local = {
            ("GET", "/internal/state"): lambda b, q: dict(self.cl.sampler.snapshot, time=time.time()),
            ("GET", "/internal/history"): lambda b, q: self.cl.sampler.history(q.get("range", "1h")),
            ("GET", "/internal/logins"): lambda b, q: store.logins(float(q.get("since", 0))),
            ("GET", "/internal/events"): lambda b, q: store.events(int(q.get("limit", 50))),
            ("GET", "/internal/logs"): lambda b, q: pg_log_tail(int(q.get("lines", 300))),
            ("POST", "/internal/promote"): lambda b, q: {"result": self.cl.switchover() if b.get("mode") == "switchover" else self.cl.promote()},
            ("POST", "/internal/demote"): lambda b, q: self.cl.demote(b["to"]),
            ("POST", "/internal/rejoin"): lambda b, q: {"result": self.cl.rejoin()},
            ("POST", "/internal/server-row"): lambda b, q: self.cl.update_server_row(b),
        }
        # These only work on the primary; a standby passes them on.
        on_primary = {
            ("POST", "/internal/add-server"): lambda b, q: self.cl.add_server(b.get("name", ""), b.get("address", "")),
            ("POST", "/internal/remove-server"): lambda b, q: self.cl.remove_server(b.get("name", "")) or {"ok": True},
            ("POST", "/internal/settings"): lambda b, q: self.cl.save_settings(b) or {"ok": True},
            ("POST", "/internal/backup"): lambda b, q: {"file": self.cl.backups.run()},
            ("POST", "/internal/oxidized-token"): lambda b, q: {"token": oxidized.new_token()},
        }
        for key, fn in on_primary.items():
            local[key] = self._primary_only(key, fn)
        self.internal = local
        threading.Thread(target=self._cache_loop, daemon=True, name="feed-cache").start()

    def _primary_only(self, key, fn):
        def run(b, q):
            if self.cl.role() == "primary":
                return fn(b, q)
            return self.on(self.primary_name(), key[0], key[1], b, timeout=600)
        return run

    # ------------------------------------------------------------ data gathering
    def on_poll(self):
        """Every second: one point per server for the 2 minute charts."""
        snaps = {self.cl.name: (self.cl.sampler.snapshot, 0.05 if self.cl.pg.running() else None)}
        for p in self.cl.peers.values():
            snaps[p["name"]] = (p.get("snap") if p.get("ok") else None, p.get("lat"))
        t = round(time.time(), 1)
        primary = next((s for s, _ in snaps.values() if s and s.get("role") == "primary"), None)
        lag = {r["name"]: r for r in (primary or {}).get("repl", [])}
        for name, (snap, lat) in snaps.items():
            m = (snap or {}).get("m", {})
            pt = {"t": t, "lat": None if lat is None else round(lat, 2)}
            for k in ("cpu", "rx", "tx", "iops", "conns", "tps", "reads", "writes", "cache", "locks", "wal"):
                v = m.get(k)
                pt[k] = None if v is None else round(v, 2)
            if m.get("mem_total"):
                pt["mem"] = round(100 * m["mem_used"] / m["mem_total"], 1)
            if name in lag:
                pt["lag"], pt["lagb"] = round(lag[name]["lag"], 3), lag[name]["lagb"]
            self.rings[name].append(pt)

    def on(self, name, method, path, body=None, query=None, timeout=10):
        """Runs an internal call on the named server (here, or over the network)."""
        if name == self.cl.name:
            fn = self.internal[(method, path)]
            return fn(body or {}, query or {})
        srv = next((s for s in self.cl.servers if s["name"] == name), None)
        if not srv:
            raise Http(404, f"There's no server called {name}.")
        qs = ("?" + urllib.parse.urlencode(query)) if query else ""
        return self.cl.call(srv, method, path + qs, body, timeout=timeout)

    def primary_name(self):
        if self.cl.role() == "primary":
            return self.cl.name
        p = self.cl.primary_peer()
        if not p:
            raise Http(409, "No server is the primary right now. Wait for the failover, or make a standby the primary.")
        return p["name"]

    def _cache_loop(self):
        while True:
            try:
                self.refresh_cache()
            except Exception as e:  # noqa: BLE001
                log.debug("feed cache: %s", e)
            time.sleep(5)

    def gather(self, path, query):
        """Calls every reachable server and tags rows with the server's name."""
        out = []
        targets = [self.cl.name] + [p["name"] for p in self.cl.peers.values() if p.get("ok")]
        for n in targets:
            try:
                for r in self.on(n, "GET", path, query=query, timeout=4):
                    r["server"] = n
                    out.append(r)
            except Exception:  # noqa: BLE001
                continue
        return out

    def refresh_cache(self):
        since = time.time() - 26 * 3600
        self.cache = {"events": self.gather("/internal/events", {"limit": 40}),
                      "logins": self.gather("/internal/logins", {"since": since}), "at": time.time()}

    def local_rows(self, sql, args=None):
        try:
            return C.q(sql, args, db=C.DB_NAME, dicts=True) or []
        except Exception:  # noqa: BLE001
            return []

    def entries_feed(self):
        rows = self.local_rows("""SELECT name, kind, updated_by, updated_at, created_at, deleted FROM remoteman_entries
            WHERE updated_by IS NOT NULL ORDER BY updated_at DESC LIMIT 20""")
        out = []
        for r in rows:
            verb = "deleted" if r["deleted"] else ("added" if r.get("created_at") and abs((r["created_at"] or 0) - r["updated_at"]) < 1500 else "changed")
            kind = KINDS.get(r["kind"], "entry")
            out.append({"ts": r["updated_at"] / 1000, "kind": "entry", "who": r["updated_by"],
                        "text": f"{verb} {kind} entry", "subject": r["name"]})
        return out

    def presence(self):
        rows = self.local_rows("""SELECT p.user_name, p.machine, p.entry_id, p.state, p.last_seen, e.name AS entry, e.kind
            FROM remoteman_presence p LEFT JOIN remoteman_entries e ON e.id = p.entry_id
            WHERE p.last_seen > (extract(epoch FROM now()) * 1000 - 120000) ORDER BY p.last_seen DESC""")
        out = collections.defaultdict(list)
        for r in rows:
            # The app writes one extra row per open connection as "MACHINE#<entry id>"; show the machine name.
            m = r["machine"] or ""
            if len(m) > 33 and m[-33] == "#":
                r["machine"] = m[:-33]
            out[r["user_name"].lower()].append(r)
        return out

    def access_map(self):
        rows = self.local_rows("SELECT login, access FROM (SELECT r.rolname AS login, remoteman_admin.access_of(r.rolname) AS access FROM pg_roles r) x WHERE access IS NOT NULL")
        return {r["login"]: r["access"] for r in rows}

    def shares(self):
        rows = self.local_rows("""SELECT host_user, invitee, state, payload, updated_at FROM remoteman_shares
            WHERE expires_at > extract(epoch FROM now()) * 1000 AND state IN (0, 1) ORDER BY updated_at DESC""")
        out = {}
        for r in rows:
            try:
                p = json.loads(r["payload"] or "{}")
            except ValueError:
                p = {}
            key = (r["host_user"], p.get("ShareId") or p.get("shareId") or r["host_user"])
            item = out.setdefault(key, {"host": r["host_user"], "entry": p.get("EntryName") or p.get("entryName") or "",
                                        "kind": p.get("Kind") if isinstance(p.get("Kind"), str) else "", "guests": [], "pending": []})
            (item["guests"] if r["state"] == 1 else item["pending"]).append(r["invitee"])
        return list(out.values())

    def live(self, session):
        cl = self.cl
        me_snap = cl.sampler.snapshot
        servers = []
        snaps = {cl.name: me_snap}
        for p in cl.peers.values():
            if p.get("ok"):
                snaps[p["name"]] = p["snap"]
        primary = next((s for s in snaps.values() if s.get("role") == "primary"), None)
        repl = {r["name"]: r for r in (primary or {}).get("repl", [])}
        for s in cl.servers:
            if s["state"] == "removed":
                continue
            snap = snaps.get(s["name"])
            peer = cl.peers.get(s["name"], {})
            row = dict(s)
            row["here"] = s["name"] == cl.name
            row["reachable"] = bool(snap)
            row["lat"] = 0.05 if row["here"] else peer.get("lat")
            row["offset_ms"] = 0 if row["here"] else (peer.get("offset") or 0) * 1000
            row["seen"] = time.time() if snap else peer.get("seen")
            if not snap and peer.get("last_snap"):
                row["last"] = {k: peer["last_snap"].get(k) for k in ("role", "timeline", "pg_version", "version")}
            if snap:
                for k in ("role", "status", "timeline", "pg_version", "version", "started_at", "pg_started", "m", "receiver",
                          "upstream", "replay_age", "relay", "backup", "cert_days", "vacuum_age", "dbsize", "lsn", "pg_error"):
                    row[k] = snap.get(k)
            if s["name"] in repl:
                row["repl"] = repl[s["name"]]
            servers.append(row)
        # Who is connected (on the primary), with what RemoteMan says they're doing.
        pres = self.presence()
        acc = self.access_map()
        people = {}
        for c in (primary or {}).get("clients", []):
            u = people.setdefault(c["user"], {"user": c["user"], "addrs": [], "since": c["since"], "conns": 0,
                                              "access": acc.get(c["user"], "")})
            u["addrs"].append(c["addr"] or "local")
            u["since"] = min(u["since"], c["since"])
            u["conns"] += c["count"]
        for u in people.values():
            acts = pres.get(u["user"].lower(), [])
            order = sorted(acts, key=lambda r: (-r["state"], -r["last_seen"]))
            u["activity"] = [activity(r) for r in order[:2]]
            u["machines"] = sorted({r["machine"] for r in acts})
        # Activity feed.
        feed = []
        for e in self.cache["events"]:
            feed.append({"ts": e["ts"], "kind": e["kind"], "who": "", "text": e["text"], "server_event": True})
        for lg in self.cache["logins"][:60]:
            if lg["result"] == "ok":
                feed.append({"ts": lg["ts"], "kind": "login", "who": lg["user"], "text": f"signed in on {lg['server']}",
                             "detail": lg["host"]})
            else:
                feed.append({"ts": lg["ts"], "kind": "failed", "who": lg["user"] or "unknown",
                             "text": f"couldn't sign in on {lg['server']}: {lg['reason'].lower()}", "detail": lg["host"]})
        feed += self.entries_feed()
        feed.sort(key=lambda f: -f["ts"])
        # Sign-ins per hour, last 24 h.
        now = time.time()
        hours = [{"ok": 0, "failed": 0} for _ in range(24)]
        for lg in self.cache["logins"]:
            age = now - lg["ts"]
            if 0 <= age < 86400:
                hours[23 - int(age // 3600)]["ok" if lg["result"] == "ok" else "failed"] += 1
        midnight = time.mktime(time.localtime()[:3] + (0, 0, 0, 0, 0, -1))
        today = [lg for lg in self.cache["logins"] if lg["ts"] >= midnight]
        return {
            "me": {"login": session["user"], "server": cl.name},
            "now": now,
            "primary": primary["name"] if primary else None,
            "settings": dict(cl.settings),
            "servers": servers,
            "series": {n: list(r) for n, r in self.rings.items()},
            "people": sorted(people.values(), key=lambda u: u["since"]),
            "feed": feed[:30],
            "signins": {"hours": hours, "today_ok": sum(1 for l in today if l["result"] == "ok"),
                        "today_failed": sum(1 for l in today if l["result"] != "ok"),
                        "day_ok": sum(h["ok"] for h in hours), "day_failed": sum(h["failed"] for h in hours)},
            "tables": (primary or me_snap).get("tables", []),
            "size_hist": (primary or me_snap).get("size_hist", []),
            "dbsize": (primary or me_snap).get("dbsize"),
            "shares": self.shares(),
            "health": self.health(servers, primary),
        }

    def health(self, servers, primary):
        out = []

        def add(level, title, detail):
            out.append({"level": level, "title": title, "detail": detail})
        live = [s for s in servers if s["state"] == "joined"]
        standbys = [s for s in live if s.get("role") == "standby"]
        primaries = [s for s in live if s.get("role") == "primary"]
        if len(primaries) > 1:
            add("bad", "Two primaries", ", ".join(s["name"] for s in primaries) + " are both primary")
        elif not primaries:
            add("bad", "No primary", "no server is accepting changes")
        else:
            add("ok", "Primary", f"{primaries[0]['name']} accepts changes")
        streaming = [s for s in standbys if (s.get("repl") or {}).get("state") == "streaming"]
        if not standbys:
            add("warn", "Replication", "no standby: add a second server for redundancy")
        elif len(streaming) == len(standbys):
            tl = {s.get("timeline") for s in live}
            add("ok", "Replication streaming", f"{len(streaming)} standby{'s' if len(streaming) > 1 else ''}, timeline {max(t or 0 for t in tl)}")
        else:
            add("bad", "Replication", ", ".join(s["name"] for s in standbys if s not in streaming) + " not streaming")
        down = [s for s in live if not s.get("reachable")]
        if down:
            add("bad", "Servers reachable", ", ".join(s["name"] for s in down) + " can't be reached")
        af = self.cl.setting("auto_failover") == "on"
        wit = self.cl.setting("witness")
        if not af:
            add("warn", "Automatic failover", "off: make a standby the primary by hand")
        elif not standbys:
            add("warn", "Failover ready", "nothing to fail over to")
        else:
            w = wit if wit not in ("auto", "") else ("default gateway" if wit == "auto" else "none")
            add("ok" if self.cl.witness_ok() else "warn", "Failover ready",
                f"after {self.cl.setting('failover_seconds')} s, witness: {w}")
        vers = {s.get("pg_version") for s in live if s.get("pg_version")}
        add("ok" if len(vers) <= 1 else "warn", "Versions match",
            f"PostgreSQL {', '.join(sorted(v.split()[0] for v in vers)) or '?'}" + ("" if len(vers) <= 1 else " (mixed)"))
        drift = max((abs(s.get("offset_ms") or 0) for s in live if s.get("reachable")), default=0)
        add("ok" if drift < 2000 else "warn", "Clocks in sync", f"{drift:.0f} ms apart at most")
        worst = None
        for s in live:
            m = s.get("m") or {}
            if m.get("disk_total"):
                pct = 100 * m["disk_used"] / m["disk_total"]
                if not worst or pct > worst[1]:
                    worst = (s["name"], pct, (m["disk_total"] - m["disk_used"]) / 1e9)
        if worst:
            add("ok" if worst[1] < 85 else ("warn" if worst[1] < 95 else "bad"), "Disk space",
                f"{worst[0]} is {worst[1]:.0f}% full ({worst[2]:.1f} GB free)")
        b = (primary or {}).get("backup") or {}
        if b.get("ts"):
            age = (time.time() - b["ts"]) / 3600
            add("ok" if age < 26 and not b.get("error") else "warn", "Nightly backup",
                f"{time.strftime('%d %b %H:%M', time.localtime(b['ts']))}, {b['size'] / 1048576:.1f} MB" + (f", {b['dur']:.0f} s" if b.get("dur") else ""))
        else:
            add("warn", "Nightly backup", "none yet" + (f" (runs at {b.get('at')})" if b.get("at") else ""))
        relays = [s for s in live if (s.get("relay") or {}).get("up")]
        add("ok" if relays else "warn", "Relay", f"running on {', '.join(s['name'] for s in relays)}" if relays else "not running on any server")
        vac = (primary or {}).get("vacuum_age")
        if vac is not None:
            add("ok" if vac < 7 * 86400 else "warn", "Vacuum", f"last run {fmt_age(vac)} ago")
        longest = ((primary or {}).get("m") or {}).get("longest_xact", 0)
        add("ok" if longest < 300 else "warn", "No long transactions", f"longest {longest:.1f} s")
        shared = self.local_rows("SELECT rolcanlogin FROM pg_roles WHERE rolname = %s", (C.APP_USER,))
        if shared:
            add("ok" if not shared[0]["rolcanlogin"] else "warn", "Shared login",
                "off: everyone uses their own" if not shared[0]["rolcanlogin"] else f"'{C.APP_USER}' still works: give people their own logins")
        m = (primary or {}).get("m") or {}
        if m.get("max_conns"):
            add("ok" if m["conns"] < 0.8 * m["max_conns"] else "warn", "Connection slots", f"{m['conns']} of {m['max_conns']} used")
        days = [s.get("cert_days") for s in live if s.get("cert_days") is not None]
        if days:
            d = min(days)
            add("ok" if d > 30 else "warn", "Certificate", f"admin page certificate expires in {d:.0f} days")
        return out

    def users(self, session):
        with self.primary_conn(session) as c:
            cur = c.cursor()
            cur.execute("SELECT login, access, can_login, connections FROM remoteman_admin.list()")
            rows = [{"login": r[0], "access": r[1], "enabled": r[2], "connections": r[3]} for r in cur.fetchall()]
        since = time.time() - 90 * 86400
        last_login, last_from = {}, {}
        for lg in self.gather("/internal/logins", {"since": since}):
            if lg["result"] == "ok" and lg["ts"] > last_login.get(lg["user"], 0):
                last_login[lg["user"]], last_from[lg["user"]] = lg["ts"], lg["host"]
        changes = {r["updated_by"]: r for r in self.local_rows(
            """SELECT DISTINCT ON (updated_by) updated_by, name, updated_at, deleted FROM remoteman_entries
               WHERE updated_by IS NOT NULL ORDER BY updated_by, updated_at DESC""")}
        pres = self.presence()
        for r in rows:
            if r["login"] == session["user"]:
                r["connections"] = max(0, r["connections"] - 1)   # this page's own connection
            r["last_login"] = last_login.get(r["login"])
            r["last_from"] = last_from.get(r["login"])
            ch = changes.get(r["login"])
            if ch:
                r["last_change"] = {"ts": ch["updated_at"] / 1000, "entry": ch["name"], "deleted": bool(ch["deleted"])}
            acts = pres.get(r["login"].lower(), [])
            if acts:
                a = sorted(acts, key=lambda x: -x["state"])[0]
                r["activity"] = activity(a)
        return rows

    def primary_conn(self, session):
        hosts = [s for s in self.cl.servers if s["state"] == "joined" and s["address"]]
        try:
            c = psycopg2.connect(host=",".join(s["address"] for s in hosts), port=",".join(str(s["pg_port"]) for s in hosts),
                                 user=session["user"], password=session["password"], dbname=C.DB_NAME,
                                 target_session_attrs="read-write", connect_timeout=4, application_name="remoteman-admin")
        except psycopg2.OperationalError as e:
            raise Http(502, "Couldn't reach the primary: " + str(e).strip().splitlines()[-1])
        c.autocommit = True
        return _Closing(c)

    def conn_string(self, login, password):
        hosts = ",".join(f"{s['address']}:{s['pg_port']}" for s in self.cl.servers if s["state"] == "joined" and s["address"])
        pw = password
        if any(ch in pw for ch in ";'\" "):
            pw = '"' + pw.replace('"', '""') + '"'
        return f"Host={hosts};Database={C.DB_NAME};Username={login};Password={pw};Target Session Attributes=primary"

    def user_op(self, session, op, b):
        name = (b.get("name") or "").strip().lower()
        with self.primary_conn(session) as c:
            cur = c.cursor()
            try:
                if op == "add":
                    pw = b.get("password") or C.rand(20)
                    cur.execute("SELECT remoteman_admin.add(%s, %s, %s)", (name, pw, b.get("access", "edit")))
                    store.add_event("user", f"{session['user']} added the login {name} ({b.get('access', 'edit')})")
                    return {"name": name, "password": pw, "conn": self.conn_string(name, pw)}
                if op == "access":
                    cur.execute("SELECT remoteman_admin.set_access(%s, %s)", (name, b.get("access")))
                    store.add_event("user", f"{session['user']} changed {name}'s access to {b.get('access')}")
                    return {"ok": True}
                if op == "password":
                    pw = b.get("password") or C.rand(20)
                    cur.execute("SELECT remoteman_admin.set_password(%s, %s)", (name, pw))
                    store.add_event("user", f"{session['user']} set a new password for {name}")
                    if name == session["user"]:
                        session["password"] = pw
                    return {"name": name, "password": pw, "conn": self.conn_string(name, pw)}
                if op == "enable":
                    cur.execute("SELECT remoteman_admin.set_enabled(%s, %s)", (name, bool(b.get("enabled"))))
                    store.add_event("user", f"{session['user']} turned {name}'s login {'on' if b.get('enabled') else 'off'}")
                    return {"ok": True}
                if op == "remove":
                    cur.execute("SELECT remoteman_admin.remove(%s)", (name,))
                    gone = cur.fetchone()[0]
                    store.add_event("user", f"{session['user']} removed the login {name}")
                    return {"removed": gone}
            except psycopg2.Error as e:
                raise Http(400, (e.diag.message_primary or str(e)).strip())
        raise Http(404, "Unknown user action.")

    def login_history(self, q):
        days = min(int(q.get("days", 7) or 7), 90)
        rows = self.gather("/internal/logins", {"since": time.time() - days * 86400})
        text = (q.get("q") or "").lower().strip()
        res = q.get("result", "all")
        srv = q.get("server", "")
        out = []
        for r in rows:
            if res == "ok" and r["result"] != "ok":
                continue
            if res == "failed" and r["result"] == "ok":
                continue
            if srv and r["server"] != srv:
                continue
            if text and text not in (r["user"] or "").lower() and text not in (r["host"] or "").lower():
                continue
            out.append(r)
        out.sort(key=lambda r: -r["ts"])
        return out

    # ------------------------------------------------------------ sessions
    def sign_in(self, user, password, ip):
        fails = [t for t in self.fails[ip] if time.time() - t < 600]
        self.fails[ip] = fails
        if len(fails) >= 10:
            raise Http(429, "Too many failed sign-ins from this address. Wait 10 minutes.")
        user = (user or "").strip()
        try:
            c = psycopg2.connect(host="127.0.0.1", port=C.PG_PORT, user=user, password=password or "", dbname=C.DB_NAME,
                                 connect_timeout=4, application_name="RemoteMan admin page")
            try:
                cur = c.cursor()
                cur.execute("SELECT login, access, is_admin FROM remoteman_admin.me()")
                login, access, is_admin = cur.fetchone()
            finally:
                c.close()
        except psycopg2.OperationalError as e:
            self.fails[ip].append(time.time())
            time.sleep(1)
            msg = str(e)
            if "password authentication failed" in msg or "does not exist" in msg or "not permitted" in msg:
                raise Http(401, "Wrong login or password.")
            raise Http(503, "The database on this server isn't answering. " + msg.strip().splitlines()[-1])
        except psycopg2.Error as e:
            raise Http(403, (e.diag.message_primary or str(e)).strip())
        if not is_admin:
            raise Http(403, "Only RemoteMan admins can open the admin page. Ask an admin to give you admin access.")
        token = secrets.token_urlsafe(32)
        self.sessions[token] = {"user": login, "password": password, "access": access, "created": time.time(), "seen": time.time()}
        return token

    def oxidized_info(self):
        tok = oxidized.token()
        addr = self.cl.address
        try:
            count = len(oxidized.nodes())
        except Exception:  # noqa: BLE001
            count = None
        def last(key):
            try:
                return json.loads(store.get(key) or "null")
            except ValueError:
                return None
        return {"ready": bool(tok), "url": f"https://{addr}:{C.ADMIN_PORT}/oxidized/nodes.json", "token": tok or "",
                "devices": count, "config": oxidized.config_snippet(addr, tok or "<token>"),
                "last_read": last("oxidized_last_read"), "last_refused": last("oxidized_last_refused"), "server": self.cl.name}

    def session(self, token):
        s = self.sessions.get(token or "")
        if not s:
            return None
        now = time.time()
        if now - s["seen"] > SESSION_IDLE or now - s["created"] > SESSION_MAX:
            self.sessions.pop(token, None)
            return None
        s["seen"] = now
        return s


class _Closing:
    def __init__(self, c):
        self.c = c

    def __enter__(self):
        return self.c

    def __exit__(self, *a):
        self.c.close()


def fmt_age(s):
    if s < 120:
        return f"{s:.0f} s"
    if s < 7200:
        return f"{s / 60:.0f} min"
    if s < 172800:
        return f"{s / 3600:.0f} h"
    return f"{s / 86400:.0f} days"


def pg_log_tail(lines):
    import glob
    files = sorted(glob.glob(os.path.join(C.LOG_DIR, "*.json")), key=os.path.getmtime)
    out = []
    for f in reversed(files[-2:]):
        try:
            with open(f, "rb") as fh:
                fh.seek(0, 2)
                size = fh.tell()
                fh.seek(max(0, size - 400_000))
                chunk = fh.read().decode("utf-8", "replace").splitlines()
        except OSError:
            continue
        for line in reversed(chunk):
            try:
                e = json.loads(line)
            except ValueError:
                continue
            out.append({"ts": e.get("timestamp"), "level": e.get("error_severity"), "user": e.get("user"),
                        "host": e.get("remote_host"), "message": e.get("message"), "detail": e.get("detail")})
            if len(out) >= lines:
                return out
    for ev in store.events(50):
        out.append({"ts": time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(ev["ts"])), "level": "EVENT", "message": ev["text"]})
    return out[:lines]


# ---------------------------------------------------------------- HTTP
def make_handler(admin):
    cl = admin.cl

    class H(BaseHTTPRequestHandler):
        server_version = "RemoteMan"
        sys_version = ""

        def log_message(self, fmt, *args):  # quiet
            pass

        def _send(self, status, body, ctype="application/json", headers=None):
            data = body if isinstance(body, bytes) else json.dumps(body, default=str).encode()
            self.send_response(status)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(data)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Content-Type-Options", "nosniff")
            self.send_header("X-Frame-Options", "DENY")
            self.send_header("Referrer-Policy", "no-referrer")
            self.send_header("Content-Security-Policy", "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self'; frame-ancestors 'none'")
            self.send_header("Strict-Transport-Security", "max-age=31536000")
            for k, v in (headers or {}).items():
                self.send_header(k, v)
            self.end_headers()
            self.wfile.write(data)

        def _body(self):
            n = int(self.headers.get("Content-Length") or 0)
            if n > 1_000_000:
                raise Http(413, "Too large.")
            raw = self.rfile.read(n) if n else b""
            try:
                return json.loads(raw) if raw else {}
            except ValueError:
                raise Http(400, "Bad request.")

        def _cookie(self):
            for part in (self.headers.get("Cookie") or "").split(";"):
                k, _, v = part.strip().partition("=")
                if k == "rm_session":
                    return v
            return None

        def do_GET(self):
            self._route("GET")

        def do_POST(self):
            self._route("POST")

        def _route(self, method):
            u = urllib.parse.urlsplit(self.path)
            q = dict(urllib.parse.parse_qsl(u.query))
            path = u.path
            try:
                if path.startswith("/internal/"):
                    sec = self.headers.get("X-RemoteMan-Secret", "")
                    if not cl.secret or not hmac.compare_digest(sec, cl.secret):
                        raise Http(403, "Forbidden.")
                    fn = admin.internal.get((method, path))
                    if not fn:
                        raise Http(404, "Not found.")
                    return self._send(200, fn(self._body() if method == "POST" else {}, q))
                # Oxidized asks for one device as <url>/<name> when it reloads a single node.
                if method == "GET" and (path == "/oxidized/nodes.json" or path.startswith("/oxidized/nodes.json/")):
                    return self._oxidized(urllib.parse.unquote(path[len("/oxidized/nodes.json/"):]) if path.startswith("/oxidized/nodes.json/") else None)
                if path.startswith("/api/"):
                    return self._api(method, path, q)
                return self._static(path)
            except Http as e:
                self._send(e.status, {"error": str(e)})
            except RuntimeError as e:
                self._send(409, {"error": str(e)})
            except Exception as e:  # noqa: BLE001
                log.exception("request %s %s", method, path)
                self._send(500, {"error": f"Something went wrong: {e}"})

        def _oxidized(self, only=None):
            """Oxidized's http source: Authorization: Bearer <token> (or basic auth with the token as the password)."""
            ip = self.client_address[0]
            now = time.time()
            recent = [t for t in admin.fails[("oxidized", ip)] if now - t < 300]
            admin.fails[("oxidized", ip)] = recent
            if len(recent) >= 10:
                raise Http(429, "Too many wrong tokens. Wait a few minutes.")
            auth = self.headers.get("Authorization") or ""
            given = ""
            if auth.lower().startswith("bearer "):
                given = auth[7:].strip()
            elif auth.lower().startswith("basic "):
                try:
                    given = base64.b64decode(auth[6:].strip()).decode().partition(":")[2]
                except ValueError:
                    given = ""
            if not oxidized.check_token(given):
                recent.append(now)
                store.put("oxidized_last_refused", json.dumps({"ts": now, "ip": ip, "why": "no token" if not given else "wrong token"}))
                raise Http(401, "Wrong or missing token. Find it on the admin page under Servers > Oxidized.")
            nodes = oxidized.nodes()
            store.put("oxidized_last_read", json.dumps({"ts": now, "ip": ip, "count": len(nodes), "one": only or ""}))
            if only:
                nodes = [n for n in nodes if n["name"].lower() == only.lower()]
            return self._send(200, nodes)

        def _static(self, path):
            if path in ("/", "/overview", "/servers", "/users", "/logins"):
                path = "/index.html"
            name = os.path.normpath(path.lstrip("/"))
            full = os.path.join(C.UI_DIR, name)
            if name.startswith("..") or not os.path.isfile(full):
                raise Http(404, "Not found.")
            with open(full, "rb") as f:
                data = f.read()
            ctype = mimetypes.guess_type(full)[0] or "application/octet-stream"
            if ctype.startswith("text/") or ctype.endswith("javascript"):
                ctype += "; charset=utf-8"
            self._send(200, data, ctype)

        def _api(self, method, path, q):
            if method == "POST" and self.headers.get("X-Requested-With") != "remoteman":
                raise Http(403, "Forbidden.")
            if path == "/api/login" and method == "POST":
                b = self._body()
                token = admin.sign_in(b.get("user"), b.get("password"), self.client_address[0])
                return self._send(200, {"ok": True}, headers={
                    "Set-Cookie": f"rm_session={token}; Path=/; HttpOnly; Secure; SameSite=Strict; Max-Age={SESSION_MAX}"})
            if path == "/api/hello":
                return self._send(200, {"server": cl.name, "status": cl.status(), "role": cl.role(), "version": C.VERSION})
            s = admin.session(self._cookie())
            if not s:
                raise Http(401, "Sign in first.")
            b = self._body() if method == "POST" else {}
            if path == "/api/logout":
                admin.sessions.pop(self._cookie(), None)
                return self._send(200, {"ok": True}, headers={"Set-Cookie": "rm_session=; Path=/; Max-Age=0; Secure; HttpOnly"})
            if path == "/api/live":
                return self._send(200, admin.live(s))
            if path == "/api/history":
                rng = q.get("range", "1h")
                key = (rng, int(time.time() // 30))
                if key not in admin.hist_cache:
                    data = {}
                    for n in [cl.name] + [p["name"] for p in cl.peers.values() if p.get("ok")]:
                        try:
                            data[n] = admin.on(n, "GET", "/internal/history", query={"range": rng}, timeout=5)
                        except Exception:  # noqa: BLE001
                            pass
                    admin.hist_cache = {key: data}
                return self._send(200, admin.hist_cache[key])
            if path == "/api/users":
                return self._send(200, admin.users(s))
            if path.startswith("/api/users/") and method == "POST":
                return self._send(200, admin.user_op(s, path.rsplit("/", 1)[1], b))
            if path == "/api/logins":
                rows = admin.login_history(q)
                if q.get("format") == "csv":
                    buf = io.StringIO()
                    w = csv.writer(buf)
                    w.writerow(["time (UTC)", "user", "result", "reason", "server", "from", "app", "seconds connected"])
                    for r in rows:
                        w.writerow([time.strftime("%Y-%m-%d %H:%M:%S", time.gmtime(r["ts"])), r["user"], r["result"],
                                    r["reason"] or "", r["server"], r["host"], r["app"], "" if r["duration"] is None else round(r["duration"])])
                    return self._send(200, buf.getvalue().encode(), "text/csv; charset=utf-8",
                                      {"Content-Disposition": "attachment; filename=remoteman-logins.csv"})
                return self._send(200, rows[:2000])
            if path == "/api/oxidized" and method == "GET":
                return self._send(200, admin.oxidized_info())
            if path == "/api/logs":
                return self._send(200, admin.on(q.get("server", cl.name), "GET", "/internal/logs", query={"lines": 300}))
            if method == "POST":
                user = s["user"]
                if path == "/api/servers/add":
                    res = admin.on(cl.name, "POST", "/internal/add-server", b)
                    return self._send(200, res)
                if path == "/api/servers/promote":
                    mode = "switchover" if b.get("mode") == "switchover" else "force"
                    store.add_event("server", f"{user} asked {b.get('name')} to take over ({'switch-over' if mode == 'switchover' else 'forced failover'})")
                    return self._send(200, admin.on(b.get("name"), "POST", "/internal/promote", {"mode": mode}, timeout=200))
                if path == "/api/servers/rejoin":
                    return self._send(200, admin.on(b.get("name"), "POST", "/internal/rejoin", {}, timeout=600))
                if path == "/api/servers/remove":
                    return self._send(200, admin.on(cl.name, "POST", "/internal/remove-server", b))
                if path == "/api/settings":
                    return self._send(200, admin.on(cl.name, "POST", "/internal/settings", b))
                if path == "/api/oxidized/token":
                    admin.on(cl.name, "POST", "/internal/oxidized-token", {})
                    store.add_event("server", f"{user} made a new Oxidized token")
                    return self._send(200, admin.oxidized_info())
                if path == "/api/backup":
                    return self._send(200, admin.on(cl.name, "POST", "/internal/backup", {}, timeout=600))
            raise Http(404, "Not found.")

    return H


def serve(cluster):
    admin = Admin(cluster)
    httpd = ThreadingHTTPServer(("0.0.0.0", C.ADMIN_PORT), make_handler(admin))
    httpd.daemon_threads = True
    ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    ctx.minimum_version = ssl.TLSVersion.TLSv1_2
    ctx.load_cert_chain(cluster.cert_file, cluster.key_file)
    httpd.socket = ctx.wrap_socket(httpd.socket, server_side=True, do_handshake_on_connect=False)
    log.info("Admin page on https://%s:%s", cluster.address, C.ADMIN_PORT)
    threading.Thread(target=httpd.serve_forever, daemon=True, name="https").start()
    return admin
