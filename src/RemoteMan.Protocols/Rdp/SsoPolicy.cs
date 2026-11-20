using Microsoft.Win32;

namespace RemoteMan.Protocols.Rdp;

/// <summary>
/// Checks whether Windows will hand the logged-on credentials to an RDP server (silent SSO).
/// That needs Group Policy: Computer Configuration > Administrative Templates > System >
/// Credentials Delegation > "Allow delegating default credentials" with TERMSRV/* (or the host).
/// </summary>
public static class SsoPolicy
{
    private const string Key = @"SOFTWARE\Policies\Microsoft\Windows\CredentialsDelegation";

    public static bool AllowsDefaultCredentials(string host)
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var key = Registry.LocalMachine.OpenSubKey(Key);
        if (key?.GetValue("AllowDefaultCredentials") is not int enabled || enabled != 1) return false;
        using var list = key.OpenSubKey("AllowDefaultCredentials");
        if (list is null) return false;
        foreach (var name in list.GetValueNames())
        {
            if (list.GetValue(name) is not string spn || !spn.StartsWith("TERMSRV/", StringComparison.OrdinalIgnoreCase)) continue;
            var pattern = spn[8..];
            if (pattern == "*" || pattern.Equals(host, StringComparison.OrdinalIgnoreCase)) return true;
            if (pattern.StartsWith("*.") && host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public const string Hint = "Windows will ask for your password once. For silent sign-in, enable the policy " +
        "\"Allow delegating default credentials\" for TERMSRV/* (Credentials Delegation).";
}
