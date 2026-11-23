# RemoteMan database server: PostgreSQL with streaming replication between servers, automatic failover,
# the admin page (https, port 4433) and the shared-session relay (run with "relay").
# Build from the repository root:  docker build -f deploy/docker/Dockerfile -t remoteman-server .
ARG PG_MAJOR=17
FROM postgres:${PG_MAJOR}-bookworm

RUN apt-get update \
 && apt-get install -y --no-install-recommends python3 python3-psycopg2 python3-cryptography iputils-ping openssl \
 && rm -rf /var/lib/apt/lists/* \
 && mkdir -p /var/lib/remoteman /opt/remoteman \
 && chown postgres:postgres /var/lib/remoteman && chmod 700 /var/lib/remoteman

ARG VERSION=dev
ARG IMAGE=ghcr.io/langeboi/remoteman-server:latest
ENV REMOTEMAN_HOME=/var/lib/remoteman \
    REMOTEMAN_SERVER_VERSION=${VERSION} \
    REMOTEMAN_IMAGE=${IMAGE} \
    PYTHONPATH=/opt/remoteman \
    PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1

COPY deploy/postgres/remoteman-db.sh deploy/relay/remoteman-relay.py /opt/remoteman/
COPY deploy/docker/rmserver /opt/remoteman/rmserver
RUN printf '#!/bin/sh\nexec python3 -m rmserver.main "$@"\n' > /usr/local/bin/remoteman-server \
 && printf '#!/bin/sh\nexec python3 -m rmserver.main db "$@"\n' > /usr/local/bin/remoteman-db \
 && chmod 755 /usr/local/bin/remoteman-server /usr/local/bin/remoteman-db

LABEL org.opencontainers.image.title="RemoteMan server" \
      org.opencontainers.image.description="Redundant PostgreSQL for RemoteMan with failover, an admin page and the shared-session relay" \
      org.opencontainers.image.source="https://github.com/langeboi/remoteman"

USER postgres
VOLUME /var/lib/remoteman
EXPOSE 5432 4433 47120
STOPSIGNAL SIGTERM
HEALTHCHECK --interval=30s --timeout=5s --start-period=15m CMD ["remoteman-server", "health"]
ENTRYPOINT ["remoteman-server"]
CMD ["serve"]
