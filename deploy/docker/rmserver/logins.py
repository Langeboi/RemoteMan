"""Login history: follows PostgreSQL's JSON log and records every sign-in, failed attempt and disconnect."""
import glob
import json
import os
import re
import threading
import time
from datetime import datetime

from . import common as C
from . import store
from .common import log

AUTH_OK = re.compile(r"^connection authorized: user=(\S+) database=(\S+)(?: application_name=(.+?))?(?: SSL enabled.*| GSS.*)?$")
DISCONNECT = re.compile(r"^disconnection: session time: (\d+):(\d+):([\d.]+) user=(\S+) database=(\S+) host=(\S+)")
FAILED = [
    (re.compile(r'password authentication failed for user "(.+?)"'), "Wrong password"),
    (re.compile(r'role "(.+?)" does not exist'), "No such user"),
    (re.compile(r'role "(.+?)" is not permitted to log in'), "Login turned off"),
    (re.compile(r'no pg_hba\.conf entry for host "[^"]+", user "(.+?)"'), "Unknown login or no access"),
    (re.compile(r'too many connections for role "(.+?)"'), "Too many connections"),
    (re.compile(r'permission denied for database'), "No access to the database"),
]


def internal(user, app):
    return (user in ("postgres", "", None) or (user or "").startswith("remoteman_repl")
            or app in ("remoteman-admin", "walreceiver", "pg_basebackup", "pg_rewind"))


class LoginTailer:
    def __init__(self):
        self.file = None
        self.pos = 0
        self.sessions = {}   # session id -> (user, app) for connections we recorded
        threading.Thread(target=self._loop, daemon=True, name="logins").start()

    def _newest(self):
        files = glob.glob(os.path.join(C.LOG_DIR, "*.json"))
        return max(files, key=os.path.getmtime) if files else None

    def _loop(self):
        saved = store.get("log_pos")
        if saved:
            try:
                f, p = saved.rsplit("|", 1)
                self.file, self.pos = f, int(p)
            except ValueError:
                pass
        last_save = 0
        while True:
            try:
                self._read()
            except Exception as e:  # noqa: BLE001
                log.debug("login tail: %s", e)
            if self.file and time.time() - last_save > 10:
                store.put("log_pos", f"{self.file}|{self.pos}")
                last_save = time.time()
            time.sleep(1)

    def _read(self):
        newest = self._newest()
        if not newest:
            return
        if newest != self.file:
            if self.file and os.path.exists(self.file):
                self._consume(self.file)     # finish the old one first
            self.file, self.pos = newest, 0
        size = os.path.getsize(self.file)
        if size < self.pos:                  # truncated on rotation
            self.pos = 0
        self._consume(self.file)

    def _consume(self, path):
        with open(path, "rb") as f:
            f.seek(self.pos)
            while True:
                line = f.readline()
                if not line or not line.endswith(b"\n"):
                    break
                self.pos = f.tell()
                try:
                    self._handle(json.loads(line))
                except ValueError:
                    continue

    def _handle(self, e):
        msg = e.get("message", "")
        ts = _ts(e.get("timestamp"))
        host = e.get("remote_host") or ""
        sess = e.get("session_id")
        if msg.startswith("connection authorized:"):
            m = AUTH_OK.match(msg)
            user = (m.group(1) if m else e.get("user")) or ""
            app = (m.group(3) if m and m.group(3) else e.get("application_name")) or ""
            if internal(user, app) or e.get("dbname") == "replication":
                return
            if e.get("dbname") not in (C.DB_NAME, None):
                return
            store.add_login(ts, user, "ok", None, host, app or "", sess)
            self.sessions[sess] = (user, app)
            if len(self.sessions) > 5000:
                self.sessions.clear()
        elif msg.startswith("disconnection:"):
            m = DISCONNECT.match(msg)
            if m and sess in self.sessions:
                dur = int(m.group(1)) * 3600 + int(m.group(2)) * 60 + float(m.group(3))
                store.end_login(sess, ts, dur)
                self.sessions.pop(sess, None)
        elif e.get("error_severity") == "FATAL":
            for rx, reason in FAILED:
                mm = rx.search(msg)
                if mm:
                    user = mm.group(1) if mm.groups() else (e.get("user") or "")
                    if user.startswith("remoteman_repl") or user == "postgres":
                        return
                    store.add_login(ts, user, "failed", reason, host, e.get("application_name") or "", sess)
                    return
        elif msg.startswith("application_name") and sess in self.sessions:
            pass


def _ts(s):
    if not s:
        return time.time()
    try:
        # "2026-11-22 13:47:01.123 UTC"
        base, _, tz = s.rpartition(" ")
        dt = datetime.strptime(base, "%Y-%m-%d %H:%M:%S.%f")
        if tz == "UTC":
            from datetime import timezone
            return dt.replace(tzinfo=timezone.utc).timestamp()
        return dt.timestamp()
    except ValueError:
        return time.time()
