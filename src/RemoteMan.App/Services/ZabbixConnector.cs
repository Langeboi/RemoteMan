using System.Net;
using RemoteMan.Core.Integrations.Zabbix;
using RemoteMan.Core.Model;
using RemoteMan.Core.Networking;
using RemoteMan.Core.Security;
using RemoteMan.Core.Storage;
using RemoteMan.Protocols.Ssh;

namespace RemoteMan.App.Services;

/// <summary>Makes a connected Zabbix client from the settings, through the jump host entry when one is set.</summary>
public static class ZabbixConnector
{
    public static string? LoadToken(ZabbixConfig cfg)
    {
        if (string.IsNullOrEmpty(cfg.ProtectedToken)) return null;
        try { return new DpapiSecretProtector().Unprotect(cfg.ProtectedToken); }
        catch { return null; }
    }

    public static string ProtectToken(string token) => new DpapiSecretProtector().Protect(token.Trim());

    public static async Task<ZabbixClient> ConnectAsync(ZabbixConfig cfg, string token, EntryStore? store, ISecretProtector? protector,
        CancellationToken ct = default)
    {
        SshTunnel? tunnel = null;
        Func<EndPoint>? via = null;
        if (cfg.JumpHostEntryId is { } jumpId)
        {
            var jump = store?.Get(jumpId)
                ?? throw new ZabbixApiException("The jump host entry for Zabbix no longer exists. Pick another one in Zabbix settings.");
            if (jump.Kind != EntryKind.Ssh) throw new ZabbixApiException($"{jump.Name} isn't an SSH entry, so it can't be a jump host.");
            var api = ZabbixClient.ApiEndpoint(cfg.Url);
            var resolved = await new EndpointResolver().ResolveAsync(jump, ct);
            if (!resolved.Success)
                throw new ZabbixApiException($"Jump host {jump.Name} doesn't answer ({string.Join("; ", resolved.Attempts.Select(a => $"{a.Endpoint} {a.Error}"))}).");
            var cred = protector is null ? new RemoteMan.Protocols.ResolvedCredential(null, null, null, null, null) : Secrets.Resolve(jump, store!, protector);
            try
            {
                tunnel = await SshTunnel.OpenAsync(resolved.Endpoint!, cred, jump.Ssh ?? new SshSettings(), new KnownHosts(AppPaths.KnownHosts),
                    api.Host, api.Port, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new ZabbixApiException($"Jump host {jump.Name}: {ex.Message}", ex);
            }
            var local = tunnel.LocalEndPoint;
            via = () => local;
        }

        var client = new ZabbixClient(cfg.Url, token, cfg.IgnoreCertificateErrors, via, attached: tunnel);
        try
        {
            await client.ConnectAsync(ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
