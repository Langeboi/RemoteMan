using System.Diagnostics;
using RemoteMan.Core.Model;

namespace RemoteMan.Protocols.Tools;

/// <summary>
/// Opens a PowerShell window with Enter-PSSession to the host. The password goes to the new process
/// in an environment variable (removed as soon as it's read), never on the command line.
/// </summary>
public static class PowerShellLauncher
{
    public static void Open(HostEndpoint endpoint, ResolvedCredential cred, PowerShellSettings s)
    {
        var host = Quote(endpoint.Host);
        var args = $"-ComputerName {host}";
        var defaultPort = s.UseSsl ? 5986 : 5985;
        if (endpoint.Port > 0 && endpoint.Port != defaultPort) args += $" -Port {endpoint.Port}";
        if (s.UseSsl) args += " -UseSSL";
        if (!string.IsNullOrWhiteSpace(s.ConfigurationName)) args += $" -ConfigurationName {Quote(s.ConfigurationName!)}";

        var script = "$Host.UI.RawUI.WindowTitle = " + Quote($"{endpoint.Host} (PowerShell remoting)") + "; ";
        var psi = new ProcessStartInfo(PowerShellExe()) { UseShellExecute = false };
        if (!cred.UseWindowsLogin && !string.IsNullOrEmpty(cred.UserName))
        {
            var user = string.IsNullOrEmpty(cred.Domain) || cred.UserName!.Contains('\\') || cred.UserName.Contains('@') ? cred.UserName! : $"{cred.Domain}\\{cred.UserName}";
            if (cred.Password is { } pw)
            {
                psi.Environment["REMOTEMAN_PS_PW"] = pw;
                script += "$p = ConvertTo-SecureString $env:REMOTEMAN_PS_PW -AsPlainText -Force; Remove-Item Env:REMOTEMAN_PS_PW; " +
                          $"$c = New-Object System.Management.Automation.PSCredential({Quote(user)}, $p); ";
            }
            else script += $"$c = Get-Credential -UserName {Quote(user)} -Message 'Password for {endpoint.Host}'; ";
            args += " -Credential $c";
        }
        script += $"Enter-PSSession {args}";
        psi.ArgumentList.Add("-NoExit");
        psi.ArgumentList.Add("-NoLogo");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);
        // A console program started without a shell gets its own console window.
        psi.CreateNoWindow = false;
        Process.Start(psi);
    }

    private static string PowerShellExe()
    {
        // PowerShell 7 if installed, else Windows PowerShell (always there).
        foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
        {
            var p7 = Path.Combine(dir, "PowerShell", "7", "pwsh.exe");
            if (File.Exists(p7)) return p7;
        }
        return Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    private static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
}
