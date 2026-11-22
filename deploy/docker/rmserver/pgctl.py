"""Running PostgreSQL inside the container: settings, start/stop, promote, base backup and rewind."""
import os
import re
import shutil
import signal
import subprocess
import threading
import time

from . import common as C
from .common import log

BIN = os.environ.get("PG_BIN", "")
if not BIN:
    for v in ("17", "16", "18", "15"):
        if os.path.isdir(f"/usr/lib/postgresql/{v}/bin"):
            BIN = f"/usr/lib/postgresql/{v}/bin"
            break


def tool(name):
    return os.path.join(BIN, name) if BIN else name


class Postgres:
    """Owns the postgres process. Restarts it after a crash unless a managed stop is in progress."""

    def __init__(self):
        self.proc = None
        self.lock = threading.RLock()
        self.wanted = False
        self.busy = ""          # what is being done right now ("rewinding", "promoting" ...), for the page
        self.started_at = None
        threading.Thread(target=self._keepalive, daemon=True, name="pg-keepalive").start()

    # ------------------------------------------------------------ settings
    def write_settings(self, upstream=None, name=None, repl_password=None):
        """Settings live outside the data directory, so a base backup or rewind never brings in another server's."""
        lines = [
            "# Written by the RemoteMan server container on every start. Put your own settings in",
            "# /var/lib/remoteman/postgresql.custom.conf (read last).",
            "listen_addresses = '*'",
            f"port = {C.PG_PORT}",
            f"hba_file = '{C.PGHBA}'",
            f"unix_socket_directories = '{C.SOCKET_DIR}'",
            "max_connections = 200",
            "wal_level = replica",
            "max_wal_senders = 16",
            "max_replication_slots = 16",
            "wal_keep_size = '512MB'",
            "max_slot_wal_keep_size = '4GB'",
            "hot_standby = on",
            "hot_standby_feedback = on",
            "wal_log_hints = on",
            "password_encryption = 'scram-sha-256'",
            "logging_collector = on",
            "log_destination = 'jsonlog'",
            f"log_directory = '{C.LOG_DIR}'",
            "log_filename = 'postgresql-%a.log'",
            "log_truncate_on_rotation = on",
            "log_rotation_age = '1d'",
            "log_rotation_size = 0",
            "log_connections = on",
            "log_disconnections = on",
            "log_min_messages = warning",
            "log_timezone = 'UTC'",
            "track_io_timing = on",
            "recovery_target_timeline = 'latest'",
        ]
        if upstream and name and repl_password:
            host, port = upstream
            conninfo = (f"host={host} port={port} user={C.repl_role(name)} password={repl_password} "
                        f"application_name={name} dbname=postgres")
            lines.append("primary_conninfo = '" + conninfo.replace("'", "''") + "'")
            lines.append(f"primary_slot_name = '{C.slot_name(name)}'")
        custom = os.path.join(C.BASE, "postgresql.custom.conf")
        if not os.path.exists(custom):
            with open(custom, "w") as f:
                f.write("# Your own PostgreSQL settings (for example shared_buffers = '1GB'). Restart the container after a change.\n")
        lines.append(f"include_if_exists = '{custom}'")
        fd = os.open(C.PGCONF, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        with os.fdopen(fd, "w") as f:
            f.write("\n".join(lines) + "\n")
        hba = [
            "# Written by the RemoteMan server container on every start.",
            "local  all          postgres                 peer",
            f"host   replication  +{C.REPL_GROUP}  all  scram-sha-256",
            f"host   postgres     +{C.REPL_GROUP}  all  scram-sha-256",
            f"host   {C.DB_NAME}  +{C.APP_USER}   all  scram-sha-256",
            f"host   {C.DB_NAME}  +{C.RO_GROUP}   all  scram-sha-256",
        ]
        with open(C.PGHBA, "w") as f:
            f.write("\n".join(hba) + "\n")
        os.makedirs(C.LOG_DIR, exist_ok=True)
        self.clean_auto_conf()

    def upstream_in_settings(self):
        try:
            with open(C.PGCONF) as f:
                m = re.search(r"primary_conninfo = '.*?host=(\S+) port=(\d+)", f.read())
                return (m.group(1), int(m.group(2))) if m else None
        except OSError:
            return None

    def clean_auto_conf(self):
        """A base backup or rewind copies the other server's postgresql.auto.conf; its replication settings don't belong here."""
        f = os.path.join(C.PGDATA, "postgresql.auto.conf")
        if os.path.exists(f):
            with open(f) as fh:
                keep = [l for l in fh if not re.match(r"\s*(primary_conninfo|primary_slot_name)\b", l)]
            with open(f, "w") as fh:
                fh.writelines(keep)

    # ------------------------------------------------------------ state
    def initialized(self):
        return os.path.exists(os.path.join(C.PGDATA, "PG_VERSION"))

    def is_standby_on_disk(self):
        return os.path.exists(os.path.join(C.PGDATA, "standby.signal"))

    def running(self):
        return self.proc is not None and self.proc.poll() is None

    def ready(self):
        if not self.running():
            return False
        r = subprocess.run([tool("pg_isready"), "-q", "-h", C.SOCKET_DIR, "-p", str(C.PG_PORT)])
        return r.returncode == 0

    def controldata(self):
        r = subprocess.run([tool("pg_controldata"), C.PGDATA], capture_output=True, text=True)
        out = {}
        for line in r.stdout.splitlines():
            if ":" in line:
                k, v = line.split(":", 1)
                out[k.strip()] = v.strip()
        return out

    def disk_timeline(self):
        try:
            return int(self.controldata().get("Latest checkpoint's TimeLineID", "0"))
        except ValueError:
            return 0

    # ------------------------------------------------------------ process
    def initdb(self):
        os.makedirs(C.PGDATA, mode=0o700, exist_ok=True)
        subprocess.run([tool("initdb"), "-D", C.PGDATA, "-U", "postgres", "--data-checksums", "-E", "UTF8",
                        "--locale=C.UTF-8", "--auth-local=peer", "--auth-host=scram-sha-256"],
                       check=True, capture_output=True)

    def start(self, wait=60):
        with self.lock:
            self.wanted = True
            if self.running():
                return
            log.info("Starting PostgreSQL")
            self.proc = subprocess.Popen([tool("postgres"), "-D", C.PGDATA, "-c", f"config_file={C.PGCONF}"],
                                         stdout=subprocess.DEVNULL, stderr=None)
            self.started_at = time.time()
        t = time.time()
        while time.time() - t < wait:
            if self.ready():
                return
            if not self.running():
                raise RuntimeError("PostgreSQL stopped right after starting. See the log above.")
            time.sleep(0.3)
        raise RuntimeError("PostgreSQL didn't become ready in time.")

    def stop(self, mode="fast", wait=120):
        with self.lock:
            self.wanted = False
            if not self.running():
                return
            log.info("Stopping PostgreSQL (%s)", mode)
            self.proc.send_signal({"smart": signal.SIGTERM, "fast": signal.SIGINT, "immediate": signal.SIGQUIT}[mode])
            try:
                self.proc.wait(wait)
            except subprocess.TimeoutExpired:
                self.proc.send_signal(signal.SIGQUIT)
                self.proc.wait(30)

    def reload(self):
        if self.running():
            self.proc.send_signal(signal.SIGHUP)

    def _keepalive(self):
        while True:
            time.sleep(2)
            with self.lock:
                if self.wanted and self.proc is not None and self.proc.poll() is not None:
                    log.warning("PostgreSQL exited (code %s); starting it again", self.proc.returncode)
                    self.proc = None
                    try:
                        self.start()
                    except Exception as e:  # noqa: BLE001
                        log.error("Restart failed: %s", e)

    # ------------------------------------------------------------ replication
    def basebackup(self, host, port, name, password):
        self.busy = "copying the database"
        try:
            if os.path.isdir(C.PGDATA):
                shutil.rmtree(C.PGDATA)
            os.makedirs(C.PGDATA, mode=0o700)
            env = dict(os.environ, PGPASSWORD=password, PGAPPNAME=name)
            log.info("Copying the database from %s:%s", host, port)
            subprocess.run([tool("pg_basebackup"), "-h", host, "-p", str(port), "-U", C.repl_role(name), "-D", C.PGDATA,
                            "-X", "stream", "-S", C.slot_name(name), "-c", "fast", "-w"], check=True, env=env,
                           capture_output=True, text=True)
            self.after_copy()
        except subprocess.CalledProcessError as e:
            lines = (e.stderr or "").strip().splitlines()
            raise RuntimeError("Copying the database failed: " + (lines[-1] if lines else str(e)))
        finally:
            self.busy = ""

    def after_copy(self):
        shutil.rmtree(os.path.join(C.PGDATA, "pg_replslot"), ignore_errors=True)
        os.makedirs(os.path.join(C.PGDATA, "pg_replslot"), mode=0o700, exist_ok=True)
        for f in ("postmaster.pid", "postmaster.opts"):
            try:
                os.remove(os.path.join(C.PGDATA, f))
            except OSError:
                pass
        self.clean_auto_conf()
        open(os.path.join(C.PGDATA, "standby.signal"), "w").close()

    def rewind(self, host, port, name, password):
        """Make this (stopped) server a copy of the given primary again. Falls back to a full copy."""
        self.busy = "rewinding"
        try:
            src = f"host={host} port={port} user={C.repl_role(name)} password={password} dbname=postgres"
            r = subprocess.run([tool("pg_rewind"), "-D", C.PGDATA, "--source-server=" + src],
                               capture_output=True, text=True)
            if r.returncode == 0:
                log.info("Rewound from %s:%s", host, port)
                self.after_copy()
                return "rewound"
            log.warning("Rewind wasn't possible (%s); copying the whole database instead",
                        (r.stderr.strip().splitlines() or ["?"])[-1])
        finally:
            self.busy = ""
        self.basebackup(host, port, name, password)
        return "copied"
