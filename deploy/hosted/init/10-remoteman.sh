#!/bin/sh
# First start only (docker-entrypoint-initdb.d): a self-signed certificate for DB_NAME, TLS required for every
# network sign-in, the superuser only from the relay, and a short delay after each failed password.
set -eu
cd "$PGDATA"

openssl req -new -x509 -days 3650 -nodes -newkey rsa:3072 -subj "/CN=${DB_NAME}" \
  -addext "subjectAltName=DNS:${DB_NAME}" -addext "basicConstraints=critical,CA:TRUE" \
  -keyout server.key -out server.crt
chmod 600 server.key

cat >> postgresql.conf <<CONF

# ---- RemoteMan hosted workspaces ----
ssl = on
ssl_cert_file = 'server.crt'
ssl_key_file = 'server.key'
ssl_min_protocol_version = 'TLSv1.2'
password_encryption = 'scram-sha-256'
shared_preload_libraries = 'auth_delay'
auth_delay.milliseconds = 500
max_connections = 300
log_connections = off
log_disconnections = off
CONF

cat > pg_hba.conf <<HBA
# TYPE   DATABASE  USER      ADDRESS            METHOD
local    all       all                          trust
# The relay manages accounts, workspaces and backups as postgres, from its own address only.
hostssl  all       postgres  ${RELAY_IP}/32     scram-sha-256
host     all       postgres  all                reject
# Everyone else: their own RemoteMan account login, over TLS. The databases themselves decide who may connect
# (each workspace grants CONNECT to its members only).
hostssl  all       all       0.0.0.0/0          scram-sha-256
hostssl  all       all       ::/0               scram-sha-256
HBA
