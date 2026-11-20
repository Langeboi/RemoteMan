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
    private readonly EntryStore _store;
    private sealed record CredChoice(Guid Id, string Name);
    private sealed record JumpChoice(Guid? Id, string Name);
    private sealed record LoginChoice(LoginSource Source, string Name);
    /// <summary>Set on OK when "Save as a reusable credential too" was ticked: the caller saves it first.</summary>
    public Entry? NewCredential { get; private set; }
    private string? _certBase64;
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
        _store = store;
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
        var hasLogin = connectable || kind == EntryKind.Folder;
        Show(CredLabel, hasLogin); Show(LoginBox, hasLogin);
        Show(RdpGroup, kind == EntryKind.Rdp);
        Show(SshGroup, kind == EntryKind.Ssh);
        Show(WebGroup, kind == EntryKind.Web);
        Show(VncGroup, kind == EntryKind.Vnc);
        if (kind == EntryKind.Web) HostsGroup.Header = "Fallback hosts (optional; the URL's host is used when empty)";

        foreach (var h in entry.Endpoints) _hosts.Add(new HostRow { Host = h.Host, Port = h.Port > 0 ? h.Port.ToString() : "" });
        if (_hosts.Count == 0 && kind != EntryKind.Web) _hosts.Add(new HostRow { Port = Entry.DefaultPort(kind).ToString() });
        HostsList.ItemsSource = _hosts;
        Renumber();

        // Login source, like RDM: none, inherited from the folder, a saved credential, SSO, or typed in here.
        var creds = store.Entries.Where(x => x.Kind == EntryKind.Credential).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new CredChoice(x.Id, x.Name)).ToList();
        CredBox.ItemsSource = creds;
        CredBox.SelectedItem = creds.FirstOrDefault(c => c.Id == entry.CredentialId) ?? creds.FirstOrDefault();
        var parentLogin = entry.ParentId is { } pid && store.Get(pid) is { } parent ? Describe(store.ResolveLogin(parent)) : null;
        var logins = new List<LoginChoice>
        {
            new(LoginSource.None, kind == EntryKind.Folder ? "None" : "None (ask when connecting)"),
            new(LoginSource.Inherited, parentLogin is null ? "Inherited from folder" : $"Inherited from folder ({parentLogin})"),
            new(LoginSource.Saved, "Saved credential"),
        };
        // Single sign-on: RDP (CredSSP default credentials) and web (Kerberos/NTLM, Entra ID); folders pass it on.
        if (kind is EntryKind.Rdp or EntryKind.Web or EntryKind.Folder || entry.Login == LoginSource.WindowsLogin)
            logins.Add(new(LoginSource.WindowsLogin, "My Windows login (SSO)"));
        logins.Add(new(LoginSource.Embedded, "Typed in this entry"));
        LoginBox.ItemsSource = logins;
        LoginBox.SelectedItem = logins.FirstOrDefault(l => l.Source == entry.Login) ?? logins[0];

        var c = entry.Credential ?? new CredentialData { Type = kind == EntryKind.Ssh ? CredentialType.SshKey : CredentialType.Password };
        UserBox.Text = c.UserName;
        DomainBox.Text = c.Domain;
        KeyPathBox.Text = c.PrivateKeyPath;
        KeyFromDisk.IsChecked = !string.IsNullOrWhiteSpace(c.PrivateKeyPath);
        AskPasswordBox.IsChecked = c.AskPassword;
        NotesBox.Text = c.Notes;
        CertBox.Text = c.ProtectedCertificate is null ? "" : "(stored)";
        (c.Type switch { CredentialType.SshKey => TypeKey, CredentialType.Certificate => TypeCert, CredentialType.Secret => TypeSecret, _ => TypePassword }).IsChecked = true;
        Show(SaveAsCredential, kind != EntryKind.Credential);
        OnKeyFromDisk(this, new RoutedEventArgs());
        UpdateLoginUi();

        var r = entry.Rdp ?? new RdpSettings();
        RdpIgnoreCert.IsChecked = r.IgnoreCertificateErrors; RdpSmart.IsChecked = r.SmartSizing; RdpClipboard.IsChecked = r.RedirectClipboard;
        RdpDrives.IsChecked = r.RedirectDrives; RdpCopySelect.IsChecked = r.CopyOnSelect; RdpAdmin.IsChecked = r.AdminSession; RdpGateway.Text = r.GatewayHost;

        var s = entry.Ssh ?? new SshSettings();
        SshAutoAccept.IsChecked = s.AutoAcceptHostKey; SshLegacy.IsChecked = s.AllowLegacyAlgorithms; SshStartup.Text = s.StartupCommand;
        SshCopySelect.IsChecked = s.CopyOnSelect; SshDownloads.Text = s.DownloadFolder;
        var jumps = new List<JumpChoice> { new(null, "None (direct)") };
        jumps.AddRange(store.Entries.Where(x => x.Kind == EntryKind.Ssh && x.Id != entry.Id).OrderBy(x => x.Name)
            .Select(x => new JumpChoice(x.Id, $"{x.Name} ({x.Endpoints.FirstOrDefault()?.ToString() ?? "no host"})")));
        JumpBox.ItemsSource = jumps;
        JumpBox.SelectedItem = jumps.FirstOrDefault(j => j.Id == s.JumpHostId) ?? jumps[0];

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

    /// <summary>Shows the route, e.g. "this PC → bastion → pve-01", and warns about loops.</summary>
    private void OnJumpChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_store is null) return;
        var names = new List<string>();
        var seen = new HashSet<Guid> { _e.Id };
        var next = (JumpBox.SelectedItem as JumpChoice)?.Id;
        while (next is { } id)
        {
            if (!seen.Add(id)) { JumpPath.Text = "These jump hosts point at each other in a loop."; return; }
            var hop = _store.Get(id);
            if (hop is null) break;
            names.Insert(0, hop.Name);
            next = hop.Ssh?.JumpHostId;
        }
        var target = string.IsNullOrWhiteSpace(NameBox.Text) ? "this entry" : NameBox.Text.Trim();
        JumpPath.Text = names.Count == 0 ? "" : $"this PC → {string.Join(" → ", names)} → {target}";
    }

    private static void Show(UIElement el, bool visible) => el.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private void Renumber()
    {
        for (var i = 0; i < _hosts.Count; i++) _hosts[i].Role = i == 0 ? "Primary" : $"Fallback {i}";
    }

    private static string? Describe((LoginSource Source, Entry? Holder) r) => r.Source switch
    {
        LoginSource.WindowsLogin => "Windows login",
        LoginSource.Saved => r.Holder?.Name,
        LoginSource.Embedded => r.Holder?.Credential?.UserName is { Length: > 0 } u ? u : "typed in",
        _ => null,
    };

    private CredentialType SelectedType =>
        TypeKey.IsChecked == true ? CredentialType.SshKey :
        TypeCert.IsChecked == true ? CredentialType.Certificate :
        TypeSecret.IsChecked == true ? CredentialType.Secret : CredentialType.Password;

    private LoginSource SelectedLogin => (LoginBox.SelectedItem as LoginChoice)?.Source ?? LoginSource.None;

    private void OnLoginChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateLoginUi();
    private void OnCredTypeChanged(object sender, RoutedEventArgs e) => UpdateLoginUi();

    private void UpdateLoginUi()
    {
        if (!IsInitialized || LoginBox is null) return;
        var isCred = _e.Kind == EntryKind.Credential;
        var login = SelectedLogin;
        Show(CredBox, !isCred && login == LoginSource.Saved);
        Show(CredGroup, isCred || login == LoginSource.Embedded);
        LoginHint.Text = isCred ? "" : login switch
        {
            LoginSource.None => _e.Kind == EntryKind.Folder ? "" : "Windows or the server asks for the user name and password when you connect.",
            LoginSource.Inherited => "Uses the folder's login. Change it once on the folder and every entry in it follows.",
            LoginSource.Saved => CredBox.Items.Count == 0 ? "No saved credentials yet. Create one with New > Credential." : "Same as Ctrl+click on a credential in the tree.",
            LoginSource.WindowsLogin => "Signs in as whoever runs RemoteMan. Nothing is stored.",
            _ => "",
        };
        Show(LoginHint, LoginHint.Text.Length > 0);

        var t = SelectedType;
        Show(UserRow, t is CredentialType.Password or CredentialType.SshKey);
        Show(DomainRow, t == CredentialType.Password);
        Show(KeyRow, t == CredentialType.SshKey);
        Show(PassphraseRow, t == CredentialType.SshKey);
        Show(CertRow, t == CredentialType.Certificate);
        Show(CertInfo, t == CredentialType.Certificate && CertInfo.Text.Length > 0);
        Show(PasswordRow, t is CredentialType.Password or CredentialType.Certificate or CredentialType.Secret);
        PasswordLabel.Text = t switch { CredentialType.Secret => "Secret", CredentialType.Certificate => "Password", _ => "Password" };
        Show(AskPasswordBox, t == CredentialType.Password);
        Show(NotesRow, t == CredentialType.Secret || isCred);
    }

    private void OnKeyFromDisk(object sender, RoutedEventArgs e)
    {
        if (KeyPathRow is null) return;
        var disk = KeyFromDisk.IsChecked == true;
        Show(KeyPathRow, disk);
        KeyBox.IsEnabled = !disk;
    }

    private void OnBrowseKeyPath(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "SSH private key", Filter = "Private keys|*.*",
            InitialDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh") };
        if (dlg.ShowDialog(this) == true) KeyPathBox.Text = dlg.FileName;
    }

    private void OnImportKey(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Import SSH private key", Filter = "Private keys|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        KeyBox.Text = System.IO.File.ReadAllText(dlg.FileName);
        KeyFromDisk.IsChecked = false;
    }

    private void OnGenerateKey(object sender, RoutedEventArgs e)
    {
        var (priv, pub) = RemoteMan.Protocols.Ssh.SshKeys.GenerateRsa($"{Environment.UserName}@remoteman");
        KeyBox.Text = priv;
        KeyFromDisk.IsChecked = false;
        Clipboard.SetText(pub);
        ChoiceDialog.Show(this, "New key created", "The public key is on the clipboard. Add it to ~/.ssh/authorized_keys on the server, then press OK here to save the entry.\n\n" + pub, ["OK"]);
    }

    private void OnCopyPublicKey(object sender, RoutedEventArgs e)
    {
        try
        {
            var key = KeyFromDisk.IsChecked == true ? System.IO.File.ReadAllText(Environment.ExpandEnvironmentVariables(KeyPathBox.Text))
                : KeyBox.Text.Trim().Length > 0 ? KeyBox.Text
                : _e.Credential?.ProtectedPrivateKey is { } stored ? _protector.Unprotect(stored) : null;
            if (key is null) { ChoiceDialog.Show(this, "No key yet", "Paste, import or generate a key first.", ["OK"]); return; }
            var pass = PassphraseBox.Password.Length > 0 ? PassphraseBox.Password
                : _e.Credential?.ProtectedPassphrase is { } pp ? _protector.Unprotect(pp) : null;
            Clipboard.SetText(RemoteMan.Protocols.Ssh.SshKeys.PublicKeyLine(key, pass, UserBox.Text.Trim().Length > 0 ? UserBox.Text.Trim() : "remoteman"));
            Heading.Text = "Public key copied";
        }
        catch (Exception ex) { ChoiceDialog.Show(this, "Couldn't read the key", ex.Message, ["OK"]); }
    }

    private void OnBrowseCert(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Client certificate", Filter = "Certificates (*.pfx;*.p12)|*.pfx;*.p12|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        var bytes = System.IO.File.ReadAllBytes(dlg.FileName);
        _certBase64 = Convert.ToBase64String(bytes);
        CertBox.Text = System.IO.Path.GetFileName(dlg.FileName);
        try
        {
            using var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(bytes, PasswordBox.Password.Length > 0 ? PasswordBox.Password : null);
            CertInfo.Text = $"Issued to {cert.Subject}, expires {cert.NotAfter:yyyy-MM-dd}";
        }
        catch { CertInfo.Text = "Enter the certificate's password to check it."; }
        UpdateLoginUi();
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
        }

        if (_e.IsConnectable || _e.Kind == EntryKind.Folder)
        {
            _e.Login = SelectedLogin;
            if (_e.Login == LoginSource.Saved)
            {
                if (CredBox.SelectedItem is not CredChoice cc) { CredBox.Focus(); return; }
                _e.CredentialId = cc.Id;
            }
            else _e.CredentialId = null;
            if (_e.Login == LoginSource.Embedded)
            {
                _e.Credential ??= new();
                ReadCredential(_e.Credential);
                if (SaveAsCredential.IsChecked == true)
                {
                    // Also keep it as its own credential entry, and link to that instead.
                    NewCredential = new Entry { Kind = EntryKind.Credential, ParentId = _e.ParentId, Name = DisplayName(_e.Credential, _e.Name), Credential = _e.Credential };
                    _e.Credential = null;
                    _e.Login = LoginSource.Saved;
                    _e.CredentialId = NewCredential.Id;
                }
            }
            else _e.Credential = null;
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
                if (JumpPath.Text.Contains("loop")) { JumpBox.Focus(); return; }
                _e.Ssh ??= new();
                _e.Ssh.AutoAcceptHostKey = SshAutoAccept.IsChecked == true;
                _e.Ssh.AllowLegacyAlgorithms = SshLegacy.IsChecked == true;
                _e.Ssh.StartupCommand = string.IsNullOrWhiteSpace(SshStartup.Text) ? null : SshStartup.Text;
                _e.Ssh.CopyOnSelect = SshCopySelect.IsChecked == true;
                _e.Ssh.DownloadFolder = string.IsNullOrWhiteSpace(SshDownloads.Text) ? null : SshDownloads.Text.Trim();
                _e.Ssh.JumpHostId = (JumpBox.SelectedItem as JumpChoice)?.Id;
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
                ReadCredential(_e.Credential);
                break;
        }
        DialogResult = true;
    }

    private static string DisplayName(CredentialData c, string fallback) =>
        c.UserName is { Length: > 0 } u ? (c.Domain is { Length: > 0 } d ? $"{d}\\{u}" : u) : fallback + " login";

    /// <summary>Copies the form into <paramref name="c"/>. Secrets are only replaced when something new was entered.</summary>
    private void ReadCredential(CredentialData c)
    {
        var t = SelectedType;
        if (c.Type != t)
        {
            // Changing the type drops what belonged to the old one.
            c.ProtectedPassword = null; c.ProtectedPrivateKey = null; c.ProtectedPassphrase = null; c.ProtectedCertificate = null; c.PrivateKeyPath = null;
        }
        c.Type = t;
        c.UserName = t is CredentialType.Password or CredentialType.SshKey && UserBox.Text.Trim().Length > 0 ? UserBox.Text.Trim() : null;
        c.Domain = t == CredentialType.Password && !string.IsNullOrWhiteSpace(DomainBox.Text) ? DomainBox.Text.Trim() : null;
        c.Notes = string.IsNullOrWhiteSpace(NotesBox.Text) ? null : NotesBox.Text;
        c.AskPassword = t == CredentialType.Password && AskPasswordBox.IsChecked == true;
        if (c.AskPassword) c.ProtectedPassword = null;
        else if (PasswordBox.Password.Length > 0) c.ProtectedPassword = _protector.Protect(PasswordBox.Password);
        if (t == CredentialType.SshKey)
        {
            if (KeyFromDisk.IsChecked == true) { c.PrivateKeyPath = KeyPathBox.Text.Trim(); c.ProtectedPrivateKey = null; }
            else
            {
                c.PrivateKeyPath = null;
                if (KeyBox.Text.Trim().Length > 0) c.ProtectedPrivateKey = _protector.Protect(KeyBox.Text.Trim() + "\n");
            }
            if (PassphraseBox.Password.Length > 0) c.ProtectedPassphrase = _protector.Protect(PassphraseBox.Password);
        }
        if (t == CredentialType.Certificate && _certBase64 is not null) c.ProtectedCertificate = _protector.Protect(_certBase64);
    }
}
