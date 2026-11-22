#!/usr/bin/env bash
# RemoteMan database pair: two PostgreSQL servers on Debian/Ubuntu, one primary and one hot standby
# that keeps a live copy over streaming replication. If the primary dies, the standby takes over
# (automatically with --auto-failover, or with "remoteman-db promote"). When the old primary comes
# back it notices it is out of date, rewinds itself and becomes the new standby.
#
#   Server 1:  sudo bash remoteman-db.sh first --peer <IP of server 2>
#              (prints the exact command for server 2, with a one-time join key)
#   Server 2:  sudo bash remoteman-db.sh second --primary <IP of server 1> --join <key> [--auto-failover]
#
# Afterwards, on either server:
#   remoteman-db status      who is primary, replication lag
#   remoteman-db promote     make this standby the primary (manual failover)
#   remoteman-db rejoin      turn this server back into a standby of the other one
#   remoteman-db conn        the connection string to paste into RemoteMan
#   remoteman-db user ...    personal logins: add, list, passwd, remove (run on the primary)
#   remoteman-db relay ...   shared-session relay for teammates on other networks: install, status, remove
#
# RemoteMan gets one connection string with both hosts and "Target Session Attributes=primary",
# so it always talks to whichever server is the primary at the moment.

set -euo pipefail

# ---------------------------------------------------------------- settings
PG_VERSION="${PG_VERSION:-17}"
CLUSTER="${CLUSTER:-main}"
PORT="${PORT:-5432}"
CONF="${REMOTEMAN_DB_CONF:-/etc/remoteman-db.conf}"
HELPER=/usr/local/sbin/remoteman-db
SLOT=remoteman_standby
REPL_USER=remoteman_repl
FAILOVER_SECONDS="${FAILOVER_SECONDS:-30}"

say()  { printf '\033[1;36m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m!!\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31mxx\033[0m %s\n' "$*" >&2; exit 1; }

need_root() { [[ $EUID -eq 0 ]] || die "Run as root (sudo bash $0 $*)."; }

load_conf() {
    [[ -f "$CONF" ]] || die "$CONF not found. Set this server up first with 'first' or 'second'."
    # shellcheck disable=SC1090
    source "$CONF"
}

data_dir()   { echo "/var/lib/postgresql/$PG_VERSION/$CLUSTER"; }
etc_dir()    { echo "/etc/postgresql/$PG_VERSION/$CLUSTER"; }
pg()         { runuser -u postgres -- env -u PGPASSWORD HOME=/var/lib/postgresql PGPASSFILE=/var/lib/postgresql/.pgpass "$@"; }
local_sql()  { pg psql -X -At -p "$PORT" -d postgres -c "$1"; }
peer_sql()   { pg psql -X -At "host=$PEER_HOST port=$PEER_PORT user=$REPL_USER dbname=postgres connect_timeout=3" -c "$1"; }
cluster()    { pg_ctlcluster "$PG_VERSION" "$CLUSTER" "$@"; }
is_running() { pg_lsclusters -h 2>/dev/null | awk -v v="$PG_VERSION" -v c="$CLUSTER" '$1==v && $2==c {print $4}' | grep -q online; }
in_recovery(){ [[ "$(local_sql 'SELECT pg_is_in_recovery()')" == t ]]; }
peer_role()  {  # primary | standby | down
    local r; r="$(peer_sql 'SELECT pg_is_in_recovery()' 2>/dev/null)" || { echo down; return; }
    [[ "$r" == f ]] && echo primary || echo standby
}
timeline_local() { local_sql 'SELECT timeline_id FROM pg_control_checkpoint()'; }
timeline_peer()  { peer_sql 'SELECT timeline_id FROM pg_control_checkpoint()'; }
rand() { local s; s="$(head -c 600 /dev/urandom | tr -dc 'A-Za-z0-9')"; echo "${s:0:${1:-32}}"; }

# ---------------------------------------------------------------- install
install_postgres() {
    export DEBIAN_FRONTEND=noninteractive
    if dpkg -s "postgresql-$PG_VERSION" >/dev/null 2>&1; then say "PostgreSQL $PG_VERSION is already installed"; return; fi
    say "Installing PostgreSQL $PG_VERSION"
    apt-get update -qq
    apt-get install -y -qq postgresql-common ca-certificates curl >/dev/null
    # The PostgreSQL project's own repository has every version for every Debian/Ubuntu release,
    # so both servers get exactly the same version whatever release they run.
    if [[ -z "${NO_PGDG:-}" ]] && /usr/share/postgresql-common/pgdg/apt.postgresql.org.sh -y >/dev/null 2>&1 \
        && apt-get install -y -qq "postgresql-$PG_VERSION" >/dev/null 2>&1; then
        return
    fi
    warn "apt.postgresql.org can't be reached; trying the release's own PostgreSQL $PG_VERSION"
    apt-get install -y -qq "postgresql-$PG_VERSION" >/dev/null || die "postgresql-$PG_VERSION isn't available here. Run with PG_VERSION=<a version your release has> on both servers."
}

ensure_cluster() {
    if ! pg_lsclusters -h | awk '{print $1" "$2}' | grep -qx "$PG_VERSION $CLUSTER"; then
        pg_createcluster "$PG_VERSION" "$CLUSTER" --port "$PORT" >/dev/null
    fi
}

# Settings shared by both servers, in Debian's conf.d (outside the data directory, so a rewind or a
# base backup never overwrites them).
write_settings() {
    RO_GROUP="${APP_USER}_readonly"
    mkdir -p "$(etc_dir)/conf.d"
    cat > "$(etc_dir)/conf.d/remoteman.conf" <<EOF
# Written by remoteman-db.sh
listen_addresses = '*'
port = $PORT
wal_level = replica
max_wal_senders = 10
max_replication_slots = 10
wal_keep_size = '512MB'
max_slot_wal_keep_size = '4GB'   # a standby that is gone for days can't fill the disk
hot_standby = on
wal_log_hints = on               # lets the old primary rewind and rejoin after a failover
password_encryption = 'scram-sha-256'
primary_conninfo = 'host=$PEER_HOST port=$PEER_PORT user=$REPL_USER application_name=$(hostname -s) passfile=/var/lib/postgresql/.pgpass'
primary_slot_name = '$SLOT'
EOF
    grep -q "include_dir = 'conf.d'" "$(etc_dir)/postgresql.conf" || echo "include_dir = 'conf.d'" >> "$(etc_dir)/postgresql.conf"

    local hba; hba="$(etc_dir)/pg_hba.conf"
    sed -i '/# remoteman-db begin/,/# remoteman-db end/d' "$hba"
    cat >> "$hba" <<EOF
# remoteman-db begin
host  replication  $REPL_USER  $PEER_HOST/32  scram-sha-256
host  postgres     $REPL_USER  $PEER_HOST/32  scram-sha-256
host  $DB_NAME     +$APP_USER   0.0.0.0/0     scram-sha-256
host  $DB_NAME     +$APP_USER   ::/0          scram-sha-256
host  $DB_NAME     +$RO_GROUP   0.0.0.0/0     scram-sha-256
host  $DB_NAME     +$RO_GROUP   ::/0          scram-sha-256
# remoteman-db end
EOF
    # The peer's replication password, for streaming, status checks and rewinds.
    local pgpass=/var/lib/postgresql/.pgpass
    touch "$pgpass"
    sed -i "/^$PEER_HOST:$PEER_PORT:/d" "$pgpass"
    echo "$PEER_HOST:$PEER_PORT:*:$REPL_USER:$REPL_PASSWORD" >> "$pgpass"
    chown postgres:postgres "$pgpass"; chmod 600 "$pgpass"
}

# A base backup or rewind copies postgresql.auto.conf from the other server; its replication
# settings would point this server at itself, so they go (conf.d has the right ones).
clean_auto_conf() {
    local f; f="$(data_dir)/postgresql.auto.conf"
    [[ -f "$f" ]] && sed -i '/^primary_conninfo/d;/^primary_slot_name/d' "$f" || true
}

open_firewall() {
    if command -v ufw >/dev/null && ufw status 2>/dev/null | grep -q "Status: active"; then
        say "Opening port $PORT in ufw"; ufw allow "$PORT"/tcp >/dev/null
    fi
}

write_conf() {
    umask 077
    cat > "$CONF" <<EOF
# remoteman-db settings (root only: holds the replication password)
PG_VERSION=$PG_VERSION
CLUSTER=$CLUSTER
PORT=$PORT
PEER_HOST=$PEER_HOST
PEER_PORT=$PEER_PORT
DB_NAME=$DB_NAME
APP_USER=$APP_USER
REPL_PASSWORD=$REPL_PASSWORD
AUTO_FAILOVER=${AUTO_FAILOVER:-no}
WITNESS=${WITNESS:-}
FAILOVER_SECONDS=$FAILOVER_SECONDS
EOF
}

install_helper() {
    local self; self="$(readlink -f "$0")"
    [[ "$self" == "$HELPER" ]] || install -m 755 "$self" "$HELPER"
    # Before PostgreSQL starts: if the other server took over while this one was down, rewind and
    # come back as its standby instead of as a second primary.
    if [[ -d /run/systemd/system ]]; then
        mkdir -p "/etc/systemd/system/postgresql@$PG_VERSION-$CLUSTER.service.d"
        cat > "/etc/systemd/system/postgresql@$PG_VERSION-$CLUSTER.service.d/remoteman-db.conf" <<EOF
[Service]
ExecStartPre=-$HELPER pre-start
EOF
        cat > /etc/systemd/system/remoteman-db-watch.service <<EOF
[Unit]
Description=RemoteMan database pair watcher (failover and rejoin)
After=network-online.target postgresql.service
Wants=network-online.target

[Service]
ExecStart=$HELPER watch
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF
        systemctl daemon-reload
        systemctl enable --now remoteman-db-watch.service >/dev/null 2>&1 || warn "Couldn't start the watcher service"
    else
        warn "No systemd here: start '$HELPER watch' yourself to get automatic failover and rejoin."
    fi
}

# ---------------------------------------------------------------- first server
cmd_first() {
    need_root first
    PEER_HOST=""; DB_NAME=remoteman; APP_USER=remoteman; APP_PASSWORD=""
    while [[ $# -gt 0 ]]; do
        case "$1" in
            --peer) PEER_HOST="$2"; shift 2 ;;
            --db) DB_NAME="$2"; shift 2 ;;
            --user) APP_USER="$2"; shift 2 ;;
            --password) APP_PASSWORD="$2"; shift 2 ;;
            *) die "Unknown option $1" ;;
        esac
    done
    if [[ -z "$PEER_HOST" ]]; then read -rp "IP address of the second server: " PEER_HOST; fi
    [[ -n "$PEER_HOST" ]] || die "The second server's IP address is needed (--peer)."
    [[ "$DB_NAME$APP_USER" =~ ^[a-z_][a-z0-9_]*$ ]] || die "Database and user names: lowercase letters, digits and _ only."
    PEER_PORT="${PEER_PORT:-$PORT}"
    APP_PASSWORD="${APP_PASSWORD:-$(rand 24)}"
    REPL_PASSWORD="$(rand 32)"

    install_postgres
    ensure_cluster
    write_settings
    write_conf
    open_firewall
    say "Starting PostgreSQL"
    cluster restart 2>/dev/null || cluster start

    say "Creating database $DB_NAME, login $APP_USER and the replication login"
    local_sql "DO \$\$ BEGIN
        IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '$APP_USER') THEN CREATE ROLE $APP_USER LOGIN; END IF;
        IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '$REPL_USER') THEN CREATE ROLE $REPL_USER LOGIN REPLICATION; END IF;
    END \$\$;" >/dev/null
    local_sql "ALTER ROLE $APP_USER PASSWORD '$APP_PASSWORD'" >/dev/null
    local_sql "ALTER ROLE $REPL_USER PASSWORD '$REPL_PASSWORD'" >/dev/null
    [[ "$(local_sql "SELECT 1 FROM pg_database WHERE datname = '$DB_NAME'")" == 1 ]] || local_sql "CREATE DATABASE $DB_NAME OWNER $APP_USER" >/dev/null
    # What pg_rewind and the status checks need, without making the login a superuser.
    local_sql "GRANT pg_monitor TO $REPL_USER;
        GRANT EXECUTE ON FUNCTION pg_ls_dir(text, boolean, boolean) TO $REPL_USER;
        GRANT EXECUTE ON FUNCTION pg_stat_file(text, boolean) TO $REPL_USER;
        GRANT EXECUTE ON FUNCTION pg_read_binary_file(text) TO $REPL_USER;
        GRANT EXECUTE ON FUNCTION pg_read_binary_file(text, bigint, bigint, boolean) TO $REPL_USER;" >/dev/null
    local_sql "SELECT pg_create_physical_replication_slot('$SLOT') WHERE NOT EXISTS (SELECT FROM pg_replication_slots WHERE slot_name = '$SLOT')" >/dev/null
    ensure_groups

    install_helper

    local me; me="$(hostname -I | awk '{print $1}')"
    local key; key="$(printf 'v=%s;port=%s;db=%s;user=%s;rp=%s' "$PG_VERSION" "$PORT" "$DB_NAME" "$APP_USER" "$REPL_PASSWORD" | base64 -w0)"
    echo
    say "Server 1 is ready as the primary."
    echo
    echo "  Now run this on server 2 ($PEER_HOST), with the script copied there:"
    echo
    echo "    sudo bash remoteman-db.sh second --primary $me --join $key --auto-failover"
    echo
    echo "  (Leave out --auto-failover to switch over by hand with 'remoteman-db promote'.)"
    echo
    echo "  RemoteMan data source (PostgreSQL), connection string:"
    echo
    echo "    Host=$me:$PORT,$PEER_HOST:$PEER_PORT;Database=$DB_NAME;Username=$APP_USER;Password=$APP_PASSWORD;Target Session Attributes=primary"
    echo
    echo "  The password for $APP_USER is shown only now. 'remoteman-db conn' prints the string without it."
    echo "  Give each person their own login instead with 'remoteman-db user add <name>'."
}

# ---------------------------------------------------------------- second server
cmd_second() {
    need_root second
    PEER_HOST=""; local key=""
    while [[ $# -gt 0 ]]; do
        case "$1" in
            --primary) PEER_HOST="$2"; shift 2 ;;
            --join) key="$2"; shift 2 ;;
            --auto-failover) AUTO_FAILOVER=yes; shift ;;
            --witness) WITNESS="$2"; shift 2 ;;
            *) die "Unknown option $1" ;;
        esac
    done
    [[ -n "$PEER_HOST" && -n "$key" ]] || die "Use: second --primary <IP of server 1> --join <key printed by server 1>"
    local decoded; decoded="$(echo "$key" | base64 -d 2>/dev/null)" || die "That join key isn't valid."
    local kv
    IFS=';' read -ra kv <<< "$decoded"
    for p in "${kv[@]}"; do
        case "$p" in
            v=*) PG_VERSION="${p#v=}" ;; port=*) PEER_PORT="${p#port=}" ;; db=*) DB_NAME="${p#db=}" ;;
            user=*) APP_USER="${p#user=}" ;; rp=*) REPL_PASSWORD="${p#rp=}" ;;
        esac
    done
    [[ -n "${REPL_PASSWORD:-}" && -n "${DB_NAME:-}" ]] || die "That join key isn't valid."
    # Without a witness the watcher can't tell "server 1 is down" from "I lost the network":
    # the default gateway stands in (promote only while the gateway answers).
    if [[ "${AUTO_FAILOVER:-no}" == yes && -z "${WITNESS:-}" ]]; then
        WITNESS="$(ip route show default 2>/dev/null | awk '{print $3; exit}')"
    fi

    install_postgres
    ensure_cluster
    write_settings
    write_conf
    open_firewall

    say "Checking that server 1 answers"
    peer_sql 'SELECT 1' >/dev/null || die "Can't log in to $PEER_HOST:$PEER_PORT. Check the IP, the firewall and that 'first' finished there."

    say "Copying the database from server 1 (this can take a moment)"
    cluster stop >/dev/null 2>&1 || true
    local dd; dd="$(data_dir)"
    rm -rf "${dd:?}"/*
    pg pg_basebackup -h "$PEER_HOST" -p "$PEER_PORT" -U "$REPL_USER" -D "$dd" -X stream -S "$SLOT" -c fast -w
    clean_auto_conf
    pg touch "$dd/standby.signal"
    cluster start

    install_helper
    sleep 2
    echo
    say "Server 2 is ready as the hot standby of $PEER_HOST."
    "$HELPER" status || true
    if [[ "${AUTO_FAILOVER:-no}" == yes ]]; then
        echo "  Automatic failover is on: it takes over when server 1 is gone for ${FAILOVER_SECONDS}s${WITNESS:+ and $WITNESS still answers}."
    fi
}

# ---------------------------------------------------------------- day two
cmd_status() {
    load_conf
    local role peer
    if ! is_running; then role="stopped"; elif in_recovery; then role="standby"; else role="primary"; fi
    peer="$(peer_role)"
    echo "This server ($(hostname -s), PostgreSQL $PG_VERSION): $role"
    echo "Other server ($PEER_HOST):                  $peer"
    if [[ "$role" == primary ]]; then
        local_sql "SELECT '  streaming to ' || client_addr || ', ' || state || ', lag ' || COALESCE(replay_lag::text, '0') FROM pg_stat_replication" | sed '/^$/d'
        [[ "$peer" == primary ]] && warn "Both servers are primary. The one with the older timeline must rejoin: 'remoteman-db rejoin' on it."
    elif [[ "$role" == standby ]]; then
        local_sql "SELECT '  replaying, last change ' || COALESCE(now() - pg_last_xact_replay_timestamp(), '0'::interval)::text || ' ago'"
    fi
    echo "Automatic failover: ${AUTO_FAILOVER:-no}"
}

cmd_promote() {
    need_root promote; load_conf
    is_running || die "PostgreSQL isn't running here."
    in_recovery || { say "This server is already the primary."; return; }
    [[ "$(peer_role)" == primary ]] && [[ "${1:-}" != --force ]] && die "$PEER_HOST is still the primary. Stop it first, or use 'promote --force' if you know it's gone."
    say "Promoting this server to primary"
    local_sql "SELECT pg_promote(true, 60)" >/dev/null
    local_sql "CHECKPOINT" >/dev/null
    # A slot for the other server, so it can come back as the standby.
    local_sql "SELECT pg_create_physical_replication_slot('$SLOT') WHERE NOT EXISTS (SELECT FROM pg_replication_slots WHERE slot_name = '$SLOT')" >/dev/null
    say "Done. RemoteMan switches over by itself on its next connection."
}

cmd_rejoin() {
    need_root rejoin; load_conf
    [[ "$(peer_role)" == primary ]] || die "$PEER_HOST isn't a primary right now, so there's nothing to rejoin."
    say "Turning this server into the standby of $PEER_HOST"
    cluster stop -m fast 2>/dev/null || true
    rewind_from_peer
    cluster start
    say "Done."
}

rewind_from_peer() {
    local dd; dd="$(data_dir)"
    if ! pg "/usr/lib/postgresql/$PG_VERSION/bin/pg_rewind" -D "$dd" \
            --source-server="host=$PEER_HOST port=$PEER_PORT user=$REPL_USER dbname=postgres" -R --progress >/dev/null 2>&1; then
        warn "Rewind wasn't possible; copying the whole database from $PEER_HOST instead"
        rm -rf "${dd:?}"/*
        pg pg_basebackup -h "$PEER_HOST" -p "$PEER_PORT" -U "$REPL_USER" -D "$dd" -X stream -S "$SLOT" -c fast -w
    fi
    # The rewind copied the other server's slots and replication settings; neither belongs here.
    rm -rf "${dd:?}"/pg_replslot/*
    clean_auto_conf
    pg touch "$dd/standby.signal"
}

# Runs before PostgreSQL starts (systemd drop-in). This server was a primary, the other one is a
# primary now on a newer timeline: it took over while this one was down, so rewind first.
cmd_pre_start() {
    load_conf
    local dd; dd="$(data_dir)"
    [[ -f "$dd/standby.signal" ]] && return 0
    [[ "$(peer_role)" == primary ]] || return 0
    local mine theirs
    mine="$(pg "/usr/lib/postgresql/$PG_VERSION/bin/pg_controldata" "$dd" | awk -F: '/Latest checkpoint.s TimeLineID/ {gsub(/ /,"",$2); print $2}')"
    theirs="$(timeline_peer)"
    if [[ -n "$mine" && -n "$theirs" && "$theirs" -gt "$mine" ]]; then
        logger -t remoteman-db "$PEER_HOST took over while this server was down; rejoining as its standby"
        rewind_from_peer
    fi
}

# Every 5 seconds: a standby with automatic failover promotes itself when the primary has been gone
# long enough (and the witness still answers, so it isn't just this server that lost the network);
# a primary that finds the other server is primary on a newer timeline steps down.
cmd_watch() {
    load_conf
    local gone=0
    while true; do
        if is_running; then
            if in_recovery; then
                if [[ "${AUTO_FAILOVER:-no}" == yes && "$(peer_role)" == down ]]; then
                    if [[ -z "${WITNESS:-}" ]] || ping -c1 -W2 "$WITNESS" >/dev/null 2>&1; then gone=$((gone + 5)); else gone=0; fi
                    if (( gone >= FAILOVER_SECONDS )); then
                        logger -t remoteman-db "$PEER_HOST gone for ${gone}s: promoting this server"
                        "$HELPER" promote --force || true
                        gone=0
                    fi
                else gone=0; fi
            elif [[ "$(peer_role)" == primary ]]; then
                local mine theirs
                mine="$(timeline_local)"; theirs="$(timeline_peer 2>/dev/null || echo 0)"
                if [[ -n "$theirs" && "$theirs" -gt "$mine" ]]; then
                    logger -t remoteman-db "$PEER_HOST is primary on a newer timeline: stepping down"
                    "$HELPER" rejoin || true
                fi
            fi
        fi
        sleep 5
    done
}

cmd_conn() {
    load_conf
    local me; me="$(hostname -I | awk '{print $1}')"
    echo "Host=$me:$PORT,$PEER_HOST:$PEER_PORT;Database=$DB_NAME;Username=$APP_USER;Password=<password>;Target Session Attributes=primary"
}

# ---------------------------------------------------------------- personal logins
# Everyone with edit rights is a member of the shared login ($APP_USER), which owns the database
# and its tables, and switches to it on connect (so tables RemoteMan creates stay owned by it).
# Read-only people are members of ${APP_USER}_readonly instead. RemoteMan records the login you
# connect with (session_user), so changes and "who's connected" show the person's own name.
# Logins live in the database, so they reach the standby by replication: add them once, on the primary.

valid_name() { [[ "$1" =~ ^[a-z_][a-z0-9_]{0,62}$ ]] || die "Login names: lowercase letters, digits and _ only (got '$1')."; }
sql_quote()  { printf "%s" "${1//\'/\'\'}"; }
db_sql()     { pg psql -X -At -v ON_ERROR_STOP=1 -p "$PORT" -d "$DB_NAME" -c "$1"; }
role_exists(){ [[ "$(local_sql "SELECT 1 FROM pg_roles WHERE rolname = '$1'")" == 1 ]]; }

need_primary() {
    is_running || die "PostgreSQL isn't running here."
    in_recovery && die "This server is the standby. Run this on the primary ($PEER_HOST); logins reach this server by themselves."
    return 0
}

# The read-only group and what it may touch. Safe to run again any time.
ensure_groups() {
    local ro="${APP_USER}_readonly"
    role_exists "$ro" || local_sql "CREATE ROLE $ro NOLOGIN" >/dev/null
    db_sql "GRANT CONNECT ON DATABASE $DB_NAME TO $ro;
        GRANT USAGE ON SCHEMA public TO $ro;
        GRANT SELECT ON ALL TABLES IN SCHEMA public TO $ro;
        ALTER DEFAULT PRIVILEGES FOR ROLE $APP_USER IN SCHEMA public GRANT SELECT ON TABLES TO $ro;" >/dev/null
    # Read-only people still show up as connected in RemoteMan.
    if [[ "$(db_sql "SELECT to_regclass('public.remoteman_presence') IS NOT NULL")" == t ]]; then
        db_sql "GRANT SELECT, INSERT, UPDATE, DELETE ON remoteman_presence TO $ro" >/dev/null
    fi
}

print_conn() {  # name password
    local me pw="$2"; me="$(hostname -I | awk '{print $1}')"
    [[ "$pw" =~ [\;\'\"[:space:]] ]] && pw="\"${pw//\"/\"\"}\""   # quoted when it has ; ' " or spaces
    echo "    Host=$me:$PORT,$PEER_HOST:$PEER_PORT;Database=$DB_NAME;Username=$1;Password=$pw;Target Session Attributes=primary"
}

cmd_user() {
    need_root "user $*"; load_conf
    RO_GROUP="${APP_USER}_readonly"
    local sub="${1:-list}"; shift || true
    case "$sub" in
        add)
            local name="${1:-}" ro=no pw=""; shift || true
            while [[ $# -gt 0 ]]; do
                case "$1" in
                    --read-only) ro=yes; shift ;;
                    --password) pw="$2"; shift 2 ;;
                    *) die "Unknown option $1" ;;
                esac
            done
            [[ -n "$name" ]] || die "Use: remoteman-db user add <name> [--read-only] [--password <pw>]"
            valid_name "$name"; need_primary; ensure_groups
            [[ "$name" == "$APP_USER" || "$name" == "$REPL_USER" || "$name" == "$RO_GROUP" || "$name" == postgres ]] && die "'$name' is reserved."
            role_exists "$name" && die "$name already exists. Use 'remoteman-db user passwd $name' for a new password."
            pw="${pw:-$(rand 20)}"
            local_sql "CREATE ROLE $name LOGIN PASSWORD '$(sql_quote "$pw")'" >/dev/null
            if [[ "$ro" == yes ]]; then
                local_sql "GRANT $RO_GROUP TO $name" >/dev/null
            else
                local_sql "GRANT $APP_USER TO $name; ALTER ROLE $name IN DATABASE $DB_NAME SET role = '$APP_USER'" >/dev/null
            fi
            say "Added $name ($( [[ $ro == yes ]] && echo read-only || echo can edit ))."
            echo
            echo "  $name's connection string for RemoteMan (the password is shown only now):"
            echo
            print_conn "$name" "$pw"
            echo
            ;;
        list)
            is_running || die "PostgreSQL isn't running here."
            printf '%-24s %-10s %-10s %s\n' LOGIN ACCESS "ONLINE" "LAST CHANGE IN REMOTEMAN"
            local has_entries; has_entries="$(db_sql "SELECT to_regclass('public.remoteman_entries') IS NOT NULL")"
            local last="NULL"
            [[ "$has_entries" == t ]] && last="(SELECT to_char(to_timestamp(max(updated_at) / 1000.0), 'YYYY-MM-DD HH24:MI') FROM remoteman_entries WHERE updated_by = r.rolname)"
            db_sql "SELECT r.rolname,
                       CASE WHEN NOT r.rolcanlogin THEN 'disabled'
                            WHEN r.rolname = '$APP_USER' THEN 'shared'
                            WHEN pg_has_role(r.oid, '$APP_USER', 'MEMBER') THEN 'edit'
                            ELSE 'read-only' END,
                       (SELECT count(*) FROM pg_stat_activity a WHERE a.usename = r.rolname AND a.datname = '$DB_NAME'),
                       COALESCE($last, '-')
                FROM pg_roles r
                WHERE NOT r.rolsuper AND (r.rolname = '$APP_USER' OR pg_has_role(r.oid, '$APP_USER', 'MEMBER') OR pg_has_role(r.oid, '$RO_GROUP', 'MEMBER'))
                ORDER BY r.rolname <> '$APP_USER', r.rolname" \
                | awk -F'|' '$1 != "'"$RO_GROUP"'" { printf "%-24s %-10s %-10s %s\n", $1, $2, ($3 > 0 ? "yes (" $3 ")" : "no"), $4 }'
            if in_recovery; then echo "(This is the standby: the list is the same as on the primary.)"; fi
            ;;
        passwd)
            local name="${1:-}" pw=""; shift || true
            [[ "${1:-}" == --password ]] && pw="${2:-}"
            [[ -n "$name" ]] || die "Use: remoteman-db user passwd <name> [--password <pw>]"
            valid_name "$name"; need_primary
            role_exists "$name" || die "There's no login called $name."
            [[ "$name" == "$REPL_USER" || "$name" == "$RO_GROUP" ]] && die "'$name' isn't a person's login."
            pw="${pw:-$(rand 20)}"
            local_sql "ALTER ROLE $name LOGIN PASSWORD '$(sql_quote "$pw")'" >/dev/null
            say "New password for $name. Their connection string (shown only now):"
            echo
            print_conn "$name" "$pw"
            echo
            ;;
        remove)
            local name="${1:-}"
            [[ -n "$name" ]] || die "Use: remoteman-db user remove <name>"
            valid_name "$name"; need_primary
            role_exists "$name" || die "There's no login called $name."
            [[ "$name" == "$REPL_USER" || "$name" == "$RO_GROUP" || "$name" == postgres ]] && die "'$name' can't be removed."
            if [[ "$name" == "$APP_USER" ]]; then
                # It owns the data, so it stays; it just can't be logged in with any more.
                local_sql "ALTER ROLE $APP_USER NOLOGIN" >/dev/null
                local_sql "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE usename = '$APP_USER'" >/dev/null
                say "The shared login $APP_USER is disabled. 'remoteman-db user passwd $APP_USER' turns it back on."
                return
            fi
            local_sql "ALTER ROLE $name NOLOGIN" >/dev/null
            local_sql "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE usename = '$name'" >/dev/null
            if local_sql "DROP ROLE $name" >/dev/null 2>&1; then
                say "Removed $name and closed their open connections. Their name stays on the changes they made."
            else
                say "Disabled $name and closed their open connections (they own something in the database, so the login is kept but can't log in)."
            fi
            ;;
        *) die "Use: remoteman-db user add|list|passwd|remove" ;;
    esac
}

# After copying a newer remoteman-db.sh to a server that is already set up: refresh the helper,
# the access rules and the watcher. Run it on both servers.
cmd_update() {
    need_root update; load_conf
    write_settings
    install_helper
    systemctl restart remoteman-db-watch.service 2>/dev/null || true
    is_running && cluster reload
    if is_running && ! in_recovery; then ensure_groups; fi
    say "Updated $(hostname -s)."
}

# ---------------------------------------------------------------- shared-session relay
# Lets teammates join each other's shared RDP/SSH sessions from different networks. Both sides connect
# out to it; it only pipes bytes (the session is TLS end to end). RemoteMan looks for it on the database
# servers at port 47120 by itself, so nothing needs setting up in the app. Install it on both servers.
RELAY_PORT="${RELAY_PORT:-47120}"
RELAY_FILE=/usr/local/lib/remoteman/remoteman-relay.py

write_relay() {
    mkdir -p "$(dirname "$RELAY_FILE")"
    cat > "$RELAY_FILE" <<'PYEOF'
#!/usr/bin/env python3
"""RemoteMan shared-session relay.

Lets a teammate join a shared session when they can't reach the host directly (different networks,
no VPN). Both sides connect *out* to this relay, which only splices bytes: TLS runs end to end between
guest and host, pinned to the host's certificate, so the relay can't read or change anything.

Protocol (one ASCII line, then raw bytes):
  host  -> HOST <share>           relay -> OK; later CONN <id> per guest, PING now and then
  guest -> JOIN <share>           relay -> OK once spliced to the host, or NOHOST
  host  -> ACCEPT <share> <id>    relay -> OK, then this connection is the guest's pipe

Standard library only (Python 3.8+). Usage: remoteman-relay.py [--port 47120] [--bind 0.0.0.0]
"""
import argparse
import asyncio
import secrets

MAX_LINE = 200
MAX_PENDING = 200
JOIN_WAIT = 10

hosts = {}      # share -> writer of the host's control connection
pending = {}    # conn id -> (share, future that gets the host's (reader, writer))


async def read_line(reader):
    line = await asyncio.wait_for(reader.readuntil(b"\n"), 15)
    if len(line) > MAX_LINE:
        raise ValueError("line too long")
    return line.decode("ascii", "replace").strip()


async def send(writer, text):
    writer.write((text + "\n").encode("ascii"))
    await writer.drain()


async def pipe(reader, writer):
    try:
        while True:
            data = await reader.read(65536)
            if not data:
                break
            writer.write(data)
            await writer.drain()
    except (ConnectionError, asyncio.CancelledError, OSError):
        pass
    finally:
        try:
            writer.close()
        except Exception:
            pass


async def serve(reader, writer):
    keep = False
    try:
        parts = (await read_line(reader)).split()
        if len(parts) == 2 and parts[0] == "HOST":
            share = parts[1]
            old = hosts.get(share)
            if old is not None and old is not writer:
                old.close()
            hosts[share] = writer
            await send(writer, "OK")
            keep = True
            try:
                while True:
                    try:
                        data = await asyncio.wait_for(reader.read(256), 30)
                        if not data:
                            break
                    except asyncio.TimeoutError:
                        await send(writer, "PING")
            finally:
                if hosts.get(share) is writer:
                    del hosts[share]
                keep = False
        elif len(parts) == 2 and parts[0] == "JOIN":
            share = parts[1]
            host = hosts.get(share)
            if host is None or len(pending) >= MAX_PENDING:
                await send(writer, "NOHOST")
                return
            conn = secrets.token_hex(6)
            fut = asyncio.get_running_loop().create_future()
            pending[conn] = (share, fut)
            try:
                await send(host, "CONN " + conn)
                h_reader, h_writer = await asyncio.wait_for(fut, JOIN_WAIT)
            finally:
                pending.pop(conn, None)
            await send(writer, "OK")
            keep = True
            await asyncio.gather(pipe(reader, h_writer), pipe(h_reader, writer))
            keep = False
        elif len(parts) == 3 and parts[0] == "ACCEPT":
            share, conn = parts[1], parts[2]
            item = pending.get(conn)
            if item is None or item[0] != share or item[1].done():
                await send(writer, "NOCONN")
                return
            await send(writer, "OK")
            keep = True
            item[1].set_result((reader, writer))
    except Exception:
        keep = False
    finally:
        if not keep:
            try:
                writer.close()
            except Exception:
                pass


async def main():
    ap = argparse.ArgumentParser(description="RemoteMan shared-session relay")
    ap.add_argument("--port", type=int, default=47120)
    ap.add_argument("--bind", default="0.0.0.0")
    args = ap.parse_args()
    server = await asyncio.start_server(serve, args.bind, args.port)
    print(f"RemoteMan relay listening on {args.bind}:{args.port}", flush=True)
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    asyncio.run(main())
PYEOF
    chmod 755 "$RELAY_FILE"
}

cmd_relay() {
    case "${1:-}" in
        install)
            need_root "relay install"
            command -v python3 >/dev/null || { say "Installing python3"; apt-get install -y -qq python3 >/dev/null; }
            id remoteman-relay >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin remoteman-relay
            write_relay
            cat > /etc/systemd/system/remoteman-relay.service <<EOF
[Unit]
Description=RemoteMan shared-session relay
After=network-online.target
Wants=network-online.target

[Service]
User=remoteman-relay
ExecStart=/usr/bin/python3 $RELAY_FILE --port $RELAY_PORT
Restart=always
RestartSec=2
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true

[Install]
WantedBy=multi-user.target
EOF
            systemctl daemon-reload
            systemctl enable --now remoteman-relay.service >/dev/null 2>&1
            systemctl restart remoteman-relay.service
            if command -v ufw >/dev/null && ufw status 2>/dev/null | grep -q "Status: active"; then
                say "Opening port $RELAY_PORT in ufw"; ufw allow "$RELAY_PORT"/tcp >/dev/null
            fi
            sleep 1
            systemctl is-active --quiet remoteman-relay.service || die "The relay didn't start: journalctl -u remoteman-relay"
            say "Relay running on port $RELAY_PORT. RemoteMan finds it on its own; teammates on other networks can now join shared sessions."
            say "Teammates must be able to reach this server on TCP $RELAY_PORT (open it on any firewall or port forward in front of it)."
            ;;
        status)
            systemctl --no-pager status remoteman-relay.service 2>/dev/null | head -5 || say "The relay isn't installed."
            ;;
        remove)
            need_root "relay remove"
            systemctl disable --now remoteman-relay.service >/dev/null 2>&1 || true
            rm -f /etc/systemd/system/remoteman-relay.service "$RELAY_FILE"
            systemctl daemon-reload
            say "Relay removed."
            ;;
        *) die "Use: remoteman-db relay install|status|remove" ;;
    esac
}

usage() { sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'; }

case "${1:-}" in
    first) shift; cmd_first "$@" ;;
    second) shift; cmd_second "$@" ;;
    status) cmd_status ;;
    promote) shift; cmd_promote "$@" ;;
    rejoin) cmd_rejoin ;;
    pre-start) cmd_pre_start ;;
    watch) cmd_watch ;;
    conn) cmd_conn ;;
    user) shift; cmd_user "$@" ;;
    update) cmd_update ;;
    relay) shift; cmd_relay "$@" ;;
    *) usage ;;
esac
