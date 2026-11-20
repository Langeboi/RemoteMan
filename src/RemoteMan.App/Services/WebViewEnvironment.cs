using Microsoft.Web.WebView2.Core;

namespace RemoteMan.App.Services;

/// <summary>
/// One shared WebView2 environment for every terminal and web tab: the browser process
/// starts once, so each new tab opens in tens of milliseconds instead of seconds.
/// </summary>
public static class WebViewEnvironment
{
    private static Task<CoreWebView2Environment>? _env;

    public static Task<CoreWebView2Environment> GetAsync() =>
        _env ??= CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewData,
            new CoreWebView2EnvironmentOptions("--disable-features=msSmartScreenProtection"));

    private static Task<CoreWebView2Environment>? _ai;

    /// <summary>A separate profile for AI account sign-in, so those cookies never mix with web entries.</summary>
    public static Task<CoreWebView2Environment> GetAiAsync() =>
        _ai ??= CoreWebView2Environment.CreateAsync(null, AppPaths.AiWebData);
}
