namespace RemoteMan.Gateway.Server.Admin;

/// <summary>The copy-paste that starts a connector: one docker run line, or a compose.yaml. Used by the CLI and the console.</summary>
public static class ConnectorSetup
{
    public static string DockerRun(string relayUrl, string token, string name, string image, string? allow = null) =>
        $"docker run -d --name remoteman-connector --restart unless-stopped --network host " +
        $"-e RELAY_URL={relayUrl} -e SITE_TOKEN={token} -e NAME={name}" +
        (string.IsNullOrWhiteSpace(allow) ? "" : $" -e ALLOW={allow.Replace(" ", "")}") +
        $" -v remoteman-connector:/data {image} connector";

    public static string Compose(string relayUrl, string token, string name, string image, string? allow = null) => $"""
        services:
          connector:
            image: {image}
            container_name: remoteman-connector
            command: connector
            restart: unless-stopped
            network_mode: host   # the machine's own DNS and routes, so internal names and subnets resolve
            environment:
              RELAY_URL: {relayUrl}
              SITE_TOKEN: {token}
              NAME: {name}
              {(string.IsNullOrWhiteSpace(allow) ? "# ALLOW: 10.0.0.0/8,192.168.0.0/16   # the most it may ever open (default: all private ranges)" : $"ALLOW: {allow.Replace(" ", "")}")}
            volumes:
              - connector-data:/data
        volumes:
          connector-data:
        """;

    /// <summary>
    /// A shell script for a Linux box that may not have Docker yet: installs it (Docker's own get.docker.com script)
    /// when it's missing, then starts the connector. Pipe it to sh.
    /// </summary>
    public static string Script(string relayUrl, string token, string name, string image) => $"""
        #!/bin/sh
        # RemoteMan gateway connector for {name}. Run as root (or with sudo) on a Linux machine inside the network.
        set -e
        if ! command -v docker >/dev/null 2>&1; then
          echo "Docker isn't installed; installing it with get.docker.com..."
          curl -fsSL https://get.docker.com | sh
        fi
        docker rm -f remoteman-connector >/dev/null 2>&1 || true
        {DockerRun(relayUrl, token, name, image)}
        echo "Connector {name} started. It shows as online in the admin console within a few seconds."
        echo "Logs: docker logs -f remoteman-connector"
        """;
}
