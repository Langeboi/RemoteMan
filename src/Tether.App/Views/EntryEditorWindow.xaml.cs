using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Tether.Core.Model;
using Tether.Core.Security;
using Tether.Core.Storage;

namespace Tether.App.Views;

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
    private sealed record CredChoice(Guid? Id, string Name);

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
        creds.AddRange(store.Entries.Where(x => x.Kind == EntryKind.Credential).OrderBy(x => x.Name).Select(x => new CredChoice(x.Id, x.Name)));
        CredBox.ItemsSource = creds;
        CredBox.SelectedItem = creds.FirstOrDefault(c => c.Id == entry.CredentialId) ?? creds[0];

        if (entry.Credential is { } c)
        {
            UserBox.Text = c.UserName;
            DomainBox.Text = c.Domain;
        }

        var r = entry.Rdp ?? new RdpSettings();
        RdpIgnoreCert.IsChecked = r.IgnoreCertificateErrors; RdpSmart.IsChecked = r.SmartSizing; RdpClipboard.IsChecked = r.RedirectClipboard;
        RdpDrives.IsChecked = r.RedirectDrives; RdpAdmin.IsChecked = r.AdminSession; RdpGateway.Text = r.GatewayHost;

        var s = entry.Ssh ?? new SshSettings();
        SshAutoAccept.IsChecked = s.AutoAcceptHostKey; SshLegacy.IsChecked = s.AllowLegacyAlgorithms; SshStartup.Text = s.StartupCommand;

        var w = entry.Web ?? new WebSettings();
        UrlBox.Text = w.Url;
        WebBrowser.ItemsSource = Enum.GetValues<BrowserKind>(); WebBrowser.SelectedItem = w.Browser;
        WebIgnoreCert.IsChecked = w.IgnoreCertificateErrors; WebAutoFill.IsChecked = w.AutoFill;

        var v = entry.Vnc ?? new VncSettings();
        VncViewerBox.ItemsSource = Enum.GetValues<VncViewer>(); VncViewerBox.SelectedItem = v.Viewer; VncViewOnly.IsChecked = v.ViewOnly;

        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
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

        if (_e.IsConnectable)
        {
            _e.Endpoints = _hosts.Where(h => !string.IsNullOrWhiteSpace(h.Host))
                .Select(h => new HostEndpoint(h.Host.Trim(), int.TryParse(h.Port, out var p) ? p : 0)).ToList();
            _e.CredentialId = (CredBox.SelectedItem as CredChoice)?.Id;
        }

        switch (_e.Kind)
        {
            case EntryKind.Rdp:
                _e.Rdp ??= new();
                _e.Rdp.IgnoreCertificateErrors = RdpIgnoreCert.IsChecked == true;
                _e.Rdp.SmartSizing = RdpSmart.IsChecked == true;
                _e.Rdp.RedirectClipboard = RdpClipboard.IsChecked == true;
                _e.Rdp.RedirectDrives = RdpDrives.IsChecked == true;
                _e.Rdp.AdminSession = RdpAdmin.IsChecked == true;
                _e.Rdp.GatewayHost = string.IsNullOrWhiteSpace(RdpGateway.Text) ? null : RdpGateway.Text.Trim();
                _e.Rdp.UseRdGateway = _e.Rdp.GatewayHost is not null;
                break;
            case EntryKind.Ssh:
                _e.Ssh ??= new();
                _e.Ssh.AutoAcceptHostKey = SshAutoAccept.IsChecked == true;
                _e.Ssh.AllowLegacyAlgorithms = SshLegacy.IsChecked == true;
                _e.Ssh.StartupCommand = string.IsNullOrWhiteSpace(SshStartup.Text) ? null : SshStartup.Text;
                break;
            case EntryKind.Web:
                _e.Web ??= new();
                _e.Web.Url = UrlBox.Text.Trim();
                _e.Web.Browser = (BrowserKind)(WebBrowser.SelectedItem ?? BrowserKind.Embedded);
                _e.Web.IgnoreCertificateErrors = WebIgnoreCert.IsChecked == true;
                _e.Web.AutoFill = WebAutoFill.IsChecked == true;
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
