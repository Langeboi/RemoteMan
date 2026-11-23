"""Paths, settings from the environment, local state and small helpers shared by the whole server."""
import base64
import contextlib
import json
import logging
import os
import re
import secrets
import socket
import string
import threading
import time

import psycopg2
import psycopg2.extras

BASE = os.environ.get("REMOTEMAN_HOME", "/var/lib/remoteman")
PGDATA = os.path.join(BASE, "pgdata")
PGCONF = os.path.join(BASE, "postgresql.conf")
PGHBA = os.path.join(BASE, "pg_hba.conf")
STATE_FILE = os.path.join(BASE, "state.json")
LOG_DIR = os.path.join(BASE, "log")
TLS_DIR = os.path.join(BASE, "tls")
BACKUP_DIR = os.path.join(BASE, "backups")
ADMIN_DB = os.path.join(BASE, "admin.sqlite")
SOCKET_DIR = "/var/run/postgresql"
SCRIPT = os.environ.get("REMOTEMAN_DB_SCRIPT", "/opt/remoteman/remoteman-db.sh")
UI_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "ui")
VERSION = os.environ.get("REMOTEMAN_SERVER_VERSION", "dev")

DB_NAME = "remoteman"
APP_USER = "remoteman"
RO_GROUP = APP_USER + "_readonly"
ADM_GROUP = APP_USER + "_admins"
REPL_GROUP = APP_USER + "_replicators"


def env_int(name, default):
    try:
        return int(os.environ.get(name, "") or default)
    except ValueError:
        return default


PG_PORT = env_int("PG_PORT", 5432)
ADMIN_PORT = env_int("ADMIN_PORT", 4433)
RELAY_PORT = env_int("RELAY_PORT", 47120)
RELAY_HOST = os.environ.get("RELAY_HOST", "127.0.0.1")

log = logging.getLogger("remoteman")


def setup_logging():
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)-5s %(message)s", datefmt="%Y-%m-%d %H:%M:%S")


def rand(n=24):
    alphabet = string.ascii_letters + string.digits
    return "".join(secrets.choice(alphabet) for _ in range(n))


def clean_name(name):
    name = (name or "").strip().lower().replace("-", "_").replace(".", "_")
    name = re.sub(r"[^a-z0-9_]", "", name)
    if name and name[0].isdigit():
        name = "s" + name
    return name[:30]


def valid_server_name(name):
    return bool(re.fullmatch(r"[a-z_][a-z0-9_]{0,29}", name or ""))


def repl_role(name):
    return "remoteman_repl_" + name


def slot_name(name):
    return "rm_" + name


def detect_address():
    """The address other machines most likely reach this one on (host networking: the host's own)."""
    if os.environ.get("ADVERTISE"):
        return os.environ["ADVERTISE"].strip()
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(("10.255.255.255", 1))
        addr = s.getsockname()[0]
        s.close()
        return addr
    except OSError:
        return socket.gethostbyname(socket.gethostname())


def default_gateway():
    try:
        with open("/proc/net/route") as f:
            for line in f.readlines()[1:]:
                p = line.split()
                if p[1] == "00000000":
                    g = int(p[2], 16)
                    return socket.inet_ntoa(g.to_bytes(4, "little"))
    except OSError:
        pass
    return ""


# ---------------------------------------------------------------- local state (not replicated)
_state_lock = threading.Lock()


def load_state():
    try:
        with open(STATE_FILE) as f:
            return json.load(f)
    except (OSError, ValueError):
        return {}


def save_state(**changes):
    with _state_lock:
        st = load_state()
        st.update(changes)
        tmp = STATE_FILE + ".tmp"
        fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        with os.fdopen(fd, "w") as f:
            json.dump(st, f, indent=2)
        os.replace(tmp, STATE_FILE)
        return st


# ---------------------------------------------------------------- join keys
def encode_key(obj):
    return "rmj_" + base64.urlsafe_b64encode(json.dumps(obj, separators=(",", ":")).encode()).decode().rstrip("=")


def decode_key(key):
    key = (key or "").strip()
    if key.startswith("rmj_"):
        key = key[4:]
    key += "=" * (-len(key) % 4)
    try:
        obj = json.loads(base64.urlsafe_b64decode(key.encode()))
    except Exception:
        raise ValueError("That join key isn't valid. Copy it again from the admin page.")
    if not isinstance(obj, dict) or not obj.get("n") or not obj.get("pw") or not obj.get("h"):
        raise ValueError("That join key isn't valid. Copy it again from the admin page.")
    return obj


# ---------------------------------------------------------------- SQL
def local_conn(db="postgres", autocommit=True):
    """Superuser connection over the local socket (the server runs as the postgres OS user)."""
    c = psycopg2.connect(host=SOCKET_DIR, port=PG_PORT, user="postgres", dbname=db,
                         application_name="remoteman-admin", connect_timeout=3)
    c.autocommit = autocommit
    return c


@contextlib.contextmanager
def local(db="postgres"):
    c = local_conn(db)
    try:
        yield c
    finally:
        c.close()


def q(sql, args=None, db="postgres", one=False, dicts=False):
    with local(db) as c:
        with c.cursor(cursor_factory=psycopg2.extras.RealDictCursor if dicts else None) as cur:
            cur.execute(sql, args)
            if cur.description is None:
                return None
            rows = cur.fetchall()
    if one:
        return rows[0] if rows else None
    return rows


def q1(sql, args=None, db="postgres"):
    r = q(sql, args, db=db, one=True)
    return r[0] if r else None


def remote_conn(host, port, user, password, db="postgres", timeout=3):
    c = psycopg2.connect(host=host, port=port, user=user, password=password, dbname=db,
                         application_name="remoteman-admin", connect_timeout=timeout)
    c.autocommit = True
    return c


def tcp_latency(host, port, timeout=1.0):
    """Milliseconds to open a TCP connection, or None."""
    t = time.perf_counter()
    try:
        with socket.create_connection((host, port), timeout=timeout):
            return (time.perf_counter() - t) * 1000
    except OSError:
        return None


def now_ms():
    return int(time.time() * 1000)
