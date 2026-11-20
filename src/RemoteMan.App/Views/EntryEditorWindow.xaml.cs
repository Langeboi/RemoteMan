using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteMan.Core.Integrations.Zabbix;
using RemoteMan.Core.Model;
using RemoteMan.Core.Security;
using RemoteMan.Core.Storage;

namespace RemoteMan.App.Views;

public sealed partial class HostRow : ObservableObject
{
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _port = "";
    [ObservableProperty] private string _role = "";
}

/// <summary>Edits an <see cref="Entry"/> in place; the caller saves it on OK.</summary>
public partial class EntryEditorWindow : Window
{
    private readonly Entry _e;
    private readonly ISecretProtector _protector;
    private readonly ObservableCollection<HostRow> _hosts = new();
    private sealed record CredChoice(Guid? Id, string Name, bool Windows = false);
    private ZabbixSnapshot? _zabbix;
    private string? _zabbixHostId;

    /// <summary>Latest Zabbix data; when set, connection entries get a Zabbix section with the link picker.</summary>
    public ZabbixSnapshot? Zabbix
    {
        get => _zabbix;
        set { _zabbix = value; ShowZabbix(); }
    }

    public EntryEditorWindow(Entry entry, EntryStore store, ISecretProtector protector)
    {
        InitializeComponent();
        _e = entry;
        _protector = protector;
        var kind = entry.Kind;
        Heading.Text = (entry.Revision == 0 ? "New " : "Edit ") + kind.ToString().ToLowerInvariant() + (entry.Name.Length > 0 ? $": {entry.Name}" : "");
        var editing = store.PresenceFor(entry.Id).Where(p => p.State == PresenceState.Editing).Select(p => p.User).ToList();
        OthersEditing.Text = editing.Count > 0 ? $"{string.Join(", ", editing)} also editing" : "";

        NameBox.Text = entry.Name;
        TagsBox.Text = entry.Tags;
        _zabbixHostId = entry.ZabbixHostId;

        var connectable = entry.IsConnectable;
        Show(HostsGroup, connectable);
        Show(UrlLabel, kind == EntryKind.Web); Show(UrlBox, kind == EntryKind.Web);
        Show(CredLabel, connectable); Show(CredBox, connectable);
        Show(CredGroup, kind == EntryKind.Credential);
        Show(RdpGroup, kind == EntryKind.Rdp);
        Show(SshGroup, kind == EntryKind.Ssh);
        Show(WebGroup, kind == EntryKind.Web);
        Show(VncGroup, kind == EntryKind.Vnc);
        if (kind == EntryKind.Web) HostsGroup.Header = "Fallback hosts (optional; the URL's host is used when empty)";

        foreach (var h in entry.Endpoints) _hosts.Add(new HostRow { Host = h.Host, Port = h.Port > 0 ? h.Port.ToString() : "" });
        if (_hosts.Count == 0 && kind != EntryKind.Web) _hosts.Add(new HostRow { Port = Entry.DefaultPort(kind).ToString() });
        HostsList.ItemsSource = _hosts;
        Renumber();

        var creds = new List<CredChoice> { new(null, "(none)") };
        // Single sign-on: RDP (CredSSP default credentials) and web (Kerberos/NTLM, Entra ID).
        if (kind is EntryKind.Rdp or EntryKind.Web) creds.Add(new(null, "(my Windows login)", true));
        creds.AddRange(store.Entries.Where(x => x.Kind == EntryKind.Credential).OrderBy(x => x.Name).Select(x => new CredChoice(x.Id, x.Name)));
        CredBox.ItemsSource = creds;
        CredBox.SelectedItem = entry.UseWindowsLogin && kind is EntryKind.Rdp or EntryKind.Web
            ? creds.First(c => c.Windows)
            : creds.FirstOrDefault(c => !c.Windows && c.Id == entry.CredentialId) ?? creds[0];

        if (entry.Credential is { } c)
        {
            UserBox.Text = c.UserName;
            DomainBox.Text = c.Domain;
        }

        var r = entry.Rdp ?? new RdpSettings();
        RdpIgnoreCert.IsChecked = r.IgnoreCertificateErrors; RdpSmart.IsChecked = r.SmartSizing; RdpClipboard.IsChecked = r.RedirectClipboard;
        RdpDrives.IsChecked = r.RedirectDrives; RdpCopySelect.IsChecked = r.CopyOnSelect; RdpAdmin.IsChecked = r.AdminSession; RdpGateway.Text = r.GatewayHost;

        var s = entry.Ssh ?? new SshSettings();
        SshAutoAccept.IsChecked = s.AutoAcceptHostKey; SshLegacy.IsChecked = s.AllowLegacyAlgorithms; SshStartup.Text = s.StartupCommand;
        SshCopySelect.IsChecked = s.CopyOnSelect; SshDownloads.Text = s.DownloadFolder;

        var w = entry.Web ?? new WebSettings();
        UrlBox.Text = w.Url;
        WebBrowser.ItemsSource = Enum.GetValues<BrowserKind>(); WebBrowser.SelectedItem = w.Browser;
        WebIgnoreCert.IsChecked = w.IgnoreCertificateErrors; WebAutoFill.IsChecked = w.AutoFill; WebAutoSubmit.IsChecked = w.AutoSubmit;
        WebUserSel.Text = w.UserFieldSelector; WebPassSel.Text = w.PasswordFieldSelector; WebSubmitSel.Text = w.SubmitSelector; WebCopySelect.IsChecked = w.CopyOnSelect;

        var v = entry.Vnc ?? new VncSettings();
        VncViewerBox.ItemsSource = Enum.GetValues<VncViewer>(); VncViewerBox.SelectedItem = v.Viewer; VncViewOnly.IsChecked = v.ViewOnly;

        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void ShowZabbix()
    {
        Show(ZabbixGroup, _zabbix is not null && _e.IsConnectable);
        if (_zabbix is null) return;
        var host = _zabbixHostId is { } id ? _zabbix.Matcher.GetHost(id) : null;
        if (host is not null) ZabbixCurrent.Text = $"Linked: {host.DisplayName} (hostid {host.HostId})";
        else if (_zabbixHostId is null && _zabbix.Matcher.Match(_e) is { } m) ZabbixCurrent.Text = $"Matched automatically: {m.Host.DisplayName} ({m.Reason})";
        else ZabbixCurrent.Text = _zabbixHostId is null ? "Not linked" : "Linked host no longer exists in Zabbix";
        ZabbixUnlink.IsEnabled = _zabbixHostId is not null;
    }

    private void OnZabbixLink(object sender, RoutedEventArgs e)
    {
        if (_zabbix is null) return;
        var probe = EntryStore.Clone(_e);
        probe.Name = NameBox.Text.Trim();
        probe.ZabbixHostId = _zabbixHostId;
        probe.Endpoints = _hosts.Where(h => !string.IsNullOrWhiteSpace(h.Host)).Select(h => new HostEndpoint(h.Host.Trim())).ToList();
        var dlg = new ZabbixLinkWindow(_zabbix, probe) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _zabbixHostId = dlg.ChosenHostId;
        ShowZabbix();
    }

    private void OnZabbixUnlink(object sender, RoutedEventArgs e)
    {
        _zabbixHostId = null;
        ShowZabbix();
    }

    private static void Show(UIElement el, bool visible) => el.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private void Renumber()
    {
        for (var i = 0; i < _hosts.Count; i++) _hosts[i].Role = i == 0 ? "Primary" : $"Fallback {i}";
    }

    private void OnAddHost(object sender, RoutedEventArgs e)
    {
        _hosts.Add(new HostRow { Port = _hosts.LastOrDefault()?.Port ?? Entry.DefaultPort(_e.Kind).ToString() });
        Renumber();
    }

    private void OnRemoveHost(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HostRow row) { _hosts.Remove(row); Renumber(); }
    }

    private void OnMoveUp(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HostRow row && _hosts.IndexOf(row) is > 0 and var i) { _hosts.Move(i, i - 1); Renumber(); }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            // Default the name to the first host, like a quick connect.
            NameBox.Text = _hosts.FirstOrDefault(h => h.Host.Length > 0)?.Host ?? (_e.Kind == EntryKind.Web ? UrlBox.Text : "");
            if (string.IsNullOrWhiteSpace(NameBox.Text)) { NameBox.Focus(); return; }
        }
        _e.Name = NameBox.Text.Trim();
        _e.Tags = string.IsNullOrWhiteSpace(TagsBox.Text) ? null : TagsBox.Text.Trim();
        _e.ZabbixHostId = _zabbixHostId;

        if (_e.IsConnectable)
        {
            _e.Endpoints = _hosts.Where(h => !string.IsNullOrWhiteSpace(h.Host))
                .Select(h => new HostEndpoint(h.Host.Trim(), int.TryParse(h.Port, out var p) ? p : 0)).ToList();
            var choice = CredBox.SelectedItem as CredChoice;
            _e.UseWindowsLogin = choice?.Windows == true;
            _e.CredentialId = choice?.Windows == true ? null : choice?.Id;
        }

        switch (_e.Kind)
        {
            case EntryKind.Rdp:
                _e.Rdp ??= new();
                _e.Rdp.IgnoreCertificateErrors = RdpIgnoreCert.IsChecked == true;
                _e.Rdp.SmartSizing = RdpSmart.IsChecked == true;
                _e.Rdp.RedirectClipboard = RdpClipboard.IsChecked == true;
                _e.Rdp.RedirectDrives = RdpDrives.IsChecked == true;
                _e.Rdp.CopyOnSelect = RdpCopySelect.IsChecked == true;
                _e.Rdp.AdminSession = RdpAdmin.IsChecked == true;
                _e.Rdp.GatewayHost = string.IsNullOrWhiteSpace(RdpGateway.Text) ? null : RdpGateway.Text.Trim();
                _e.Rdp.UseRdGateway = _e.Rdp.GatewayHost is not null;
                break;
            case EntryKind.Ssh:
                _e.Ssh ??= new();
                _e.Ssh.AutoAcceptHostKey = SshAutoAccept.IsChecked == true;
                _e.Ssh.AllowLegacyAlgorithms = SshLegacy.IsChecked == true;
                _e.Ssh.StartupCommand = string.IsNullOrWhiteSpace(SshStartup.Text) ? null : SshStartup.Text;
                _e.Ssh.CopyOnSelect = SshCopySelect.IsChecked == true;
                _e.Ssh.DownloadFolder = string.IsNullOrWhiteSpace(SshDownloads.Text) ? null : SshDownloads.Text.Trim();
                break;
            case EntryKind.Web:
                _e.Web ??= new();
                _e.Web.Url = WebSettings.NormalizeUrl(UrlBox.Text);
                _e.Web.Browser = (BrowserKind)(WebBrowser.SelectedItem ?? BrowserKind.Embedded);
                _e.Web.IgnoreCertificateErrors = WebIgnoreCert.IsChecked == true;
                _e.Web.AutoFill = WebAutoFill.IsChecked == true;
                _e.Web.AutoSubmit = WebAutoSubmit.IsChecked == true;
                _e.Web.UserFieldSelector = string.IsNullOrWhiteSpace(WebUserSel.Text) ? null : WebUserSel.Text.Trim();
                _e.Web.PasswordFieldSelector = string.IsNullOrWhiteSpace(WebPassSel.Text) ? null : WebPassSel.Text.Trim();
                _e.Web.SubmitSelector = string.IsNullOrWhiteSpace(WebSubmitSel.Text) ? null : WebSubmitSel.Text.Trim();
                _e.Web.CopyOnSelect = WebCopySelect.IsChecked == true;
                break;
            case EntryKind.Vnc:
                _e.Vnc ??= new();
                _e.Vnc.Viewer = (VncViewer)(VncViewerBox.SelectedItem ?? VncViewer.TigerVnc);
                _e.Vnc.ViewOnly = VncViewOnly.IsChecked == true;
                break;
            case EntryKind.Credential:
                _e.Credential ??= new();
                _e.Credential.UserName = UserBox.Text.Trim();
                _e.Credential.Domain = string.IsNullOrWhiteSpace(DomainBox.Text) ? null : DomainBox.Text.Trim();
                // Secrets are only replaced when something new was typed.
                if (PasswordBox.Password.Length > 0) _e.Credential.ProtectedPassword = _protector.Protect(PasswordBox.Password);
                if (KeyBox.Text.Trim().Length > 0) _e.Credential.ProtectedPrivateKey = _protector.Protect(KeyBox.Text.Trim() + "\n");
                if (PassphraseBox.Password.Length > 0) _e.Credential.ProtectedPassphrase = _protector.Protect(PassphraseBox.Password);
                break;
        }
        DialogResult = true;
    }
}
