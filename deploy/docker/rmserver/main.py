"""Entry point: `serve` runs a database server, `relay` the shared-session relay, anything else is a remoteman-db command."""
import http.client
import json
import os
import signal
import ssl
import sys
import threading
import time

from . import common as C
from .common import log


def serve():
    C.setup_logging()
    from . import cluster as cl_mod
    from . import logins, metrics, store, web

    cluster = cl_mod.Cluster()
    stopping = threading.Event()

    def on_term(signum, frame):
        if stopping.is_set():
            return
        stopping.set()
        log.info("Stopping")
        try:
            cluster.pg.stop("fast", wait=50)
        finally:
            os._exit(0)

    signal.signal(signal.SIGTERM, on_term)
    signal.signal(signal.SIGINT, on_term)
    cluster.sampler = metrics.Sampler(cluster)
    try:
        cluster.boot()
    except SystemExit as e:
        log.error("%s", e)
        cluster.pg.stop("fast")
        sys.exit(1)
    except Exception as e:  # noqa: BLE001
        log.exception("Start failed: %s", e)
        cluster.pg.stop("fast")
        sys.exit(1)
    logins.LoginTailer()
    web.serve(cluster)
    last_prune = 0
    while not stopping.is_set():
        if time.time() - last_prune > 3600:
            last_prune = time.time()
            try:
                store.prune()
            except Exception:  # noqa: BLE001
                pass
        time.sleep(5)


def relay():
    script = "/opt/remoteman/remoteman-relay.py"
    os.execvp("python3", ["python3", script, "--port", str(C.RELAY_PORT), "--bind", os.environ.get("RELAY_BIND", "0.0.0.0")])


def health():
    import socket
    import subprocess
    try:
        with open("/proc/1/cmdline", "rb") as f:
            relay_mode = b"relay" in f.read()
    except OSError:
        relay_mode = False
    if relay_mode:
        try:
            socket.create_connection(("127.0.0.1", C.RELAY_PORT), timeout=3).close()
            sys.exit(0)
        except OSError:
            sys.exit(1)
    if C.load_state().get("removed"):
        sys.exit(0)
    r = subprocess.run(["pg_isready", "-q", "-h", C.SOCKET_DIR, "-p", str(C.PG_PORT)])
    sys.exit(0 if r.returncode == 0 else 1)


# ---------------------------------------------------------------- remoteman-db (inside the container)
USAGE = """remoteman-db: manage this RemoteMan server (run with: docker exec remoteman remoteman-db <command>)

  status                     every server, its role and how far behind it is
  conn                       the connection string for RemoteMan (without a password)
  join-key <name> [address]  a 30 minute join key and compose file for a new server
  promote [--force]          make this server the primary (switch-over; --force when the primary is gone)
  rejoin                     make this server a standby of the current primary again
  backup                     back up the database now (on the primary)
  password <login> [pw]      new password for a login (on the primary); prints the connection string
  logs                       the last lines of this server's PostgreSQL log
  oxidized [new-token]       Oxidized config for reading its device list from RemoteMan (new-token: replace the token)
"""


def call(method, path, body=None, timeout=300):
    st = C.load_state()
    ctx = ssl.create_default_context()
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE
    c = http.client.HTTPSConnection("127.0.0.1", C.ADMIN_PORT, timeout=timeout, context=ctx)
    c.request(method, path, body=json.dumps(body).encode() if body is not None else None,
              headers={"X-RemoteMan-Secret": st.get("secret", ""), "Content-Type": "application/json"})
    r = c.getresponse()
    data = json.loads(r.read() or b"{}")
    if r.status >= 400:
        sys.exit("xx " + (data.get("error") if isinstance(data, dict) else str(data)))
    return data


def cli(args):
    if not args or args[0] in ("-h", "--help", "help"):
        print(USAGE)
        return
    cmd, rest = args[0], args[1:]
    st = C.load_state()
    if cmd == "status":
        me = call("GET", "/internal/state")
        rows = [(me["name"], me)]
        for s in st.get("servers", []):
            if s["name"] == me["name"] or s["state"] == "removed":
                continue
            try:
                from .cluster import Cluster  # noqa: F401
                ctx = ssl.create_default_context()
                ctx.check_hostname = False
                ctx.verify_mode = ssl.CERT_NONE
                c = http.client.HTTPSConnection(s["address"], s["admin_port"], timeout=3, context=ctx)
                c.request("GET", "/internal/state", headers={"X-RemoteMan-Secret": st.get("secret", "")})
                rows.append((s["name"], json.loads(c.getresponse().read())))
            except Exception:  # noqa: BLE001
                rows.append((s["name"], {"status": "unreachable" if s["state"] == "joined" else s["state"], "address": s["address"]}))
        prim = next((r for _, r in rows if r.get("role") == "primary"), {})
        lag = {x["name"]: x for x in prim.get("repl", [])}
        print(f"{'SERVER':<14}{'ADDRESS':<18}{'ROLE':<10}{'STATE':<16}{'BEHIND':<12}")
        for name, r in rows:
            behind = ""
            if name in lag:
                behind = f"{lag[name]['lag']:.1f} s"
            print(f"{name:<14}{(r.get('address') or ''):<18}{(r.get('role') or '-'):<10}{(r.get('status') or ''):<16}{behind:<12}")
        print(f"\nAutomatic failover: {st.get('settings', {}).get('auto_failover', 'on')}")
    elif cmd == "conn":
        hosts = ",".join(f"{s['address']}:{s['pg_port']}" for s in st.get("servers", []) if s["state"] == "joined")
        print(f"Host={hosts};Database={C.DB_NAME};Username=<login>;Password=<password>;Target Session Attributes=primary")
    elif cmd == "join-key":
        if not rest:
            sys.exit("Use: remoteman-db join-key <name> [address]")
        res = call("POST", "/internal/add-server", {"name": rest[0], "address": rest[1] if len(rest) > 1 else ""})
        print(f"Join key for {res['name']} (works for 30 minutes):\n\n  {res['key']}\n\ncompose.yaml for {res['name']}:\n")
        print(res["compose"])
    elif cmd == "promote":
        mode = "force" if "--force" in rest else "switchover"
        print(call("POST", "/internal/promote", {"mode": mode}).get("result"))
    elif cmd == "rejoin":
        print(call("POST", "/internal/rejoin", {}).get("result"))
    elif cmd == "backup":
        print("Saved", call("POST", "/internal/backup", {}).get("file"))
    elif cmd == "password":
        if not rest:
            sys.exit("Use: remoteman-db password <login> [password]")
        name, pw = rest[0], (rest[1] if len(rest) > 1 else C.rand(20))
        if C.q1("SELECT pg_is_in_recovery()"):
            sys.exit("This server is a standby. Run it on the primary.")
        if not C.q1("SELECT 1 FROM pg_roles WHERE rolname = %s", (name,)):
            sys.exit(f"There's no login called {name}.")
        import psycopg2.sql as sql
        C.q(sql.SQL("ALTER ROLE {} LOGIN PASSWORD %s").format(sql.Identifier(name)).as_string(C.local_conn()), (pw,))
        hosts = ",".join(f"{s['address']}:{s['pg_port']}" for s in st.get("servers", []) if s["state"] == "joined")
        print(f"New password for {name}: {pw}\n\nHost={hosts};Database={C.DB_NAME};Username={name};Password={pw};Target Session Attributes=primary")
    elif cmd == "oxidized":
        from . import oxidized
        if rest and rest[0] == "new-token":
            call("POST", "/internal/oxidized-token", {})
        tok = oxidized.token()
        if not tok:
            sys.exit("The Oxidized token isn't made yet. It's created when the primary starts.")
        print("Put this in Oxidized's config in place of its source: section, then restart Oxidized:\n")
        print(oxidized.config_snippet(C.detect_address(), tok))
        print(f"Devices now: {len(oxidized.nodes())}")
    elif cmd == "logs":
        for e in reversed(call("GET", "/internal/logs?lines=60")):
            print(e.get("ts"), e.get("level"), e.get("message"))
    else:
        print(USAGE)
        sys.exit(1)


def main():
    args = sys.argv[1:]
    prog = os.path.basename(sys.argv[0])
    if prog == "remoteman-db":
        return cli(args)
    if not args or args[0] == "serve":
        return serve()
    if args[0] == "relay":
        return relay()
    if args[0] == "health":
        return health()
    if args[0] == "db":
        return cli(args[1:])
    return cli(args)


if __name__ == "__main__":
    main()
