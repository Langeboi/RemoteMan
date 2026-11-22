"""Samples this server once a second: the machine (CPU, memory, disk, network, IOPS) and PostgreSQL."""
import collections
import json
import os
import re
import socket
import ssl
import threading
import time

from . import common as C
from .common import log

DISK_RE = re.compile(r"^(sd[a-z]+|vd[a-z]+|xvd[a-z]+|hd[a-z]+|nvme\d+n\d+|mmcblk\d+)$")

# Keys kept in the history, in this order.
SERIES = ["cpu", "mem", "disk", "rx", "tx", "iops", "conns", "tps", "reads", "writes", "cache", "locks", "wal", "lag", "lagb"]


def _read(path):
    try:
        with open(path) as f:
            return f.read()
    except OSError:
        return ""


class Host:
    def __init__(self):
        self.prev = None

    def raw(self):
        cpu = [int(x) for x in _read("/proc/stat").splitlines()[0].split()[1:]]
        idle = cpu[3] + (cpu[4] if len(cpu) > 4 else 0)
        rx = tx = 0
        for line in _read("/proc/net/dev").splitlines()[2:]:
            name, data = line.split(":", 1)
            if name.strip() == "lo":
                continue
            f = data.split()
            rx += int(f[0])
            tx += int(f[8])
        ios = 0
        for line in _read("/proc/diskstats").splitlines():
            f = line.split()
            if len(f) > 7 and DISK_RE.match(f[2]):
                ios += int(f[3]) + int(f[7])
        return {"t": time.time(), "cpu_total": sum(cpu), "cpu_idle": idle, "rx": rx, "tx": tx, "ios": ios}

    def sample(self):
        r = self.raw()
        out = {}
        if self.prev:
            p = self.prev
            dt = max(r["t"] - p["t"], 0.001)
            tot = r["cpu_total"] - p["cpu_total"]
            out["cpu"] = round(100.0 * (1 - (r["cpu_idle"] - p["cpu_idle"]) / tot), 1) if tot > 0 else 0.0
            out["rx"] = max(0, (r["rx"] - p["rx"]) / dt)
            out["tx"] = max(0, (r["tx"] - p["tx"]) / dt)
            out["iops"] = max(0, (r["ios"] - p["ios"]) / dt)
        self.prev = r
        mem = {}
        for line in _read("/proc/meminfo").splitlines():
            k, v = line.split(":", 1)
            mem[k] = int(v.split()[0]) * 1024
        # Inside a container with a memory limit, the cgroup is the real ceiling.
        limit = _read("/sys/fs/cgroup/memory.max").strip()
        used_cg = _read("/sys/fs/cgroup/memory.current").strip()
        if limit.isdigit() and used_cg.isdigit() and int(limit) < mem.get("MemTotal", 1 << 62):
            out["mem_total"], out["mem_used"] = int(limit), int(used_cg)
        else:
            out["mem_total"] = mem.get("MemTotal", 0)
            out["mem_used"] = mem.get("MemTotal", 0) - mem.get("MemAvailable", 0)
        st = os.statvfs(C.BASE)
        out["disk_total"] = st.f_blocks * st.f_frsize
        out["disk_used"] = (st.f_blocks - st.f_bfree) * st.f_frsize
        out["cores"] = os.cpu_count() or 1
        try:
            out["host_up"] = float(_read("/proc/uptime").split()[0])
        except (IndexError, ValueError):
            pass
        return out


class Sampler:
    """Keeps the latest snapshot of this server plus 2 minutes of 1 s samples and 24 hours of 1 min averages."""

    def __init__(self, cluster):
        self.cluster = cluster
        self.host = Host()
        self.prev_db = None
        self.snapshot = {}
        self.sec = collections.deque(maxlen=120)
        self.minute = collections.deque(maxlen=1440)
        self._acc = []
        self._slow_at = 0
        self.slow = {}
        threading.Thread(target=self._loop, daemon=True, name="sampler").start()

    def _loop(self):
        while True:
            t = time.time()
            try:
                self._sample()
            except Exception as e:  # noqa: BLE001
                log.debug("sample failed: %s", e)
            time.sleep(max(0.05, 1 - (time.time() - t)))

    # ------------------------------------------------------------ one sample
    def _sample(self):
        cl = self.cluster
        pg = cl.pg
        m = self.host.sample()
        snap = {
            "name": cl.name, "address": cl.address, "pg_port": C.PG_PORT, "admin_port": C.ADMIN_PORT,
            "version": C.VERSION, "time": time.time(), "status": cl.status(), "role": None,
            "started_at": pg.started_at,
        }
        if pg.ready():
            try:
                self._pg(snap, m)
            except Exception as e:  # noqa: BLE001
                snap["pg_error"] = str(e).strip()
        if time.time() - self._slow_at > 15:
            self._slow_at = time.time()
            try:
                self._slow_checks(snap)
            except Exception as e:  # noqa: BLE001
                log.debug("slow checks failed: %s", e)
        snap.update(self.slow)
        snap["m"] = m
        self.snapshot = snap
        point = {"t": round(snap["time"], 1)}
        for k in SERIES:
            if k == "mem":
                v = m.get("mem_used", 0) / max(m.get("mem_total", 1), 1) * 100
            elif k == "disk":
                v = m.get("disk_used", 0) / max(m.get("disk_total", 1), 1) * 100
            else:
                v = m.get(k)
            point[k] = None if v is None else round(v, 3)
        self.sec.append(point)
        self._acc.append(point)
        if len(self._acc) >= 60:
            avg = {"t": self._acc[-1]["t"]}
            for k in SERIES:
                vals = [p[k] for p in self._acc if p.get(k) is not None]
                avg[k] = round(sum(vals) / len(vals), 3) if vals else None
                if k in ("lag", "locks") and vals:
                    avg[k] = max(vals)
            self.minute.append(avg)
            self._acc = []

    def _pg(self, snap, m):
        with C.local() as c:
            cur = c.cursor()
            cur.execute("""SELECT pg_is_in_recovery(), (SELECT timeline_id FROM pg_control_checkpoint()),
                current_setting('server_version'), extract(epoch FROM pg_postmaster_start_time()),
                current_setting('max_connections')::int""")
            rec, tl, ver, start, maxc = cur.fetchone()
            snap.update(role="standby" if rec else "primary", timeline=tl, pg_version=ver, pg_started=float(start))
            m["max_conns"] = maxc
            if rec:
                # The control file lags behind a timeline switch on a standby; the WAL receiver knows better.
                cur.execute("""SELECT pg_wal_lsn_diff(pg_last_wal_receive_lsn(), '0/0'), pg_wal_lsn_diff(pg_last_wal_replay_lsn(), '0/0'),
                    extract(epoch FROM now() - pg_last_xact_replay_timestamp()),
                    (SELECT status FROM pg_stat_wal_receiver), (SELECT received_tli FROM pg_stat_wal_receiver),
                    (SELECT sender_host || ':' || sender_port FROM pg_stat_wal_receiver)""")
                rl, pl, age, status, rtli, sender = cur.fetchone()
                snap.update(lsn=float(rl or pl or 0), replay_lsn=float(pl or 0), replay_age=age,
                            receiver=status or "stopped", upstream=sender)
                if rtli and rtli > (tl or 0):
                    snap["timeline"] = rtli
                wal_pos = float(rl or pl or 0)
            else:
                cur.execute("SELECT pg_wal_lsn_diff(pg_current_wal_lsn(), '0/0')")
                wal_pos = float(cur.fetchone()[0])
                snap["lsn"] = wal_pos
                cur.execute("""SELECT application_name, host(client_addr), state, sync_state,
                    COALESCE(extract(epoch FROM replay_lag), 0), COALESCE(pg_wal_lsn_diff(pg_current_wal_lsn(), replay_lsn), 0),
                    extract(epoch FROM backend_start)
                    FROM pg_stat_replication ORDER BY application_name""")
                snap["repl"] = [{"name": r[0], "addr": r[1], "state": r[2], "sync": r[3], "lag": float(r[4]),
                                 "lagb": float(r[5]), "since": float(r[6])} for r in cur.fetchall()]
                if snap["repl"]:
                    m["lag"] = max(r["lag"] for r in snap["repl"])
                    m["lagb"] = max(r["lagb"] for r in snap["repl"])
                cur.execute(f"""SELECT usename, host(client_addr), extract(epoch FROM min(backend_start)), count(*),
                        max(application_name)
                    FROM pg_stat_activity WHERE datname = '{C.DB_NAME}' AND backend_type = 'client backend'
                      AND application_name <> 'remoteman-admin' AND pid <> pg_backend_pid()
                    GROUP BY usename, host(client_addr) ORDER BY 3""")
                snap["clients"] = [{"user": r[0], "addr": r[1], "since": float(r[2]), "count": r[3], "app": r[4]}
                                   for r in cur.fetchall()]
            cur.execute("""SELECT sum(xact_commit + xact_rollback), sum(tup_returned + tup_fetched),
                    sum(tup_inserted + tup_updated + tup_deleted), sum(blks_hit), sum(blks_read)
                FROM pg_stat_database""")
            xact, reads, writes, hit, rd = [float(x or 0) for x in cur.fetchone()]
            cur.execute("""SELECT count(*) FILTER (WHERE backend_type = 'client backend'),
                    COALESCE(max(extract(epoch FROM now() - xact_start)) FILTER (WHERE state <> 'idle' AND backend_type = 'client backend'), 0),
                    (SELECT count(*) FROM pg_locks WHERE NOT granted)
                FROM pg_stat_activity""")
            conns, longest, locks = cur.fetchone()
            m.update(conns=int(conns), longest_xact=float(longest), locks=int(locks))
            now = time.time()
            cur_db = {"t": now, "xact": xact, "reads": reads, "writes": writes, "hit": hit, "rd": rd, "wal": wal_pos}
            if self.prev_db:
                p = self.prev_db
                dt = max(now - p["t"], 0.001)
                m["tps"] = max(0, (xact - p["xact"]) / dt)
                m["reads"] = max(0, (reads - p["reads"]) / dt)
                m["writes"] = max(0, (writes - p["writes"]) / dt)
                dh, dr = hit - p["hit"], rd - p["rd"]
                m["cache"] = 100.0 * dh / (dh + dr) if dh + dr > 0 else (self.snapshot.get("m", {}).get("cache") or 100.0)
                m["wal"] = max(0, (wal_pos - p["wal"]) / dt)
            self.prev_db = cur_db

    def _slow_checks(self, snap):
        """Things that change slowly: sizes, vacuum, certificate, backup, relay."""
        out = {}
        if self.cluster.pg.ready():
            with C.local(C.DB_NAME) as c:
                cur = c.cursor()
                cur.execute("SELECT pg_database_size(current_database())")
                out["dbsize"] = cur.fetchone()[0]
                cur.execute("""SELECT relname, pg_total_relation_size(relid), n_live_tup
                    FROM pg_stat_user_tables WHERE schemaname = 'public' ORDER BY 2 DESC""")
                out["tables"] = [{"name": r[0], "bytes": r[1], "rows": r[2]} for r in cur.fetchall()]
                cur.execute("""SELECT extract(epoch FROM now() - max(GREATEST(last_vacuum, last_autovacuum)))
                    FROM pg_stat_user_tables""")
                out["vacuum_age"] = cur.fetchone()[0]
            hist = json.loads(self._size_hist_raw() or "[]")
            day = time.strftime("%Y-%m-%d")
            if not hist or hist[-1][0] != day:
                hist.append([day, out["dbsize"]])
            else:
                hist[-1][1] = out["dbsize"]
            hist = hist[-60:]
            from . import store
            store.put("size_hist", json.dumps(hist))
            out["size_hist"] = hist
        out["cert_days"] = self._cert_days()
        out["relay"] = relay_stats()
        out["backup"] = self.cluster.backups.last()
        self.slow = out

    def _size_hist_raw(self):
        from . import store
        return store.get("size_hist")

    def _cert_days(self):
        try:
            info = ssl._ssl._test_decode_cert(self.cluster.cert_file)  # noqa: SLF001
            exp = ssl.cert_time_to_seconds(info["notAfter"])
            return round((exp - time.time()) / 86400, 1)
        except Exception:  # noqa: BLE001
            return None

    def history(self, rng):
        if rng == "2m":
            return list(self.sec)
        if rng == "1h":
            return list(self.minute)[-60:]
        pts = list(self.minute)
        # 24 h: 5 minute buckets.
        out = []
        for i in range(0, len(pts), 5):
            chunk = pts[i:i + 5]
            avg = {"t": chunk[-1]["t"]}
            for k in SERIES:
                vals = [p[k] for p in chunk if p.get(k) is not None]
                avg[k] = (max(vals) if k in ("lag", "locks") else sum(vals) / len(vals)) if vals else None
            out.append(avg)
        return out


def relay_stats():
    """Asks the relay next door (if any) for its counters. Only answers on loopback."""
    try:
        with socket.create_connection((C.RELAY_HOST, C.RELAY_PORT), timeout=0.5) as s:
            s.sendall(b"STATS\n")
            s.settimeout(1)
            data = b""
            while not data.endswith(b"\n") and len(data) < 65536:
                chunk = s.recv(4096)
                if not chunk:
                    break
                data += chunk
        st = json.loads(data.decode())
        st["up"] = True
        return st
    except Exception:  # noqa: BLE001
        return {"up": False}
