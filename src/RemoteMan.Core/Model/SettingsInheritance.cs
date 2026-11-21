namespace RemoteMan.Core.Model;

/// <summary>Copies one settings section from a folder to an entry ("Folder" ticked on an Advanced section).</summary>
public static class SettingsInheritance
{
    public static readonly InheritSections[] All =
    [
        InheritSections.Connection, InheritSections.Display, InheritSections.Resources, InheritSections.Gateway,
        InheritSections.Terminal, InheritSections.Authentication, InheritSections.Browser,
    ];

    public static void Copy(InheritSections section, Entry from, Entry to)
    {
        switch (section)
        {
            case InheritSections.Connection:
                to.Connection = from.Connection?.Copy();
                break;
            case InheritSections.Display:
            {
                var f = from.Rdp ?? new RdpSettings(); var t = to.Rdp ??= new RdpSettings();
                t.DesktopWidth = f.DesktopWidth; t.DesktopHeight = f.DesktopHeight; t.SmartSizing = f.SmartSizing;
                t.Monitors = f.Monitors; t.SelectedMonitors = f.SelectedMonitors; t.ScalePercent = f.ScalePercent;
                t.ColorDepth = f.ColorDepth; t.Speed = f.Speed;
                break;
            }
            case InheritSections.Resources:
            {
                var f = from.Rdp ?? new RdpSettings(); var t = to.Rdp ??= new RdpSettings();
                t.Audio = f.Audio; t.Microphone = f.Microphone; t.RedirectClipboard = f.RedirectClipboard; t.CopyOnSelect = f.CopyOnSelect;
                t.RedirectPrinters = f.RedirectPrinters; t.RedirectDrives = f.RedirectDrives; t.Drives = f.Drives; t.WindowsKeys = f.WindowsKeys;
                break;
            }
            case InheritSections.Gateway:
            {
                var f = from.Rdp ?? new RdpSettings(); var t = to.Rdp ??= new RdpSettings();
                t.UseRdGateway = f.UseRdGateway; t.GatewayHost = f.GatewayHost; t.GatewayCredentialId = f.GatewayCredentialId;
                t.GatewayBypassLocal = f.GatewayBypassLocal; t.SshTunnelId = f.SshTunnelId;
                break;
            }
            case InheritSections.Terminal:
            {
                var f = from.Ssh ?? new SshSettings(); var t = to.Ssh ??= new SshSettings();
                t.Theme = f.Theme; t.FontSize = f.FontSize; t.Scrollback = f.Scrollback; t.Encoding = f.Encoding;
                t.BackspaceCtrlH = f.BackspaceCtrlH; t.LogSession = f.LogSession; t.LogFolder = f.LogFolder; t.CopyOnSelect = f.CopyOnSelect;
                break;
            }
            case InheritSections.Authentication:
            {
                var f = from.Ssh ?? new SshSettings(); var t = to.Ssh ??= new SshSettings();
                t.AutoAcceptHostKey = f.AutoAcceptHostKey; t.AllowLegacyAlgorithms = f.AllowLegacyAlgorithms; t.AuthOrder = f.AuthOrder;
                t.KexAlgorithms = f.KexAlgorithms; t.Ciphers = f.Ciphers; t.HostKeyAlgorithms = f.HostKeyAlgorithms; t.KeepAliveSeconds = f.KeepAliveSeconds;
                break;
            }
            case InheritSections.Browser:
            {
                var f = from.Web ?? new WebSettings(); var t = to.Web ??= new WebSettings();
                t.IgnoreCertificateErrors = f.IgnoreCertificateErrors; t.Zoom = f.Zoom; t.Profile = f.Profile;
                t.AnswerBasicAuth = f.AnswerBasicAuth; t.KeepAliveMinutes = f.KeepAliveMinutes; t.CopyOnSelect = f.CopyOnSelect;
                break;
            }
        }
    }
}
