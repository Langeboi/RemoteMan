using System.Net.Http.Json;
using System.Text.Json;
using RemoteMan.Core.Model;

namespace RemoteMan.Protocols.Proxmox;

/// <summary>Signs in to the Proxmox API and builds the noVNC console address for a VM or container.</summary>
public static class ProxmoxConsole
{
    public static string ConsoleUrl(HostEndpoint ep, ProxmoxSettings s) =>
        $"https://{ep.Host}:{(ep.Port > 0 ? ep.Port : 8006)}/?console={(s.Container ? "lxc" : "kvm")}&novnc=1&vmid={s.VmId}" +
        $"&node={Uri.EscapeDataString(s.Node)}&resize=scale";

    /// <summary>Returns the PVEAuthCookie ticket. "root" becomes "root@pam" when no realm is given.</summary>
    public static async Task<string> GetTicketAsync(HostEndpoint ep, ResolvedCredential cred, bool ignoreCertificateErrors, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(cred.UserName) || cred.Password is null)
            throw new InvalidOperationException("The Proxmox console needs a user name and password on the entry (for example root@pam).");
        var user = cred.UserName.Contains('@') ? cred.UserName : cred.UserName + "@pam";
        using var handler = new HttpClientHandler();
        if (ignoreCertificateErrors) handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        var url = $"https://{ep.Host}:{(ep.Port > 0 ? ep.Port : 8006)}/api2/json/access/ticket";
        using var resp = await http.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = user, ["password"] = cred.Password }), ct);
        if ((int)resp.StatusCode == 401) throw new UnauthorizedAccessException($"Proxmox rejected the login for {user}.");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("data").GetProperty("ticket").GetString()
               ?? throw new InvalidOperationException("Proxmox returned no ticket.");
    }
}
