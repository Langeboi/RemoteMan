"""The RemoteMan database cluster: first start, joining, failover, switchover, rejoin and adding/removing servers.

Every server runs the same container. One is the primary, the others are hot standbys streaming from it.
Which servers exist, and the cluster settings, live in a small table in the database itself, so every
standby has an up-to-date copy by replication. Servers watch each other through the admin API (port 4433).
"""
import concurrent.futures
import glob
import http.client
import json
import os
import re
import shutil
import socket
import ssl
import subprocess
import threading
import time

from . import common as C
from . import store
from .common import log
from .pgctl import Postgres, tool

SCHEMA_SQL = """
CREATE SCHEMA IF NOT EXISTS remoteman_cluster AUTHORIZATION postgres;
REVOKE ALL ON SCHEMA remoteman_cluster FROM PUBLIC;
CREATE TABLE IF NOT EXISTS remoteman_cluster.servers (
    name text PRIMARY KEY, address text NOT NULL DEFAULT '', pg_port int NOT NULL, admin_port int NOT NULL,
    state text NOT NULL, added_at timestamptz NOT NULL DEFAULT now(), joined_at timestamptz,
    key_expires timestamptz, removed_at timestamptz);
CREATE TABLE IF NOT EXISTS remoteman_cluster.settings (key text PRIMARY KEY, value text NOT NULL);
"""

DEFAULT_SETTINGS = {
    "auto_failover": os.environ.get("AUTO_FAILOVER", "on"),
    "failover_seconds": os.environ.get("FAILOVER_SECONDS", "30"),
    "witness": os.environ.get("WITNESS", "auto"),
}


def lsn_bytes(s):
    hi, lo = s.split("/")
    return (int(hi, 16) << 32) + int(lo, 16)


def login_functions_sql():
    """The login functions behind RemoteMan's Users tab, taken from remoteman-db.sh so both setups stay the same."""
    with open(C.SCRIPT) as f:
        text = f.read()
    start = text.index("install_login_functions()")
    at = text.index("<<'SQL'", start)
    body = text[text.index("\n", at) + 1:]
    body = body[:body.index("\nSQL\n")]
    for k, v in {"__APP__": C.APP_USER, "__RO__": C.RO_GROUP, "__ADM__": C.ADM_GROUP,
                 "__REPL__": "remoteman_repl", "__DB__": C.DB_NAME}.items():
        body = body.replace(k, v)
    return body


class Cluster:
    def __init__(self):
        st = C.load_state()
        self.name = st.get("name") or C.clean_name(os.environ.get("NAME") or socket.gethostname())
        self.address = C.detect_address()
        self.pg = Postgres()
        self.peers = {}
        self.servers = st.get("servers", [])
        self.settings = st.get("settings", dict(DEFAULT_SETTINGS))
        self.secret = st.get("secret")
        self.phase = "starting"
        self.message = ""
        self.op_lock = threading.RLock()
        self.stepping_down = False
        self.primary_gone_since = None
        self.receiver_bad_since = None
        self.warned = set()
        self.cert_file = self.key_file = None
        self.backups = Backups(self)
        self.sampler = None
        self._pool = concurrent.futures.ThreadPoolExecutor(max_workers=8)
        self._ssl = ssl.create_default_context()
        self._ssl.check_hostname = False
        self._ssl.verify_mode = ssl.CERT_NONE

    # ------------------------------------------------------------ small things
    @property
    def repl_password(self):
        return C.load_state().get("repl_password")

    def status(self):
        if self.pg.busy:
            return self.pg.busy
        if self.stepping_down:
            return "handing over"
        if self.phase != "running":
            return self.phase
        if not self.pg.running():
            return "stopped"
        return "running"

    def setting(self, k):
        return self.settings.get(k, DEFAULT_SETTINGS.get(k, ""))

    def role(self):
        return (self.sampler.snapshot.get("role") if self.sampler else None) if self.pg.ready() else None

    # ------------------------------------------------------------ start
    def boot(self):
        for d in (C.BASE, C.LOG_DIR, C.TLS_DIR, C.BACKUP_DIR):
            os.makedirs(d, exist_ok=True)
        store.init()
        store.close_open_logins()
        self.ensure_cert()
        if not C.valid_server_name(self.name):
            raise SystemExit(f"'{self.name}' can't be used as a server name. Set NAME to letters, digits and _ only.")
        if self.pg.initialized() and not C.load_state().get("setup_done"):
            log.warning("The first setup didn't finish last time; starting it over")
            shutil.rmtree(C.PGDATA, ignore_errors=True)
        if not self.pg.initialized():
            if os.environ.get("JOIN"):
                self.phase = "joining"
                self.join(os.environ["JOIN"])
            elif os.environ.get("ROLE", "").lower() == "standby":
                self.phase = "waiting for a join key"
                raise SystemExit("ROLE is standby but no JOIN key is set. Get one from the admin page: Servers > Add server.")
            else:
                self.bootstrap()
        else:
            key = os.environ.get("JOIN", "").strip()
            if key and C.load_state().get("join_key") != key:
                log.warning("\n%s\n JOIN is set, but this server already has its own database, so the key is ignored.\n"
                            " A join key only works on a server's very first start. To join this server to the cluster,\n"
                            " wipe it and start again with a new key (this deletes this server's data):\n"
                            "   docker compose down -v && docker compose up -d\n%s", "=" * 78, "=" * 78)
            self.pre_start()
            self.pg.start()
            if not self._in_recovery():
                self.ensure_schema()
        self.refresh()
        self.phase = "running"
        C.save_state(name=self.name)
        threading.Thread(target=self._poll_loop, daemon=True, name="peers").start()
        threading.Thread(target=self._watch_loop, daemon=True, name="watch").start()

    def _in_recovery(self):
        return C.q1("SELECT pg_is_in_recovery()")

    def bootstrap(self):
        log.info("First start: setting up %s as the primary", self.name)
        self.pg.initdb()
        self.pg.write_settings()
        self.pg.start()
        app_pw = os.environ.get("REMOTEMAN_PASSWORD") or C.rand(20)
        repl_pw, secret = C.rand(32), C.rand(40)
        C.q(f"CREATE ROLE {C.APP_USER} LOGIN PASSWORD %s", (app_pw,))
        C.q(f"CREATE ROLE {C.REPL_GROUP} NOLOGIN")
        C.q(f"CREATE ROLE {C.repl_role(self.name)} LOGIN REPLICATION PASSWORD %s IN ROLE {C.REPL_GROUP}", (repl_pw,))
        C.q(f"CREATE DATABASE {C.DB_NAME} OWNER {C.APP_USER}")
        C.save_state(name=self.name, repl_password=repl_pw)
        self.ensure_schema()
        C.q("INSERT INTO remoteman_cluster.servers (name, address, pg_port, admin_port, state, joined_at) "
            "VALUES (%s, %s, %s, %s, 'joined', now())", (self.name, self.address, C.PG_PORT, C.ADMIN_PORT), db=C.DB_NAME)
        settings = dict(DEFAULT_SETTINGS, secret=secret)
        for k, v in settings.items():
            C.q("INSERT INTO remoteman_cluster.settings VALUES (%s, %s) ON CONFLICT (key) DO NOTHING", (k, v), db=C.DB_NAME)
        self.secret = secret
        C.save_state(secret=secret, setup_done=True)
        store.add_event("server", f"{self.name} set up as the primary")
        bar = "=" * 78
        print(f"\n{bar}\n RemoteMan server {self.name} is ready as the primary.\n\n"
              f" Admin page:   https://{self.address}:{C.ADMIN_PORT}\n"
              f" Sign in as:   {C.APP_USER}\n"
              f" Password:     {app_pw if not os.environ.get('REMOTEMAN_PASSWORD') else '(the REMOTEMAN_PASSWORD you set)'}\n\n"
              f" RemoteMan data source (PostgreSQL), connection string:\n"
              f"   Host={self.address}:{C.PG_PORT};Database={C.DB_NAME};Username={C.APP_USER};Password=<password>;"
              f"Target Session Attributes=primary\n\n"
              f" Add a second server on the admin page: Servers > Add server.\n"
              f" This password is shown only now. Give each person their own login under Users.\n{bar}\n", flush=True)

    def ensure_schema(self):
        """Roles, grants, the login functions and the cluster tables. Safe to run on every start of a primary."""
        exists = lambda r: C.q1("SELECT 1 FROM pg_roles WHERE rolname = %s", (r,)) == 1  # noqa: E731
        for role in (C.RO_GROUP, C.ADM_GROUP, C.REPL_GROUP):
            if not exists(role):
                C.q(f"CREATE ROLE {role} NOLOGIN")
        C.q(f"""GRANT pg_monitor TO {C.REPL_GROUP};
            GRANT EXECUTE ON FUNCTION pg_ls_dir(text, boolean, boolean) TO {C.REPL_GROUP};
            GRANT EXECUTE ON FUNCTION pg_stat_file(text, boolean) TO {C.REPL_GROUP};
            GRANT EXECUTE ON FUNCTION pg_read_binary_file(text) TO {C.REPL_GROUP};
            GRANT EXECUTE ON FUNCTION pg_read_binary_file(text, bigint, bigint, boolean) TO {C.REPL_GROUP};""")
        if not exists(C.repl_role(self.name)) and self.repl_password:
            C.q(f"CREATE ROLE {C.repl_role(self.name)} LOGIN REPLICATION PASSWORD %s IN ROLE {C.REPL_GROUP}",
                (self.repl_password,))
        ro, app = C.RO_GROUP, C.APP_USER
        C.q(f"""GRANT CONNECT ON DATABASE {C.DB_NAME} TO {ro};
            GRANT USAGE ON SCHEMA public TO {ro};
            GRANT SELECT ON ALL TABLES IN SCHEMA public TO {ro};
            ALTER DEFAULT PRIVILEGES FOR ROLE {app} IN SCHEMA public GRANT SELECT ON TABLES TO {ro};""", db=C.DB_NAME)
        if C.q1("SELECT to_regclass('public.remoteman_presence') IS NOT NULL", db=C.DB_NAME):
            C.q(f"GRANT SELECT, INSERT, UPDATE, DELETE ON remoteman_presence TO {ro}", db=C.DB_NAME)
        C.q(login_functions_sql(), db=C.DB_NAME)
        C.q(SCHEMA_SQL, db=C.DB_NAME)
        self.sync_role_settings()

    def sync_role_settings(self):
        """Editors act as the shared login once connected (see remoteman-db.sh); read-only people don't."""
        rows = C.q(f"""SELECT format('ALTER ROLE %%I IN DATABASE %%I SET role = %%L', r.rolname, '{C.DB_NAME}', '{C.APP_USER}')
              FROM pg_auth_members m JOIN pg_roles r ON r.oid = m.member
              WHERE m.roleid = '{C.APP_USER}'::regrole AND NOT r.rolsuper
                AND NOT EXISTS (SELECT FROM pg_db_role_setting s WHERE s.setrole = r.oid
                                AND s.setdatabase = (SELECT oid FROM pg_database WHERE datname = '{C.DB_NAME}')
                                AND 'role={C.APP_USER}' = ANY (s.setconfig))
            UNION ALL
            SELECT format('ALTER ROLE %%I IN DATABASE %%I RESET role', r.rolname, '{C.DB_NAME}')
              FROM pg_db_role_setting s JOIN pg_roles r ON r.oid = s.setrole
              WHERE s.setdatabase = (SELECT oid FROM pg_database WHERE datname = '{C.DB_NAME}')
                AND 'role={C.APP_USER}' = ANY (s.setconfig) AND NOT pg_has_role(r.oid, '{C.APP_USER}', 'MEMBER')""", ())
        for (stmt,) in rows or []:
            C.q(stmt)

    def join(self, key):
        k = C.decode_key(key)
        name = k["n"]
        if os.environ.get("NAME") and C.clean_name(os.environ["NAME"]) != name:
            log.warning("NAME is %s but the join key is for %s; using %s", os.environ["NAME"], name, name)
        self.name = name
        C.save_state(name=name, repl_password=k["pw"], join_key=key.strip())
        log.info("Joining as %s", name)
        deadline = time.time() + 600
        last_err = ""
        while True:
            for hp in k["h"]:
                host, _, port = hp.rpartition(":")
                try:
                    c = C.remote_conn(host, int(port), C.repl_role(name), k["pw"])
                    rec = c.cursor()
                    rec.execute("SELECT pg_is_in_recovery()")
                    standby = rec.fetchone()[0]
                    c.close()
                    if standby:
                        continue
                    self.pg.basebackup(host, int(port), name, k["pw"])
                    self.pg.write_settings(upstream=(host, int(port)), name=name, repl_password=k["pw"])
                    self.pg.start()
                    C.save_state(setup_done=True)
                    store.add_event("server", f"{name} joined as a standby of {host}")
                    return
                except Exception as e:  # noqa: BLE001
                    last_err = str(e).strip().splitlines()[0] if str(e).strip() else repr(e)
            if "password authentication failed" in last_err:
                raise SystemExit("The join key was refused: it has expired (30 minutes) or was replaced. "
                                 "Make a new one on the admin page (Servers > Add server) and put it in JOIN.")
            if time.time() > deadline:
                raise SystemExit(f"Couldn't reach the primary to join: {last_err}")
            log.warning("Can't join yet (%s); trying again in 10 s", last_err)
            time.sleep(10)

    def pre_start(self):
        """Before PostgreSQL starts: if another server took over while this one was down, come back as its standby."""
        primary = self.find_primary(timeout=3)
        pw = self.repl_password
        if not self.pg.is_standby_on_disk():
            mine = self.pg.disk_timeline()
            if primary and primary.get("timeline", 0) > mine:
                log.info("%s took over while this server was down; rejoining as its standby", primary["name"])
                self.pg.write_settings()
                self.pg.rewind(primary["address"], primary["pg_port"], self.name, pw)
                self.pg.write_settings(upstream=(primary["address"], primary["pg_port"]), name=self.name, repl_password=pw)
                store.add_event("server", f"{self.name} came back after {primary['name']} took over, and rejoined as its standby")
            else:
                self.pg.write_settings()
        else:
            up = (primary["address"], primary["pg_port"]) if primary else self.pg.upstream_in_settings()
            self.pg.write_settings(upstream=up, name=self.name, repl_password=pw)

    # ------------------------------------------------------------ TLS
    def ensure_cert(self):
        if os.environ.get("TLS_CERT") and os.environ.get("TLS_KEY"):
            self.cert_file, self.key_file = os.environ["TLS_CERT"], os.environ["TLS_KEY"]
            return
        self.cert_file, self.key_file = os.path.join(C.TLS_DIR, "cert.pem"), os.path.join(C.TLS_DIR, "key.pem")
        renew = not os.path.exists(self.cert_file)
        if not renew:
            r = subprocess.run(["openssl", "x509", "-checkend", str(30 * 86400), "-noout", "-in", self.cert_file], capture_output=True)
            renew = r.returncode != 0
        if renew:
            san = f"DNS:{socket.gethostname()},DNS:localhost,IP:127.0.0.1"
            if re.fullmatch(r"[\d.]+", self.address):
                san += f",IP:{self.address}"
            subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-sha256", "-nodes", "-days", "825",
                            "-keyout", self.key_file, "-out", self.cert_file, "-subj", f"/CN=RemoteMan {self.name}",
                            "-addext", f"subjectAltName={san}"], check=True, capture_output=True)
            os.chmod(self.key_file, 0o600)
            log.info("Made a self-signed certificate for the admin page")

    # ------------------------------------------------------------ peers
    def refresh(self):
        """Re-read the server list and settings from the (replicated) cluster tables."""
        try:
            rows = C.q("""SELECT name, address, pg_port, admin_port, state,
                    extract(epoch FROM added_at), extract(epoch FROM joined_at), extract(epoch FROM key_expires)
                FROM remoteman_cluster.servers ORDER BY added_at, name""", db=C.DB_NAME)
            servers = [{"name": r[0], "address": r[1], "pg_port": r[2], "admin_port": r[3], "state": r[4],
                        "added_at": r[5] and float(r[5]), "joined_at": r[6] and float(r[6]),
                        "key_expires": r[7] and float(r[7])} for r in rows]
            settings = dict(C.q("SELECT key, value FROM remoteman_cluster.settings", db=C.DB_NAME))
        except Exception as e:  # noqa: BLE001
            log.debug("refresh: %s", e)
            return
        secret = settings.pop("secret", None)
        changed = servers != self.servers or settings != self.settings or secret != self.secret
        self.servers, self.settings = servers, settings
        if secret:
            self.secret = secret
        if changed:
            C.save_state(servers=servers, settings=settings, secret=self.secret)

    def others(self):
        return [s for s in self.servers if s["name"] != self.name and s["state"] != "removed" and s["address"]]

    def call(self, server, method, path, body=None, timeout=3.0):
        """Calls another server's internal API."""
        conn = http.client.HTTPSConnection(server["address"], server["admin_port"], timeout=timeout, context=self._ssl)
        try:
            data = json.dumps(body).encode() if body is not None else None
            conn.request(method, path, body=data, headers={"X-RemoteMan-Secret": self.secret or "",
                                                            "Content-Type": "application/json"})
            r = conn.getresponse()
            payload = json.loads(r.read() or b"{}")
            if r.status >= 400:
                raise RuntimeError(payload.get("error") or f"HTTP {r.status}")
            return payload
        finally:
            conn.close()

    def _poll_one(self, s):
        t0 = time.time()
        rec = {"name": s["name"], "server": s, "ok": False}
        rec["lat"] = C.tcp_latency(s["address"], s["pg_port"])
        try:
            snap = self.call(s, "GET", "/internal/state", timeout=1.5)
            t1 = time.time()
            rec.update(ok=True, snap=snap, seen=t1, offset=snap.get("time", t1) - (t0 + t1) / 2)
        except Exception as e:  # noqa: BLE001
            rec["err"] = str(e) or e.__class__.__name__
            old = self.peers.get(s["name"])
            if old and old.get("snap"):
                rec["last_snap"] = old.get("snap") if old.get("ok") else old.get("last_snap")
                rec["seen"] = old.get("seen")
        return rec

    def poll_peers(self):
        others = self.others()
        if not self.secret:
            return
        results = list(self._pool.map(self._poll_one, others))
        names = {s["name"] for s in others}
        peers = {r["name"]: r for r in results}
        self.peers = peers
        for n in list(self.peers):
            if n not in names:
                self.peers.pop(n, None)

    def _poll_loop(self):
        n = 0
        while True:
            t = time.time()
            try:
                if n % 5 == 0 and self.pg.ready():
                    self.refresh()
                self.poll_peers()
                if self.on_poll:
                    self.on_poll()
            except Exception as e:  # noqa: BLE001
                log.debug("poll: %s", e)
            n += 1
            time.sleep(max(0.1, 1 - (time.time() - t)))

    on_poll = None

    def find_primary(self, timeout=1.5):
        """The live primary among the other servers (newest timeline wins), asking them directly."""
        best = None
        for s in self.others():
            try:
                snap = self.call(s, "GET", "/internal/state", timeout=timeout)
            except Exception:  # noqa: BLE001
                continue
            if snap.get("role") == "primary" and (not best or snap.get("timeline", 0) > best.get("timeline", 0)):
                best = snap
        return best

    def primary_peer(self):
        best = None
        for p in self.peers.values():
            snap = p.get("snap") if p.get("ok") else None
            if snap and snap.get("role") == "primary" and not snap.get("status", "").startswith("handing"):
                if not best or snap.get("timeline", 0) > best.get("timeline", 0):
                    best = snap
        return best

    # ------------------------------------------------------------ watcher
    def _watch_loop(self):
        while True:
            time.sleep(2)
            if self.phase != "running" or self.stepping_down or self.pg.busy or not self.pg.ready():
                continue
            try:
                me = next((s for s in self.servers if s["name"] == self.name), None)
                if me and me["state"] == "removed":
                    self.removed()
                    continue
                if self._in_recovery():
                    self.standby_duties()
                else:
                    self.primary_duties()
            except Exception as e:  # noqa: BLE001
                log.warning("watcher: %s", e)

    def primary_duties(self):
        snap = self.sampler.snapshot if self.sampler else {}
        mine = snap.get("timeline") or C.q1("SELECT timeline_id FROM pg_control_checkpoint()")
        for p in self.peers.values():
            ps = p.get("snap") if p.get("ok") else None
            if ps and ps.get("role") == "primary":
                if ps.get("timeline", 0) > mine:
                    store.add_event("failover", f"{ps['name']} is the primary on a newer timeline; {self.name} steps down and rejoins as its standby")
                    self.rejoin(ps)
                    return
                key = ("two", ps["name"])
                if key not in self.warned:
                    self.warned.add(key)
                    store.add_event("warning", f"Both {self.name} and {ps['name']} are primary. Make one of them a standby (Servers > Rejoin).")
        # Slots, so every other server can stream (and catch up after being away).
        want = {C.slot_name(s["name"]) for s in self.servers if s["name"] != self.name and s["state"] != "removed"}
        have = {r[0] for r in C.q("SELECT slot_name FROM pg_replication_slots WHERE slot_type = 'physical'")}
        for sl in want - have:
            C.q("SELECT pg_create_physical_replication_slot(%s, true)", (sl,))
        for sl in have - want:
            if sl.startswith("rm_"):
                C.q("SELECT pg_drop_replication_slot(%s) WHERE NOT EXISTS (SELECT FROM pg_replication_slots WHERE slot_name = %s AND active)", (sl, sl))
        # Servers that finished joining.
        streaming = {r[0]: r[1] for r in C.q("SELECT application_name, host(client_addr) FROM pg_stat_replication")}
        for s in self.servers:
            if s["state"] in ("pending", "expired") and s["name"] in streaming:
                C.q("UPDATE remoteman_cluster.servers SET state = 'joined', joined_at = now(), key_expires = NULL, "
                    "address = CASE WHEN address = '' THEN %s ELSE address END WHERE name = %s",
                    (streaming[s["name"]] or "", s["name"]), db=C.DB_NAME)
                C.q(f"ALTER ROLE {C.repl_role(s['name'])} VALID UNTIL 'infinity'")
                store.add_event("server", f"{s['name']} joined the cluster")
                self.refresh()
            elif s["state"] == "pending" and s["key_expires"] and s["key_expires"] < time.time():
                C.q("UPDATE remoteman_cluster.servers SET state = 'expired' WHERE name = %s", (s["name"],), db=C.DB_NAME)
        me = next((s for s in self.servers if s["name"] == self.name), None)
        if me and (me["address"] != self.address or me["pg_port"] != C.PG_PORT or me["admin_port"] != C.ADMIN_PORT):
            C.q("UPDATE remoteman_cluster.servers SET address = %s, pg_port = %s, admin_port = %s WHERE name = %s",
                (self.address, C.PG_PORT, C.ADMIN_PORT, self.name), db=C.DB_NAME)
        elif not me:
            C.q("INSERT INTO remoteman_cluster.servers (name, address, pg_port, admin_port, state, joined_at) "
                "VALUES (%s, %s, %s, %s, 'joined', now()) ON CONFLICT (name) DO NOTHING",
                (self.name, self.address, C.PG_PORT, C.ADMIN_PORT), db=C.DB_NAME)
        self.sync_role_settings()
        self.primary_gone_since = None

    def _report_own_row(self, primary):
        """A standby can't write the cluster table, so it asks the primary to fix its row when its address or
        ports changed (for example a new ADMIN_PORT); otherwise the others keep calling the old port."""
        me = next((s for s in self.servers if s["name"] == self.name), None)
        if not me or (me["address"], me["pg_port"], me["admin_port"]) == (self.address, C.PG_PORT, C.ADMIN_PORT):
            return
        if time.time() - getattr(self, "_reported_at", 0) < 10:
            return
        self._reported_at = time.time()
        try:
            self.call(primary, "POST", "/internal/server-row",
                      {"name": self.name, "address": self.address, "pg_port": C.PG_PORT, "admin_port": C.ADMIN_PORT})
        except Exception as e:  # noqa: BLE001
            log.warning("Telling %s about this server's new address or ports: %s", primary["name"], e)

    def update_server_row(self, b):
        """On the primary: a standby reports its own address and ports."""
        if self._in_recovery():
            raise RuntimeError("Only the primary updates the server list.")
        name = str(b.get("name", ""))
        if not any(s["name"] == name for s in self.servers):
            raise RuntimeError(f"There's no server called {name}.")
        C.q("UPDATE remoteman_cluster.servers SET address = %s, pg_port = %s, admin_port = %s WHERE name = %s",
            (str(b.get("address", "")).strip(), int(b["pg_port"]), int(b["admin_port"]), name), db=C.DB_NAME)
        self.refresh()
        return {"ok": True}

    def standby_duties(self):
        primary = self.primary_peer()
        snap = self.sampler.snapshot if self.sampler else {}
        if primary:
            self.primary_gone_since = None
            want = (primary["address"], primary["pg_port"])
            if self.pg.upstream_in_settings() != want:
                log.info("Following %s at %s:%s", primary["name"], *want)
                self.pg.write_settings(upstream=want, name=self.name, repl_password=self.repl_password)
                self.pg.reload()
                self.receiver_bad_since = time.time()
                store.add_event("server", f"{self.name} now follows {primary['name']}")
                return
            self._report_own_row(primary)
            if snap.get("receiver") == "streaming":
                self.receiver_bad_since = None
            else:
                self.receiver_bad_since = self.receiver_bad_since or time.time()
                if time.time() - self.receiver_bad_since > 30:
                    store.add_event("server", f"{self.name} couldn't stream from {primary['name']}; rewinding and rejoining")
                    self.receiver_bad_since = None
                    self.rejoin(primary)
            return
        # No primary anywhere.
        self.receiver_bad_since = None
        if self.setting("auto_failover") != "on":
            self.primary_gone_since = None
            return
        self.primary_gone_since = self.primary_gone_since or time.time()
        gone = time.time() - self.primary_gone_since
        if gone < float(self.setting("failover_seconds") or 30):
            return
        joined = [s for s in self.servers if s["state"] == "joined"]
        reachable = 1 + sum(1 for p in self.peers.values() if p.get("ok"))
        if len(joined) >= 3 and reachable * 2 <= len(joined):
            self._warn_once("minority", f"{self.name} can't see most of the cluster, so it won't take over on its own.")
            return
        if not self.witness_ok():
            self._warn_once("witness", f"{self.name} can't reach the witness, so it won't take over on its own.")
            return
        cands = [(snap.get("lsn") or 0, self.name)]
        for p in self.peers.values():
            ps = p.get("snap") if p.get("ok") else None
            if ps and ps.get("role") == "standby":
                cands.append((ps.get("lsn") or 0, ps["name"]))
        best = sorted(cands, key=lambda c: (-c[0], c[1]))[0]
        if best[1] != self.name:
            return
        store.add_event("failover", f"No primary for {int(gone)} s: {self.name} takes over as the primary")
        self.promote()
        self.warned.clear()

    def _warn_once(self, key, text):
        if key not in self.warned:
            self.warned.add(key)
            store.add_event("warning", text)

    def witness_ok(self):
        w = (self.setting("witness") or "").strip()
        if w == "auto":
            w = default_witness()
        if not w or w == "off":
            return True
        host, _, port = w.partition(":")
        if port:
            return C.tcp_latency(host, int(port), timeout=2) is not None
        return subprocess.run(["ping", "-c1", "-W2", host], capture_output=True).returncode == 0

    # ------------------------------------------------------------ operations
    def promote(self):
        with self.op_lock:
            if not self._in_recovery():
                return "already primary"
            self.pg.busy = "taking over"
            try:
                C.q("SELECT pg_promote(true, 60)")
                C.q("CHECKPOINT")
                self.pg.write_settings()
                self.ensure_schema()
            finally:
                self.pg.busy = ""
            store.add_event("failover", f"{self.name} is now the primary")
            self.primary_gone_since = None
            return "promoted"

    def switchover(self):
        """Planned hand-over: the current primary stops cleanly, this server replays everything and takes over."""
        with self.op_lock:
            if not self._in_recovery():
                return "already primary"
            primary = self.primary_peer()
            if not primary:
                raise RuntimeError("There's no reachable primary to hand over from. Use Force failover instead.")
            srv = next(s for s in self.servers if s["name"] == primary["name"])
            res = self.call(srv, "POST", "/internal/demote", {"to": self.name}, timeout=150)
            target = res["lsn"]
            self.pg.busy = "catching up"
            try:
                t = time.time()
                while time.time() - t < 30:
                    replayed = float(C.q1("SELECT pg_wal_lsn_diff(pg_last_wal_replay_lsn(), '0/0')") or 0)
                    if replayed >= target:
                        break
                    time.sleep(0.2)
                else:
                    raise RuntimeError(f"{self.name} didn't catch up with {primary['name']} in time; {primary['name']} starts again as the primary.")
            finally:
                self.pg.busy = ""
            store.add_event("failover", f"Switch-over: {primary['name']} handed over to {self.name}")
            return self.promote()

    def demote(self, to):
        """Called on the primary for a switch-over: stop cleanly, report where the WAL ends, then follow the new primary."""
        if self._in_recovery():
            raise RuntimeError(f"{self.name} isn't the primary.")
        target = next((s for s in self.servers if s["name"] == to), None)
        if not target:
            raise RuntimeError(f"There's no server called {to}.")
        with self.op_lock:
            self.stepping_down = True
            C.q("CHECKPOINT")
            self.pg.stop("fast")
            lsn = lsn_bytes(self.pg.controldata()["Latest checkpoint location"])
        threading.Thread(target=self._follow_after_demote, args=(target,), daemon=True).start()
        return {"lsn": lsn}

    def _follow_after_demote(self, target):
        try:
            t = time.time()
            while time.time() - t < 60:
                try:
                    snap = self.call(target, "GET", "/internal/state", timeout=2)
                    if snap.get("role") == "primary":
                        with self.op_lock:
                            pw = self.repl_password
                            self.pg.rewind(target["address"], target["pg_port"], self.name, pw)
                            self.pg.write_settings(upstream=(target["address"], target["pg_port"]), name=self.name, repl_password=pw)
                            self.pg.start()
                        store.add_event("server", f"{self.name} is now a standby of {target['name']}")
                        return
                except Exception:  # noqa: BLE001
                    pass
                time.sleep(1)
            store.add_event("warning", f"{target['name']} didn't take over; {self.name} stays the primary")
            self.pg.start()
        except Exception as e:  # noqa: BLE001
            store.add_event("warning", f"{self.name} couldn't follow {target['name']}: {e}")
            self.pg.write_settings()
            try:
                self.pg.start()
            except Exception:  # noqa: BLE001
                pass
        finally:
            self.stepping_down = False

    def rejoin(self, primary=None):
        """Become a standby of the current primary (rewinding if needed)."""
        with self.op_lock:
            primary = primary or self.primary_peer() or self.find_primary()
            if not primary or primary.get("name") == self.name:
                raise RuntimeError("There's no other primary to follow.")
            pw = self.repl_password
            self.pg.stop("fast")
            try:
                self.pg.write_settings()
                how = self.pg.rewind(primary["address"], primary["pg_port"], self.name, pw)
            finally:
                up = (primary["address"], primary["pg_port"])
                self.pg.write_settings(upstream=up, name=self.name, repl_password=pw)
                if not os.path.exists(os.path.join(C.PGDATA, "standby.signal")) and self.pg.initialized():
                    open(os.path.join(C.PGDATA, "standby.signal"), "w").close()
                self.pg.start()
            store.add_event("server", f"{self.name} rejoined as a standby of {primary['name']} ({how})")
            return how

    def add_server(self, name, address):
        if self._in_recovery():
            raise RuntimeError("Servers are added on the primary.")
        name = C.clean_name(name)
        if not C.valid_server_name(name):
            raise RuntimeError("Server names: letters, digits and _ only, up to 30 characters.")
        existing = next((s for s in self.servers if s["name"] == name), None)
        if existing and existing["state"] == "joined":
            raise RuntimeError(f"{name} is already part of the cluster.")
        pw = C.rand(32)
        role = C.repl_role(name)
        if C.q1("SELECT 1 FROM pg_roles WHERE rolname = %s", (role,)):
            C.q(f"ALTER ROLE {role} LOGIN PASSWORD %s VALID UNTIL %s", (pw, _in_minutes(30)))
        else:
            C.q(f"CREATE ROLE {role} LOGIN REPLICATION PASSWORD %s VALID UNTIL %s IN ROLE {C.REPL_GROUP}", (pw, _in_minutes(30)))
        C.q("SELECT pg_create_physical_replication_slot(%s, true) WHERE NOT EXISTS "
            "(SELECT FROM pg_replication_slots WHERE slot_name = %s)", (C.slot_name(name), C.slot_name(name)))
        C.q("""INSERT INTO remoteman_cluster.servers (name, address, pg_port, admin_port, state, key_expires)
               VALUES (%s, %s, %s, %s, 'pending', now() + interval '30 minutes')
               ON CONFLICT (name) DO UPDATE SET address = excluded.address, state = 'pending', removed_at = NULL,
                   key_expires = excluded.key_expires, added_at = now()""",
            (name, (address or "").strip(), C.PG_PORT, C.ADMIN_PORT), db=C.DB_NAME)
        hosts = [f"{s['address']}:{s['pg_port']}" for s in self.servers if s["state"] == "joined" and s["address"]]
        if f"{self.address}:{C.PG_PORT}" not in hosts:
            hosts.insert(0, f"{self.address}:{C.PG_PORT}")
        key = C.encode_key({"v": 1, "n": name, "pw": pw, "h": hosts})
        self.refresh()
        store.add_event("server", f"Join key made for {name} (valid 30 minutes)")
        return {"name": name, "key": key, "compose": compose_text(name, key), "expires": time.time() + 1800}

    def remove_server(self, name):
        if self._in_recovery():
            raise RuntimeError("Servers are removed on the primary.")
        if name == self.name:
            raise RuntimeError("The primary can't be removed. Make another server the primary first.")
        if not any(s["name"] == name for s in self.servers):
            raise RuntimeError(f"There's no server called {name}.")
        C.q("UPDATE remoteman_cluster.servers SET state = 'removed', removed_at = now() WHERE name = %s", (name,), db=C.DB_NAME)
        self.refresh()

        def finish():
            time.sleep(8)   # lets the server see that it was removed before its stream is cut
            try:
                C.q(f"ALTER ROLE {C.repl_role(name)} NOLOGIN")
                C.q("SELECT pg_terminate_backend(pid) FROM pg_stat_replication WHERE application_name = %s", (name,))
                time.sleep(1)
                C.q("SELECT pg_drop_replication_slot(slot_name) FROM pg_replication_slots WHERE slot_name = %s", (C.slot_name(name),))
                C.q(f"DROP ROLE IF EXISTS {C.repl_role(name)}")
                C.q("DELETE FROM remoteman_cluster.servers WHERE name = %s", (name,), db=C.DB_NAME)
            except Exception as e:  # noqa: BLE001
                log.warning("Finishing the removal of %s: %s", name, e)
        threading.Thread(target=finish, daemon=True).start()
        store.add_event("server", f"{name} was removed from the cluster")

    def removed(self):
        if self.phase == "removed":
            return
        self.phase = "removed"
        self.pg.stop("fast")
        store.add_event("server", f"{self.name} was removed from the cluster and stopped its database. Delete this container and its volume.")

    def save_settings(self, values):
        if self._in_recovery():
            raise RuntimeError("Settings are changed on the primary.")
        for k in ("auto_failover", "failover_seconds", "witness"):
            if k in values:
                v = str(values[k]).strip()
                if k == "auto_failover" and v not in ("on", "off"):
                    raise RuntimeError("Automatic failover is on or off.")
                if k == "failover_seconds" and (not v.isdigit() or not 10 <= int(v) <= 3600):
                    raise RuntimeError("The failover delay must be 10 to 3600 seconds.")
                C.q("INSERT INTO remoteman_cluster.settings VALUES (%s, %s) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
                    (k, v), db=C.DB_NAME)
        self.refresh()
        store.add_event("server", "Failover settings changed")


def _in_minutes(n):
    return time.strftime("%Y-%m-%d %H:%M:%S+00", time.gmtime(time.time() + n * 60))


def default_witness():
    """With host networking the default gateway is the real router; in a bridge network it's Docker's, which proves nothing."""
    gw = C.default_gateway()
    return gw if gw and not gw.startswith("172.17.") else ""


IMAGE = os.environ.get("REMOTEMAN_IMAGE", "ghcr.io/langeboi/remoteman-server:latest")


def compose_text(name, key):
    return f"""services:
  remoteman:
    image: {IMAGE}
    container_name: remoteman
    network_mode: host
    restart: unless-stopped
    stop_grace_period: 60s
    environment:
      NAME: {name}
      JOIN: {key}   # works for 30 minutes, only for this server
    volumes:
      - remoteman-data:/var/lib/remoteman
  relay:
    image: {IMAGE}
    command: relay
    network_mode: host
    restart: unless-stopped

volumes:
  remoteman-data:
"""


class Backups:
    """Nightly pg_dump on whichever server is the primary. BACKUP_AT=03:00 (container time), BACKUP_KEEP=7."""

    def __init__(self, cluster):
        self.cluster = cluster
        self.at = os.environ.get("BACKUP_AT", "03:00")
        self.keep = C.env_int("BACKUP_KEEP", 7)
        self.running = False
        threading.Thread(target=self._loop, daemon=True, name="backups").start()

    def files(self):
        return sorted(glob.glob(os.path.join(C.BACKUP_DIR, "remoteman-*.dump")))

    def last(self):
        f = self.files()
        info = json.loads(store.get("last_backup") or "{}") if os.path.exists(C.ADMIN_DB) else {}
        if not f:
            return {"count": 0, **info}
        st = os.stat(f[-1])
        return {"count": len(f), "file": os.path.basename(f[-1]), "ts": st.st_mtime, "size": st.st_size,
                "dur": info.get("dur"), "error": info.get("error"), "at": self.at}

    def run(self):
        if self.running:
            raise RuntimeError("A backup is already running.")
        self.running = True
        t = time.time()
        path = os.path.join(C.BACKUP_DIR, time.strftime("remoteman-%Y%m%d-%H%M%S.dump"))
        try:
            subprocess.run([tool("pg_dump"), "-h", C.SOCKET_DIR, "-p", str(C.PG_PORT), "-U", "postgres", "-Fc",
                            "-d", C.DB_NAME, "-f", path], check=True, capture_output=True, text=True)
            for old in self.files()[:-self.keep]:
                os.remove(old)
            dur = time.time() - t
            store.put("last_backup", json.dumps({"dur": round(dur, 1)}))
            store.add_event("backup", f"Backup done on {self.cluster.name}: {os.path.basename(path)} "
                                      f"({os.path.getsize(path) / 1048576:.1f} MB, {dur:.0f} s)")
            return os.path.basename(path)
        except subprocess.CalledProcessError as e:
            store.put("last_backup", json.dumps({"error": (e.stderr or "").strip()[-300:]}))
            store.add_event("warning", f"Backup failed on {self.cluster.name}: {(e.stderr or '').strip()[-200:]}")
            raise RuntimeError("The backup failed. See the activity feed.")
        finally:
            self.running = False

    def _loop(self):
        while True:
            time.sleep(30)
            if self.at == "off" or not re.fullmatch(r"\d{1,2}:\d{2}", self.at):
                continue
            try:
                if self.cluster.phase != "running" or self.cluster.role() != "primary":
                    continue
                h, m = map(int, self.at.split(":"))
                now = time.localtime()
                if (now.tm_hour, now.tm_min) < (h, m):
                    continue
                today = time.strftime("%Y%m%d")
                if any(os.path.basename(f).startswith(f"remoteman-{today}") for f in self.files()):
                    continue
                self.run()
            except Exception as e:  # noqa: BLE001
                log.debug("backup loop: %s", e)

