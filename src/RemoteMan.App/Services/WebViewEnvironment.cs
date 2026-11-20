using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Web.WebView2.Core;

namespace RemoteMan.App.Services;

/// <summary>
/// One shared WebView2 environment for every terminal and web tab: the browser process
/// starts once, so each new tab opens in tens of milliseconds instead of seconds.
/// Web entries set to "Use my Windows login" get their own environment that is allowed to
/// answer Kerberos/NTLM (Negotiate) challenges, but only from that entry's hosts.
/// </summary>
public static class WebViewEnvironment
{
    private static Task<CoreWebView2Environment>? _env;
    private static readonly Dictionary<string, Task<CoreWebView2Environment>> Sso = new();

    public static Task<CoreWebView2Environment> GetAsync() =>
        _env ??= CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewData,
            new CoreWebView2EnvironmentOptions("--disable-features=msSmartScreenProtection"));

    public static Task<CoreWebView2Environment> GetSsoAsync(IEnumerable<string> hosts)
    {
        var list = string.Join(",", hosts.Select(h => h.ToLowerInvariant()).Distinct().OrderBy(h => h));
        lock (Sso)
        {
            if (Sso.TryGetValue(list, out var env)) return env;
            var folder = Path.Combine(AppPaths.WebViewData + "-sso", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(list)))[..12]);
            var options = new CoreWebView2EnvironmentOptions(
                $"--disable-features=msSmartScreenProtection --auth-server-allowlist=\"{list}\"")
            {
                AllowSingleSignOnUsingOSPrimaryAccount = true, // Entra ID / Microsoft account sites
            };
            return Sso[list] = CoreWebView2Environment.CreateAsync(null, folder, options);
        }
    }
}
