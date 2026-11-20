using RemoteMan.Core.Model;
using RemoteMan.Core.Networking;
using RemoteMan.Core.Transfer;

namespace RemoteMan.Core.Import;

/// <summary>
/// Reads Microsoft .rdp files ("key:type:value" lines). Saved passwords ("password 51:b:") are
/// DPAPI blobs bound to the PC and user that saved them, so they are skipped.
/// </summary>
public static class RdpFileImporter
{
    public static Dictionary<string, string> ParseSettings(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            var a = line.IndexOf(':');
            if (a <= 0) continue;
            var b = line.IndexOf(':', a + 1);
            if (b < 0) continue;
            d[line[..a].Trim()] = line[(b + 1)..];
        }
        return d;
    }

    /// <summary>Imports several .rdp files. Same user/domain pairs share one credential entry.</summary>
    public static ImportResult Import(IEnumerable<(string FileName, string Text)> files)
    {
        var result = new ImportResult();
        var creds = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fileName, text) in files)
        {
            var s = ParseSettings(text);
            if (!s.TryGetValue("full address", out var address) || string.IsNullOrWhiteSpace(address))
            {
                result.Warnings.Add($"{fileName}: no \"full address\", skipped");
                continue;
            }
            var ep = EndpointResolver.Parse(address.Trim());
            if (ep.Port == 0 && Int(s, "server port") is > 0 and var sp && sp != 3389) ep = ep with { Port = sp };
            if (ep.Port == 0) ep = ep with { Port = 3389 };

            var e = new Entry
            {
                Kind = EntryKind.Rdp,
                Name = Path.GetFileNameWithoutExtension(fileName),
                Endpoints = { ep },
                Rdp = new RdpSettings
                {
                    AdminSession = Int(s, "administrative session") == 1,
                    RedirectClipboard = Int(s, "redirectclipboard") != 0,
                    RedirectDrives = s.TryGetValue("drivestoredirect", out var drives) && drives.Trim().Length > 0,
                    SmartSizing = Int(s, "smart sizing") != 0, // missing = on, which is RemoteMan's default
                },
            };
            // Full screen (screen mode id 2) means "fit to tab" here; a fixed window size is kept.
            if (Int(s, "screen mode id") == 1 && Int(s, "desktopwidth") is > 0 and var w && Int(s, "desktopheight") is > 0 and var h)
            {
                e.Rdp.DesktopWidth = w;
                e.Rdp.DesktopHeight = h;
            }
            if (s.TryGetValue("gatewayhostname", out var gw) && gw.Trim().Length > 0 && Int(s, "gatewayusagemethod") is not (0 or 4))
            {
                e.Rdp.GatewayHost = gw.Trim();
                e.Rdp.UseRdGateway = true;
            }

            var user = s.TryGetValue("username", out var u) ? u.Trim() : "";
            var domain = s.TryGetValue("domain", out var dm) ? dm.Trim() : "";
            if (user.Contains('\\') && domain.Length == 0)
            {
                var i = user.IndexOf('\\');
                domain = user[..i];
                user = user[(i + 1)..];
            }
            if (user.Length > 0)
            {
                var key = domain.Length > 0 ? $"{domain}\\{user}" : user;
                if (!creds.TryGetValue(key, out var cred))
                {
                    cred = new Entry
                    {
                        Kind = EntryKind.Credential, Name = key,
                        Credential = new CredentialData { UserName = user, Domain = domain.Length > 0 ? domain : null },
                    };
                    creds[key] = cred;
                    result.Items.Add(new ImportItem(cred));
                }
                e.CredentialId = cred.Id; e.Login = LoginSource.Saved;
                if (s.Keys.Any(k => k.StartsWith("password", StringComparison.OrdinalIgnoreCase)))
                    result.Warnings.Add($"{fileName}: the saved password is locked to the PC that saved it and was not imported");
            }
            result.Items.Add(new ImportItem(e));
        }
        result.SortAsTree();
        return result;
    }

    private static int? Int(Dictionary<string, string> s, string key) =>
        s.TryGetValue(key, out var v) && int.TryParse(v.Trim(), out var i) ? i : null;
}
