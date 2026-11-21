using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RemoteMan.App.Services;
using RemoteMan.Core.Model;
using RemoteMan.Core.Networking;
using RemoteMan.Core.Security;
using RemoteMan.Core.Storage;
using RemoteMan.Protocols.Terminal;

namespace RemoteMan.App.Views;

// The Misc and Advanced tabs: loading, saving and the one-line summaries on the closed sections.
public partial class EntryEditorWindow
{
    private static readonly (string? Hex, string Name)[] TabColorChoices =
    [
        (null, "None"), ("#E5484D", "Red"), ("#F76B15", "Orange"), ("#FFC53D", "Yellow"), ("#46A758", "Green"),
        ("#12A594", "Teal"), ("#0090FF", "Blue"), ("#8E4EC6", "Purple"), ("#D6409F", "Pink"), ("#8B8D98", "Grey"),
    ];

    private static readonly string[] Sizes = ["Fit to tab", "1280 x 720", "1366 x 768", "1600 x 900", "1920 x 1080", "2560 x 1440"];

    private static void Fill(ComboBox box, IEnumerable<Opt> items, object? current)
    {
        var list = items.ToList();
        box.ItemsSource = list;
        box.SelectedItem = list.FirstOrDefault(o => Equals(o.Value, current)) ?? list[0];
    }

    private static T Val<T>(ComboBox box, T fallback) => box.SelectedItem is Opt { Value: T v } ? v : fallback;
    private static string Label(ComboBox box) => (box.SelectedItem as Opt)?.Label ?? "";
    private static string? Text(TextBox b) => string.IsNullOrWhiteSpace(b.Text) ? null : b.Text.Trim();
    private static bool On(CheckBox b) => b.IsChecked == true;

    // ---------- Tabs ----------

    private void OnTab(object sender, RoutedEventArgs e)
    {
        if (GeneralPage is null) return;
        Show(GeneralPage, TabGeneral.IsChecked == true);
        Show(MiscPage, TabMisc.IsChecked == true);
        Show(AdvancedPage, TabAdvanced.IsChecked == true);
    }

    /// <summary>Hides the Advanced tab when an entry kind has nothing there (a credential, for example).</summary>
    private void UpdateAdvancedTab()
    {
        var any = AdvancedList.Children.OfType<SettingsSection>().Any(x => x.Visibility == Visibility.Visible);
        Show(TabAdvanced, any);
        if (!any && TabAdvanced.IsChecked == true) TabGeneral.IsChecked = true;
    }

    // ---------- Misc ----------

    private void LoadMisc(Entry entry)
    {
        var connectable = entry.IsConnectable;
        Show(OpenRow, connectable);
        Show(TabColorRow, connectable);
        Show(SizeRow, entry.Kind == EntryKind.Rdp);
        Show(BrowserRow, entry.Kind == EntryKind.Web);
        Fill(OpenInBox, [new(OpenMode.Tab, "A tab"), new(OpenMode.Window, "Its own window"), new(OpenMode.FullScreen, "Full screen")], entry.OpenIn);
        DescBox.Text = entry.Description;
        FavoriteBox.IsChecked = entry.Favorite;
        Show(FavoriteBox, entry.Kind != EntryKind.Folder);

        var r = entry.Rdp ?? new RdpSettings();
        SizeBox.ItemsSource = Sizes;
        SizeBox.Text = r.DesktopWidth > 0 && r.DesktopHeight > 0 ? $"{r.DesktopWidth} x {r.DesktopHeight}" : Sizes[0];

        _tabColor = entry.TabColor;
        foreach (var (hex, name) in TabColorChoices)
        {
            var swatch = new RadioButton
            {
                GroupName = "tabcolor", ToolTip = name, Margin = new Thickness(0, 2, 6, 2), Cursor = System.Windows.Input.Cursors.Hand,
                IsChecked = string.Equals(hex, entry.TabColor, StringComparison.OrdinalIgnoreCase) || (hex is null && string.IsNullOrEmpty(entry.TabColor)),
                Template = SwatchTemplate(hex), Tag = hex,
            };
            System.Windows.Automation.AutomationProperties.SetName(swatch, name + " tab colour");
            swatch.Checked += (s, _) => _tabColor = (string?)((FrameworkElement)s).Tag;
            TabColors.Children.Add(swatch);
        }
    }

    /// <summary>A round colour dot with a ring when chosen; "None" is an empty, crossed circle.</summary>
    private static ControlTemplate SwatchTemplate(string? hex)
    {
        var t = new ControlTemplate(typeof(RadioButton));
        var ring = new FrameworkElementFactory(typeof(Border), "ring");
        ring.SetValue(Border.WidthProperty, 22.0); ring.SetValue(Border.HeightProperty, 22.0);
        ring.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
        ring.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        ring.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        ring.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var dot = new FrameworkElementFactory(typeof(Border));
        dot.SetValue(Border.WidthProperty, 14.0); dot.SetValue(Border.HeightProperty, 14.0);
        dot.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        if (hex is not null) dot.SetValue(Border.BackgroundProperty, new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)));
        else
        {
            dot.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            dot.SetResourceReference(Border.BorderBrushProperty, "Muted");
            var slash = new FrameworkElementFactory(typeof(System.Windows.Shapes.Line));
            slash.SetValue(System.Windows.Shapes.Line.X1Property, 3.0); slash.SetValue(System.Windows.Shapes.Line.Y1Property, 10.0);
            slash.SetValue(System.Windows.Shapes.Line.X2Property, 10.0); slash.SetValue(System.Windows.Shapes.Line.Y2Property, 3.0);
            slash.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.0);
            slash.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Muted");
            dot.AppendChild(slash);
        }
        ring.AppendChild(dot);
        t.VisualTree = ring;
        var chosen = new Trigger { Property = RadioButton.IsCheckedProperty, Value = true };
        chosen.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("TextStrong"), "ring"));
        t.Triggers.Add(chosen);
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("Accent"), "ring"));
        t.Triggers.Add(focus);
        return t;
    }

    private bool SaveMisc()
    {
        _e.Description = string.IsNullOrWhiteSpace(DescBox.Text) ? null : DescBox.Text.Trim();
        if (_e.Kind != EntryKind.Folder) _e.Favorite = FavoriteBox.IsChecked == true;
        if (!_e.IsConnectable) return SaveSections();
        _e.OpenIn = Val(OpenInBox, OpenMode.Tab);
        _e.TabColor = _tabColor;
        if (_e.Kind == EntryKind.Rdp)
        {
            _e.Rdp ??= new();
            var size = SizeBox.Text.Trim();
            if (size.Length == 0 || size == Sizes[0]) { _e.Rdp.DesktopWidth = 0; _e.Rdp.DesktopHeight = 0; }
            else
            {
                var parts = size.Split(new[] { 'x', 'X', '×', '*', ',' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || !int.TryParse(parts[0], out var w) || !int.TryParse(parts[1], out var h) || w < 200 || h < 200)
                {
                    TabMisc.IsChecked = true; SizeBox.Focus(); return false;
                }
                _e.Rdp.DesktopWidth = w; _e.Rdp.DesktopHeight = h;
            }
        }
        if (_e.Kind == EntryKind.Web)
        {
            _e.Web ??= new();
            _e.Web.Browser = (WebBrowser.SelectedItem as BrowserChoice)?.Kind ?? BrowserKind.Embedded;
        }
        return SaveSections();
    }

    // ---------- Advanced: which sections show ----------

    private (SettingsSection Section, InheritSections Flag)[] Inheritable =>
    [
        (SecConnection, InheritSections.Connection), (SecDisplay, InheritSections.Display), (SecResources, InheritSections.Resources),
        (SecGateway, InheritSections.Gateway), (SecTerminal, InheritSections.Terminal), (SecAuth, InheritSections.Authentication),
        (SecBrowser, InheritSections.Browser),
    ];

    private void ShowSections(Entry entry)
    {
        var k = entry.Kind;
        var folder = k == EntryKind.Folder;
        var terminal = k is EntryKind.Ssh or EntryKind.Telnet or EntryKind.Serial;
        Show(SecConnection, entry.IsConnectable || folder);
        foreach (var x in new[] { SecDisplay, SecResources, SecGateway }) Show(x, k == EntryKind.Rdp || folder);
        Show(SecRdpSecurity, k == EntryKind.Rdp);
        Show(SecTerminal, terminal || folder);
        Show(SshStartupRow, k == EntryKind.Ssh); Show(SshDownloadsRow, k == EntryKind.Ssh);
        Show(SecForwards, k == EntryKind.Ssh);
        Show(SecScript, terminal);
        Show(SecAuth, k == EntryKind.Ssh || folder);
        Show(SecJump, k == EntryKind.Ssh);
        Show(SecBrowser, k == EntryKind.Web || folder);
        Show(SecLoginForm, k == EntryKind.Web);
        Show(SecFtp, k == EntryKind.Ftp);
        Show(SecPowerShell, k == EntryKind.PowerShell);
        Show(SecVnc, k == EntryKind.Vnc);
        Show(SecFields, entry.IsConnectable);
        if (folder)
        {
            // A folder holds defaults for every kind, so say which kind each section is for.
            SecDisplay.Title = "RDP: Display"; SecResources.Title = "RDP: Local resources"; SecGateway.Title = "RDP: Gateway and tunnel";
            SecTerminal.Title = "SSH and Telnet: Terminal"; SecAuth.Title = "SSH: Authentication"; SecBrowser.Title = "Web: Browser";
            AdvancedHint.Text = "These are defaults for the entries in this folder. An entry uses them when Folder is ticked on the same section in its own Advanced tab.";
        }
        foreach (var (section, flag) in Inheritable)
        {
            section.CanInherit = entry.ParentId is not null;
            section.IsInherited = section.CanInherit && entry.Inherit.HasFlag(flag);
        }
        UpdateAdvancedTab();
    }

    // ---------- Advanced: load ----------

    private void LoadAdvanced(Entry entry, EntryStore store)
    {
        var c = entry.Connection ?? new ConnectionSettings();
        ConnWol.IsChecked = c.WakeOnLan; ConnMac.Text = c.MacAddress; ConnBroadcast.Text = c.WakeBroadcast;
        ConnWait.IsChecked = c.WaitUntilUp; ConnWaitSecs.Text = c.WaitSeconds.ToString();
        ConnReconnect.IsChecked = c.AutoReconnect;
        ConnBefore.Text = c.RunBefore; ConnBeforeWait.IsChecked = c.WaitForRunBefore; ConnAfter.Text = c.RunAfter;

        var r = entry.Rdp ?? new RdpSettings();
        Fill(RdpMonitorsBox, [new(RdpMonitors.One, "One monitor"), new(RdpMonitors.All, "All monitors"), new(RdpMonitors.Selected, "These monitors:")], r.Monitors);
        RdpMonitorList.Text = r.SelectedMonitors;
        Fill(RdpScale, new[] { 0, 100, 125, 150, 175, 200, 250, 300 }.Select(p => new Opt(p, p == 0 ? "Automatic (like this PC)" : $"{p} %")), r.ScalePercent);
        Fill(RdpColors, [new(32, "Highest quality (32 bit)"), new(24, "True colour (24 bit)"), new(16, "High colour (16 bit)"), new(15, "15 bit")], r.ColorDepth);
        Fill(RdpSpeedBox, [new(RdpSpeed.Auto, "Detect automatically"), new(RdpSpeed.Lan, "LAN (best look)"), new(RdpSpeed.Broadband, "Broadband"), new(RdpSpeed.Slow, "Slow (VPN, mobile)")], r.Speed);
        RdpSmart.IsChecked = r.SmartSizing;
        Fill(RdpAudioBox, [new(RdpAudio.Local, "Play on this computer"), new(RdpAudio.Remote, "Play on the remote computer"), new(RdpAudio.Off, "Don't play")], r.Audio);
        RdpMic.IsChecked = r.Microphone; RdpClipboard.IsChecked = r.RedirectClipboard; RdpCopySelect.IsChecked = r.CopyOnSelect;
        RdpPrinters.IsChecked = r.RedirectPrinters; RdpDrives.IsChecked = r.RedirectDrives; RdpDriveList.Text = r.Drives;
        Fill(RdpKeysBox, [new(RdpKeys.Local, "On this computer"), new(RdpKeys.Remote, "On the remote computer"), new(RdpKeys.FullScreenOnly, "Remote only in full screen")], r.WindowsKeys);
        RdpGateway.Text = r.GatewayHost;
        var creds = new List<JumpChoice> { new(null, "Same as the entry's login") };
        creds.AddRange(store.Entries.Where(x => x.Kind == EntryKind.Credential).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(x => new JumpChoice(x.Id, x.Name)));
        RdpGatewayLogin.ItemsSource = creds;
        RdpGatewayLogin.SelectedItem = creds.FirstOrDefault(x => x.Id == r.GatewayCredentialId) ?? creds[0];
        RdpGatewayBypass.IsChecked = r.GatewayBypassLocal;
        var tunnels = new List<JumpChoice> { new(null, "None (direct)") };
        tunnels.AddRange(store.Entries.Where(x => x.Kind == EntryKind.Ssh && x.Id != entry.Id).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new JumpChoice(x.Id, $"{x.Name} ({x.Endpoints.FirstOrDefault()?.ToString() ?? "no host"})")));
        RdpTunnel.ItemsSource = tunnels;
        RdpTunnel.SelectedItem = tunnels.FirstOrDefault(x => x.Id == r.SshTunnelId) ?? tunnels[0];
        RdpIgnoreCert.IsChecked = r.IgnoreCertificateErrors; RdpAdmin.IsChecked = r.AdminSession;

        var s = entry.Ssh ?? new SshSettings();
        Fill(TermTheme, new[] { new Opt("", "As in the terminal menu") }.Concat(TerminalPrefs.Themes.Select(t => new Opt(t, t))), s.Theme ?? "");
        Fill(TermFont, new[] { 0, 10, 11, 12, 13, 14, 15, 16, 18, 20, 24 }.Select(n => new Opt(n, n == 0 ? "Default" : n.ToString())), s.FontSize);
        Fill(TermScroll, new[] { 0 }.Concat(TerminalPrefs.Scrollbacks).Select(n => new Opt(n, n == 0 ? "Default" : $"{n:N0} lines")), s.Scrollback);
        Fill(TermEncoding, TerminalEncoding.Choices.Select(x => new Opt(x.Name, x.Label)), s.Encoding ?? "");
        TermEncoding.DisplayMemberPath = nameof(Opt.Label);
        TermBackspace.IsChecked = s.BackspaceCtrlH; SshCopySelect.IsChecked = s.CopyOnSelect;
        SshStartup.Text = s.StartupCommand; SshDownloads.Text = s.DownloadFolder;
        TermLog.IsChecked = s.LogSession; TermLogFolder.Text = s.LogFolder;
        foreach (var f in s.Forwards ?? []) _forwards.Add(ForwardRow.From(f));
        ForwardsList.ItemsSource = _forwards;
        foreach (var st in s.LoginScript ?? []) _steps.Add(new StepRow { WaitFor = st.WaitFor, Send = st.Send });
        ScriptList.ItemsSource = _steps;
        SshAutoAccept.IsChecked = s.AutoAcceptHostKey; SshLegacy.IsChecked = s.AllowLegacyAlgorithms;
        Fill(SshAuthOrder,
        [
            new("", "Automatic (key, then password)"),
            new("keyboard-interactive,password,publickey", "Password and 2FA code first"),
            new("password,publickey,keyboard-interactive", "Password first"),
            new("publickey", "Key only"),
            new("password,keyboard-interactive", "Password only (no key)"),
        ], s.AuthOrder ?? "");
        SshKex.Text = s.KexAlgorithms; SshCiphers.Text = s.Ciphers; SshHostKeys.Text = s.HostKeyAlgorithms;

        var w = entry.Web ?? new WebSettings();
        WebIgnoreCert.IsChecked = w.IgnoreCertificateErrors;
        Fill(WebZoom, new[] { 50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200 }.Select(z => new Opt(z, $"{z} %")), w.Zoom);
        Fill(WebProfileBox, [new(WebProfile.Shared, "Shared with the other web entries"), new(WebProfile.Own, "Own for this entry"), new(WebProfile.Private, "Private (forgotten when closed)")], w.Profile);
        WebBasicAuth.IsChecked = w.AnswerBasicAuth;
        WebKeepAlive.IsChecked = w.KeepAliveMinutes > 0; WebKeepMinutes.Text = (w.KeepAliveMinutes > 0 ? w.KeepAliveMinutes : 10).ToString();
        WebAfterLogin.Text = w.AfterLoginUrl; WebCopySelect.IsChecked = w.CopyOnSelect;

        foreach (var f in entry.Fields ?? [])
            _fields.Add(f.Secret ? new FieldRow(_protector.Unprotect) { Name = f.Name, Secret = true, Stored = f.Value }
                                 : new FieldRow(_protector.Unprotect) { Name = f.Name, Value = f.Value });
        FieldsList.ItemsSource = _fields;
        OnAnySelection(this, null!);
    }

    private void OnAnySelection(object sender, SelectionChangedEventArgs e)
    {
        if (RdpMonitorList is null || RdpMonitorsHint is null) return;
        var m = Val(RdpMonitorsBox, RdpMonitors.One);
        Show(RdpMonitorList, m == RdpMonitors.Selected);
        Show(RdpMonitorsHint, m != RdpMonitors.One);
    }

    // ---------- Advanced: save ----------

    private bool SaveSections()
    {
        if (!_e.IsConnectable && _e.Kind != EntryKind.Folder) return true;
        if (ConnWol.IsChecked == true && WakeOnLan.ParseMac(ConnMac.Text ?? "") is null)
        {
            TabAdvanced.IsChecked = true; SecConnection.IsOpen = true; ConnMac.Focus(); return false;
        }
        var c = new ConnectionSettings
        {
            WakeOnLan = On(ConnWol), MacAddress = Text(ConnMac), WakeBroadcast = Text(ConnBroadcast),
            WaitUntilUp = On(ConnWait), WaitSeconds = int.TryParse(ConnWaitSecs.Text, out var ws) && ws > 0 ? Math.Min(ws, 3600) : 120,
            AutoReconnect = On(ConnReconnect), RunBefore = Text(ConnBefore), WaitForRunBefore = On(ConnBeforeWait), RunAfter = Text(ConnAfter),
        };
        var unchanged = !c.WakeOnLan && c.MacAddress is null && !c.WaitUntilUp && c.AutoReconnect && c.RunBefore is null && c.RunAfter is null;
        _e.Connection = unchanged ? null : c;

        var inherit = InheritSections.None;
        foreach (var (section, flag) in Inheritable)
            if (section.Visibility == Visibility.Visible && section.CanInherit && section.IsInherited) inherit |= flag;
        _e.Inherit = inherit;

        if (_e.IsConnectable)
        {
            var fields = new List<CustomField>();
            foreach (var f in _fields.Where(f => f.Name.Trim().Length > 0))
            {
                string? value;
                if (!f.Secret) value = string.IsNullOrEmpty(f.Value) ? null : f.Value;
                else if (f.Stored is not null && !f.Revealed) value = f.Stored;   // untouched: keep the encrypted value
                else value = string.IsNullOrEmpty(f.Value) ? null : _protector.Protect(f.Value);
                fields.Add(new CustomField { Name = f.Name.Trim(), Value = value, Secret = f.Secret });
            }
            _e.Fields = fields.Count > 0 ? fields : null;
        }
        return true;
    }

    private void SaveDisplay(RdpSettings r)
    {
        r.Monitors = Val(RdpMonitorsBox, RdpMonitors.One);
        r.SelectedMonitors = r.Monitors == RdpMonitors.Selected ? Text(RdpMonitorList) : null;
        r.ScalePercent = Val(RdpScale, 0);
        r.ColorDepth = Val(RdpColors, 32);
        r.Speed = Val(RdpSpeedBox, RdpSpeed.Auto);
        r.SmartSizing = On(RdpSmart);
    }

    private void SaveResources(RdpSettings r)
    {
        r.Audio = Val(RdpAudioBox, RdpAudio.Local);
        r.Microphone = On(RdpMic);
        r.RedirectClipboard = On(RdpClipboard);
        r.CopyOnSelect = On(RdpCopySelect);
        r.RedirectPrinters = On(RdpPrinters);
        r.RedirectDrives = On(RdpDrives);
        r.Drives = r.RedirectDrives ? Text(RdpDriveList)?.ToUpperInvariant() : null;
        r.WindowsKeys = Val(RdpKeysBox, RdpKeys.FullScreenOnly);
    }

    private void SaveGateway(RdpSettings r)
    {
        r.GatewayHost = Text(RdpGateway);
        r.UseRdGateway = r.GatewayHost is not null;
        r.GatewayCredentialId = (RdpGatewayLogin.SelectedItem as JumpChoice)?.Id;
        r.GatewayBypassLocal = On(RdpGatewayBypass);
        r.SshTunnelId = (RdpTunnel.SelectedItem as JumpChoice)?.Id;
    }

    private void SaveTerminal(SshSettings s)
    {
        s.Theme = Val(TermTheme, "") is { Length: > 0 } t ? t : null;
        s.FontSize = Val(TermFont, 0);
        s.Scrollback = Val(TermScroll, 0);
        s.Encoding = Val(TermEncoding, "") is { Length: > 0 } enc ? enc : null;
        s.BackspaceCtrlH = On(TermBackspace);
        s.CopyOnSelect = On(SshCopySelect);
        s.LogSession = On(TermLog);
        s.LogFolder = Text(TermLogFolder);
    }

    private void SaveAuth(SshSettings s)
    {
        s.AutoAcceptHostKey = On(SshAutoAccept);
        s.AllowLegacyAlgorithms = On(SshLegacy);
        s.AuthOrder = Val(SshAuthOrder, "") is { Length: > 0 } o ? o : null;
        s.KexAlgorithms = Text(SshKex); s.Ciphers = Text(SshCiphers); s.HostKeyAlgorithms = Text(SshHostKeys);
    }

    private void SaveScript(SshSettings s)
    {
        var steps = _steps.Where(x => !string.IsNullOrEmpty(x.Send) || !string.IsNullOrWhiteSpace(x.WaitFor))
            .Select(x => new ScriptStep { WaitFor = string.IsNullOrWhiteSpace(x.WaitFor) ? null : x.WaitFor, Send = x.Send ?? "" }).ToList();
        s.LoginScript = steps.Count > 0 ? steps : null;
    }

    private void SaveBrowser(WebSettings w)
    {
        w.IgnoreCertificateErrors = On(WebIgnoreCert);
        w.Zoom = Val(WebZoom, 100);
        w.Profile = Val(WebProfileBox, WebProfile.Shared);
        w.AnswerBasicAuth = On(WebBasicAuth);
        w.KeepAliveMinutes = On(WebKeepAlive) && int.TryParse(WebKeepMinutes.Text, out var m) && m > 0 ? Math.Min(m, 240) : 0;
        w.AfterLoginUrl = Text(WebAfterLogin);
        w.CopyOnSelect = On(WebCopySelect);
    }

    // ---------- Lists ----------

    private void OnAddForward(object sender, RoutedEventArgs e) => _forwards.Add(new ForwardRow());
    private void OnRemoveForward(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is ForwardRow r) _forwards.Remove(r); }
    private void OnAddStep(object sender, RoutedEventArgs e) => _steps.Add(new StepRow());
    private void OnRemoveStep(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is StepRow r) _steps.Remove(r); }

    private void OnCiscoExample(object sender, RoutedEventArgs e)
    {
        _steps.Add(new StepRow { WaitFor = ">", Send = "enable" });
        _steps.Add(new StepRow { WaitFor = "Password:", Send = "{secret:enable}" });
        _steps.Add(new StepRow { WaitFor = "#", Send = "terminal length 0" });
        if (!_fields.Any(f => f.Name.Equals("enable", StringComparison.OrdinalIgnoreCase)))
        {
            _fields.Add(new FieldRow(_protector.Unprotect) { Name = "enable", Secret = true });
            SecFields.IsOpen = true;
        }
    }

    private void OnAddField(object sender, RoutedEventArgs e) => _fields.Add(new FieldRow(_protector.Unprotect));
    private void OnRemoveField(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is FieldRow r) _fields.Remove(r); }
    private void OnRevealField(object sender, RoutedEventArgs e) { if ((sender as FrameworkElement)?.DataContext is FieldRow r) r.Reveal(); }

    // ---------- Two-factor code ----------

    private string? StoredTotp()
    {
        var holder = _e.Kind == EntryKind.Credential ? _e : _e.Credential is null ? null : _e;
        if (holder?.Credential?.ProtectedTotpSecret is not { } p) return null;
        try { return _protector.Unprotect(p); } catch (Exception) { return null; }
    }

    private string? CurrentTotpSecret() => TotpBox.Password.Trim() is { Length: > 0 } typed ? typed : StoredTotp();

    private void OnTotpChanged(object sender, RoutedEventArgs e) => UpdateTotp();

    private void UpdateTotp()
    {
        if (TotpNow is null) return;
        var secret = CurrentTotpSecret();
        if (secret is null) { TotpNow.Text = ""; TotpCopy.IsEnabled = false; return; }
        if (!Totp.IsValid(secret)) { TotpNow.Text = "not a valid secret"; TotpCopy.IsEnabled = false; return; }
        TotpNow.Text = $"{Totp.Code(secret)}  {Totp.SecondsLeft(secret)}s";
        TotpCopy.IsEnabled = true;
    }

    private void OnCopyTotp(object sender, RoutedEventArgs e)
    {
        if (CurrentTotpSecret() is { } secret && Totp.IsValid(secret)) Clipboard.SetText(Totp.Code(secret));
    }

    // ---------- Summaries on the closed sections ----------

    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private void UpdateSummaries()
    {
        if (!_loaded) return;
        SecConnection.Summary = Join(
            On(ConnWol) ? "Wake-on-LAN" : null,
            On(ConnWait) ? $"waits {ConnWaitSecs.Text.Trim()} s" : null,
            On(ConnReconnect) ? "reconnects" : "no reconnect",
            Text(ConnBefore) is not null ? "runs a command first" : null,
            Text(ConnAfter) is not null ? "runs a command after" : null);
        var mon = Val(RdpMonitorsBox, RdpMonitors.One);
        SecDisplay.Summary = Join(mon == RdpMonitors.One ? "one monitor" : mon == RdpMonitors.All ? "all monitors" : $"monitors {RdpMonitorList.Text.Trim()}",
            Val(RdpScale, 0) is > 0 and var sc ? $"{sc} %" : null, Val(RdpColors, 32) != 32 ? $"{Val(RdpColors, 32)} bit" : null,
            Val(RdpSpeedBox, RdpSpeed.Auto) != RdpSpeed.Auto ? Label(RdpSpeedBox).ToLowerInvariant() : null, On(RdpSmart) ? "fits the tab" : null);
        SecResources.Summary = Join(
            Val(RdpAudioBox, RdpAudio.Local) switch { RdpAudio.Remote => "sound on remote", RdpAudio.Off => "no sound", _ => "sound here" },
            On(RdpMic) ? "microphone" : null, On(RdpClipboard) ? "clipboard" : "no clipboard", On(RdpPrinters) ? "printers" : null,
            On(RdpDrives) ? (Text(RdpDriveList) is { } d ? $"drives {d.ToUpperInvariant()}" : "all drives") : null);
        SecGateway.Summary = Join(Text(RdpGateway) is { } gw ? $"via {gw}" : null,
            (RdpTunnel.SelectedItem as JumpChoice) is { Id: not null } t ? $"SSH tunnel through {t.Name}" : null) is { Length: > 0 } g ? g : "direct";
        SecRdpSecurity.Summary = Join(On(RdpIgnoreCert) ? "skips certificate warnings" : null, On(RdpAdmin) ? "admin session" : null);
        SecTerminal.Summary = Join(Val(TermTheme, "") is { Length: > 0 } th ? th : null, Val(TermFont, 0) is > 0 and var fs ? $"{fs} pt" : null,
            Val(TermEncoding, "") is { Length: > 0 } ? Label(TermEncoding).Split(' ')[0] : null, On(TermBackspace) ? "^H" : null,
            On(TermLog) ? "logs to file" : null) is { Length: > 0 } ts ? ts : "defaults";
        SecForwards.Summary = _forwards.Count == 0 ? "none" : string.Join(", ", _forwards.Select(f => f.ToForward()?.ToString()).Where(x => x is not null).Take(3))
            + (_forwards.Count > 3 ? $" +{_forwards.Count - 3}" : "");
        SecScript.Summary = _steps.Count == 0 ? "none" : $"{_steps.Count} step{(_steps.Count == 1 ? "" : "s")}";
        SecAuth.Summary = Join(Val(SshAuthOrder, "") is { Length: > 0 } ? Label(SshAuthOrder).ToLowerInvariant() : null,
            On(SshLegacy) ? "legacy allowed" : null, (Text(SshKex) ?? Text(SshCiphers) ?? Text(SshHostKeys)) is not null ? "fixed algorithms" : null,
            On(SshAutoAccept) ? "accepts new host keys" : null);
        SecJump.Summary = (JumpBox.SelectedItem as JumpChoice) is { Id: not null } j ? j.Name : "direct";
        SecBrowser.Summary = Join(Val(WebZoom, 100) != 100 ? $"{Val(WebZoom, 100)} %" : null,
            Val(WebProfileBox, WebProfile.Shared) switch { WebProfile.Own => "own cookies", WebProfile.Private => "private", _ => null },
            On(WebKeepAlive) ? $"reloads every {WebKeepMinutes.Text.Trim()} min" : null, Text(WebAfterLogin) is not null ? "opens a page after login" : null,
            On(WebIgnoreCert) ? "ignores certificate errors" : null) is { Length: > 0 } bs ? bs : "defaults";
        SecLoginForm.Summary = !On(WebAutoFill) ? "off" : Join(WebPreset.SelectedItem as string, On(WebAutoSubmit) ? "logs in" : "fills only", On(WebFillTotp) ? "2FA code" : null);
        SecFtp.Summary = Join(On(FtpTls) ? "TLS" : "plain FTP", Text(FtpStart));
        SecPowerShell.Summary = Join(On(PsSsl) ? "HTTPS" : "HTTP", Text(PsConfig));
        SecVnc.Summary = Join(VncViewerBox.SelectedItem?.ToString(), On(VncViewOnly) ? "view only" : null);
        SecFields.Summary = _fields.Count == 0 ? "none" : string.Join(", ", _fields.Select(f => f.Name).Where(n => n.Length > 0).Take(4));
        foreach (var (section, _) in Inheritable)
            if (section.CanInherit && section.IsInherited) section.Summary = "from the folder";
    }
}
