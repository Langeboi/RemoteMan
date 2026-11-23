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

    /// <summary>
    /// Browser switches for terminal and web tabs. With vivid colours on, Chromium skips colour
    /// management and sends sRGB straight to the screen, like most native apps (RDM's terminal included).
    /// On wide-gamut or calibrated monitors that is what makes colours look saturated instead of washed out.
    /// Read once: the browser process keeps its switches until RemoteMan restarts.
    /// </summary>
    private static readonly Lazy<string> Args = new(() =>
    {
        var vivid = true;
        try { vivid = AppSettings.Load().VividColors; } catch (Exception) { }
        return "--disable-features=msSmartScreenProtection" + (vivid ? " --force-color-profile=srgb" : "");
    });

    public static Task<CoreWebView2Environment> GetAsync() =>
        _env ??= CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewData,
            new CoreWebView2EnvironmentOptions(Args.Value));

    public static Task<CoreWebView2Environment> GetSsoAsync(IEnumerable<string> hosts)
    {
        var list = string.Join(",", hosts.Select(h => h.ToLowerInvariant()).Distinct().OrderBy(h => h));
        lock (Sso)
        {
            if (Sso.TryGetValue(list, out var env)) return env;
            var folder = Path.Combine(AppPaths.WebViewData + "-sso", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(list)))[..12]);
            var options = new CoreWebView2EnvironmentOptions(
                $"{Args.Value} --auth-server-allowlist=\"{list}\"")
            {
                AllowSingleSignOnUsingOSPrimaryAccount = true, // Entra ID / Microsoft account sites
            };
            return Sso[list] = CoreWebView2Environment.CreateAsync(null, folder, options);
        }
    }
    private static Task<CoreWebView2Environment>? _ai;

    /// <summary>A separate profile for AI account sign-in, so those cookies never mix with web entries.</summary>
    public static Task<CoreWebView2Environment> GetAiAsync() =>
        _ai ??= CoreWebView2Environment.CreateAsync(null, AppPaths.AiWebData);
}
