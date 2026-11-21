using System.Diagnostics;
using System.Text;
using RemoteMan.Core.Model;

namespace RemoteMan.Protocols.Rdp;

/// <summary>.rdp files and Windows' own Remote Desktop client (mstsc) for "Open with".</summary>
public static class RdpFiles
{
    public static string Build(Entry e, HostEndpoint ep, ResolvedCredential cred)
    {
        var s = e.Rdp ?? new RdpSettings();
        var user = cred.UseWindowsLogin ? null : cred.Domain is { Length: > 0 } d && cred.UserName is { Length: > 0 } ? $"{d}\\{cred.UserName}" : cred.UserName;
        var sb = new StringBuilder();
        sb.AppendLine($"full address:s:{ep.Host}{(ep.Port > 0 && ep.Port != 3389 ? ":" + ep.Port : "")}");
        if (user is { Length: > 0 }) sb.AppendLine($"username:s:{user}");
        var multi = s.Monitors != RdpMonitors.One;
        sb.AppendLine($"screen mode id:i:{(s.DesktopWidth > 0 && !multi ? 1 : 2)}");
        if (s.DesktopWidth > 0 && !multi) { sb.AppendLine($"desktopwidth:i:{s.DesktopWidth}"); sb.AppendLine($"desktopheight:i:{s.DesktopHeight}"); }
        if (multi) sb.AppendLine("use multimon:i:1");
        if (s.Monitors == RdpMonitors.Selected && s.SelectedMonitors is { Length: > 0 } mons) sb.AppendLine($"selectedmonitors:s:{mons.Replace(" ", "")}");
        if (s.ScalePercent >= 100) sb.AppendLine($"desktopscalefactor:i:{s.ScalePercent}");
        sb.AppendLine("dynamic resolution:i:1");
        sb.AppendLine($"session bpp:i:{(s.ColorDepth is 15 or 16 or 24 or 32 ? s.ColorDepth : 32)}");
        sb.AppendLine($"redirectclipboard:i:{(s.RedirectClipboard ? 1 : 0)}");
        var letters = (s.Drives ?? "").Split(',', ';', ' ').Select(x => x.Trim().TrimEnd(':', '\\')).Where(x => x.Length == 1).Select(x => x.ToUpperInvariant() + ":").ToList();
        sb.AppendLine($"drivestoredirect:s:{(!s.RedirectDrives ? "" : letters.Count > 0 ? string.Join(";", letters) : "*")}");
        sb.AppendLine($"redirectprinters:i:{(s.RedirectPrinters ? 1 : 0)}");
        sb.AppendLine($"audiomode:i:{(int)s.Audio}");
        sb.AppendLine($"audiocapturemode:i:{(s.Microphone ? 1 : 0)}");
        sb.AppendLine($"keyboardhook:i:{(int)s.WindowsKeys}");
        sb.AppendLine($"administrative session:i:{(s.AdminSession ? 1 : 0)}");
        sb.AppendLine($"authentication level:i:{(s.IgnoreCertificateErrors ? 0 : 2)}");
        sb.AppendLine("enablecredsspsupport:i:1");
        var type = s.Speed switch { RdpSpeed.Lan => 6, RdpSpeed.Broadband => 4, RdpSpeed.Slow => 2, _ => 7 };
        sb.AppendLine($"connection type:i:{type}");
        sb.AppendLine($"networkautodetect:i:{(type == 7 ? 1 : 0)}");
        sb.AppendLine($"bandwidthautodetect:i:{(type == 7 ? 1 : 0)}");
        if (e.Connection?.AutoReconnect == false) sb.AppendLine("autoreconnection enabled:i:0");
        if (s.UseRdGateway && s.GatewayHost is { Length: > 0 } gw)
        {
            sb.AppendLine($"gatewayhostname:s:{gw}");
            sb.AppendLine($"gatewayusagemethod:i:{(s.GatewayBypassLocal ? 2 : 1)}");
            sb.AppendLine("gatewayprofileusagemethod:i:1");
            sb.AppendLine($"promptcredentialonce:i:{(s.GatewayCredentialId is null ? 1 : 0)}");
        }
        return sb.ToString();
    }

    public static string Write(Entry e, HostEndpoint ep, ResolvedCredential cred)
    {
        var dir = Path.Combine(Path.GetTempPath(), "RemoteMan");
        Directory.CreateDirectory(dir);
        var safe = string.Concat(e.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var file = Path.Combine(dir, safe + ".rdp");
        File.WriteAllText(file, Build(e, ep, cred));
        return file;
    }

    /// <summary>Starts mstsc. A password is passed through Credential Manager (cmdkey) and removed after 30 s.</summary>
    public static async Task LaunchMstscAsync(string rdpFile, HostEndpoint ep, ResolvedCredential cred)
    {
        var target = "TERMSRV/" + ep.Host;
        var stored = false;
        if (!cred.UseWindowsLogin && cred.UserName is { Length: > 0 } && cred.Password is { Length: > 0 } pw)
        {
            var user = cred.Domain is { Length: > 0 } d ? $"{d}\\{cred.UserName}" : cred.UserName;
            var psi = new ProcessStartInfo("cmdkey") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add($"/generic:{target}");
            psi.ArgumentList.Add($"/user:{user}");
            psi.ArgumentList.Add($"/pass:{pw}");
            using (var p = Process.Start(psi)) if (p is not null) await p.WaitForExitAsync();
            stored = true;
        }
        Process.Start(new ProcessStartInfo("mstsc.exe") { UseShellExecute = true, ArgumentList = { rdpFile } });
        if (stored)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
                var del = new ProcessStartInfo("cmdkey") { UseShellExecute = false, CreateNoWindow = true };
                del.ArgumentList.Add($"/delete:{target}");
                try { using var p = Process.Start(del); } catch { }
            });
        }
    }
}
