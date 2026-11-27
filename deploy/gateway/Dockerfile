# RemoteMan gateway: the relay (at home, reachable on 443) and the connector (in each remote network, outbound only).
# Same image for both; the command picks the role. Build from the repository root:
#   docker build -f deploy/gateway/Dockerfile -t remoteman-gateway .
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props global.json ./
COPY src/RemoteMan.Gateway src/RemoteMan.Gateway
COPY src/RemoteMan.Gateway.Server src/RemoteMan.Gateway.Server
# The table definitions hosted workspaces share with the app (linked into the relay, see its .csproj).
COPY src/RemoteMan.Data/PostgresSchema.cs src/RemoteMan.Data/PostgresSchema.cs
ARG VERSION=dev
# Framework-dependent IL runs on amd64 and arm64 alike, so one build serves both platforms.
# MSBuild reads environment variables as properties, so the VERSION build arg would become the projects' $(Version)
# ("test-abc1234" isn't a valid one and restore fails): hide it and pass it only as the informational version.
# EnableWindowsTargeting (Directory.Build.props, for the WPF app) would fetch the Windows reference pack: not needed here.
RUN env -u VERSION dotnet publish src/RemoteMan.Gateway.Server -c Release -o /out -p:InformationalVersion=${VERSION} -p:EnableWindowsTargeting=false -p:NuGetAudit=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
COPY --from=build /out /opt/remoteman-gateway
RUN printf '#!/bin/sh\nexec dotnet /opt/remoteman-gateway/remoteman-gateway.dll "$@"\n' > /usr/local/bin/gateway \
 && chmod 755 /usr/local/bin/gateway \
 && mkdir -p /data && chown app:app /data && chmod 700 /data
ARG VERSION=dev
ENV DATA=/data PORT=8080 REMOTEMAN_GATEWAY_VERSION=${VERSION} DOTNET_gcServer=0
LABEL org.opencontainers.image.title="RemoteMan gateway" \
      org.opencontainers.image.description="Relay and connector that let RemoteMan reach databases and hosts in other networks over 443, outbound only" \
      org.opencontainers.image.source="https://github.com/langeboi/remoteman"
USER app
VOLUME /data
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s CMD ["gateway", "health"]
ENTRYPOINT ["gateway"]
CMD ["relay"]
