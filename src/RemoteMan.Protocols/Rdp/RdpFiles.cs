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
        sb.AppendLine($"screen mode id:i:{(s.DesktopWidth > 0 ? 1 : 2)}");
        if (s.DesktopWidth > 0) { sb.AppendLine($"desktopwidth:i:{s.DesktopWidth}"); sb.AppendLine($"desktopheight:i:{s.DesktopHeight}"); }
        sb.AppendLine("dynamic resolution:i:1");
        sb.AppendLine("session bpp:i:32");
        sb.AppendLine($"redirectclipboard:i:{(s.RedirectClipboard ? 1 : 0)}");
        sb.AppendLine($"drivestoredirect:s:{(s.RedirectDrives ? "*" : "")}");
        sb.AppendLine($"administrative session:i:{(s.AdminSession ? 1 : 0)}");
        sb.AppendLine($"authentication level:i:{(s.IgnoreCertificateErrors ? 0 : 2)}");
        sb.AppendLine("enablecredsspsupport:i:1");
        sb.AppendLine("networkautodetect:i:1");
        sb.AppendLine("bandwidthautodetect:i:1");
        if (s.UseRdGateway && s.GatewayHost is { Length: > 0 } gw)
        {
            sb.AppendLine($"gatewayhostname:s:{gw}");
            sb.AppendLine("gatewayusagemethod:i:1");
            sb.AppendLine("gatewayprofileusagemethod:i:1");
            sb.AppendLine("promptcredentialonce:i:1");
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
