using System.Net;
using RemoteMan.Core.Integrations.Oxidized;
using RemoteMan.Core.Model;
using RemoteMan.Core.Networking;
using RemoteMan.Core.Security;
using RemoteMan.Core.Storage;
using RemoteMan.Protocols.Ssh;

namespace RemoteMan.App.Services;

/// <summary>Makes an Oxidized client from the settings, through the jump host entry when one is set.</summary>
public static class OxidizedConnector
{
    public static string? LoadPassword(OxidizedConfig cfg)
    {
        if (string.IsNullOrEmpty(cfg.ProtectedPassword)) return null;
        try { return new DpapiSecretProtector().Unprotect(cfg.ProtectedPassword); }
        catch { return null; }
    }

    public static string ProtectPassword(string password) => new DpapiSecretProtector().Protect(password);

    public static async Task<OxidizedClient> ConnectAsync(OxidizedConfig cfg, string? password, EntryStore? store, ISecretProtector? protector,
        CancellationToken ct = default)
    {
        var baseUri = OxidizedClient.BaseUri(cfg.Url);
        SshTunnel? tunnel = null;
        Func<EndPoint>? via = null;
        if (cfg.JumpHostEntryId is { } jumpId)
        {
            var jump = store?.Get(jumpId)
                ?? throw new OxidizedApiException("The jump host entry for Oxidized no longer exists. Pick another one in Oxidized settings.");
            if (jump.Kind != EntryKind.Ssh) throw new OxidizedApiException($"{jump.Name} isn't an SSH entry, so it can't be a jump host.");
            var resolved = await new EndpointResolver().ResolveAsync(jump, ct);
            if (!resolved.Success)
                throw new OxidizedApiException($"Jump host {jump.Name} doesn't answer ({string.Join("; ", resolved.Attempts.Select(a => $"{a.Endpoint} {a.Error}"))}).");
            var cred = protector is null ? RemoteMan.Protocols.ResolvedCredential.None : Secrets.Resolve(jump, store!, protector);
            try
            {
                tunnel = await SshTunnel.OpenAsync(resolved.Endpoint!, cred, jump.Ssh ?? new SshSettings(), new KnownHosts(AppPaths.KnownHosts),
                    baseUri.Host, baseUri.Port, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new OxidizedApiException($"Jump host {jump.Name}: {ex.Message}", ex);
            }
            var local = tunnel.LocalEndPoint;
            via = () => local;
        }
        return new OxidizedClient(cfg.Url, string.IsNullOrWhiteSpace(cfg.UserName) ? null : cfg.UserName.Trim(), password,
            cfg.IgnoreCertificateErrors, via, attached: tunnel);
    }
}
