"""This server's own small database (SQLite in the volume): sign-ins it saw, and things that happened to it."""
import sqlite3
import threading
import time

from . import common as C

_lock = threading.Lock()
KEEP_DAYS = C.env_int("LOGIN_HISTORY_DAYS", 90)


def _db():
    c = sqlite3.connect(C.ADMIN_DB, timeout=10)
    c.row_factory = sqlite3.Row
    return c


def init():
    with _lock, _db() as c:
        c.executescript("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS logins (id INTEGER PRIMARY KEY, ts REAL NOT NULL, user TEXT, result TEXT NOT NULL,
                reason TEXT, host TEXT, app TEXT, session TEXT, ended REAL, duration REAL);
            CREATE INDEX IF NOT EXISTS ix_logins_ts ON logins (ts);
            CREATE INDEX IF NOT EXISTS ix_logins_session ON logins (session);
            CREATE TABLE IF NOT EXISTS events (id INTEGER PRIMARY KEY, ts REAL NOT NULL, kind TEXT NOT NULL, text TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS kv (k TEXT PRIMARY KEY, v TEXT);
        """)


def add_event(kind, text):
    from .common import log
    log.info("%s", text)
    with _lock, _db() as c:
        c.execute("INSERT INTO events (ts, kind, text) VALUES (?, ?, ?)", (time.time(), kind, text))


def events(limit=50):
    with _db() as c:
        return [dict(r) for r in c.execute("SELECT * FROM events ORDER BY id DESC LIMIT ?", (limit,))]


def add_login(ts, user, result, reason, host, app, session):
    with _lock, _db() as c:
        c.execute("INSERT INTO logins (ts, user, result, reason, host, app, session) VALUES (?, ?, ?, ?, ?, ?, ?)",
                  (ts, user, result, reason, host, app, session))


def end_login(session, ended, duration):
    with _lock, _db() as c:
        c.execute("UPDATE logins SET ended = ?, duration = ? WHERE session = ? AND ended IS NULL", (ended, duration, session))


def logins(since):
    with _db() as c:
        return [dict(r) for r in c.execute("SELECT * FROM logins WHERE ts >= ? ORDER BY ts DESC LIMIT 20000", (since,))]


def close_open_logins():
    """After a restart nobody is connected any more; sessions still marked open get an end time."""
    with _lock, _db() as c:
        c.execute("UPDATE logins SET ended = ts, duration = NULL WHERE ended IS NULL")


def get(k, default=None):
    with _db() as c:
        r = c.execute("SELECT v FROM kv WHERE k = ?", (k,)).fetchone()
        return r[0] if r else default


def put(k, v):
    with _lock, _db() as c:
        c.execute("INSERT INTO kv (k, v) VALUES (?, ?) ON CONFLICT (k) DO UPDATE SET v = excluded.v", (k, str(v)))


def prune():
    cutoff = time.time() - KEEP_DAYS * 86400
    with _lock, _db() as c:
        c.execute("DELETE FROM logins WHERE ts < ?", (cutoff,))
        c.execute("DELETE FROM events WHERE ts < ?", (cutoff,))
