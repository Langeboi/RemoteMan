using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RemoteMan.App.Services;
using RemoteMan.Core.Ai;
using RemoteMan.Core.Storage;
using RemoteMan.Data;

namespace RemoteMan.App.Views;

/// <summary>
/// First-run setup: data source, look, AI assistant, Zabbix/Oxidized, import and startup, one step at a time.
/// Every step can be skipped (it then keeps the default, or the current setting when run again from More), and
/// "Skip setup" or closing the window keeps only the steps already confirmed with Next. Steps that earlier choices make
/// unnecessary are skipped on their own: a shared database already holds the team's entries, so Import is passed over.
/// Nothing is written until the window closes.
/// </summary>
public sealed class SetupWindow : Window
{
    private enum Step { Welcome, DataSource, Look, Ai, Integrations, Import, Startup, Done }
    private enum State { None, Done, Skipped }

    private static readonly (Step Step, string Title, string Sub)[] Steps =
    [
        (Step.Welcome, "Welcome", ""),
        (Step.DataSource, "Data source", "Where entries live"),
        (Step.Look, "Look", "Theme and shading"),
        (Step.Ai, "AI assistant", "Optional"),
        (Step.Integrations, "Integrations", "Zabbix, Oxidized"),
        (Step.Import, "Import", "From RDM and others"),
        (Step.Startup, "Startup", "Reopen sessions"),
        (Step.Done, "Done", ""),
    ];

    private readonly AppSettings _settings;
    private readonly bool _rerun;
    private readonly Dictionary<Step, State> _state = Steps.ToDictionary(s => s.Step, _ => State.None);
    private readonly Dictionary<Step, FrameworkElement> _pages = new();
    private int _cur;
    private bool _applied;

    private readonly StackPanel _rail = new();
    private readonly ContentControl _content = new();
    private readonly Button _back, _skip, _next, _skipAll;

    // Data source
    private DataSourceKind _dsKind = DataSourceKind.Sqlite;
    private readonly Dictionary<DataSourceKind, Border> _dsCards = new();
    private readonly StackPanel _dsShared = new();
    private readonly TextBlock _dsLocalNote = Muted("");
    private readonly TextBox _dsName = new() { Text = "Team" };
    private readonly TextBox _dsServers = new() { AcceptsReturn = true, Height = 64, TextWrapping = TextWrapping.NoWrap, VerticalContentAlignment = VerticalAlignment.Top };
    private readonly PasswordBox _dsVault = new();
    private readonly CheckBox _dsSso = new() { Content = "Use my Windows login (SSO)" };
    private readonly TextBlock _dsHint = Muted("");
    private readonly TextBlock _dsResult = Muted("");

    // Look
    private string _theme;
    private Shading _shading;
    private readonly Dictionary<string, Border> _themeCards = new();
    private readonly Dictionary<Shading, Button> _shadingButtons = new();
    private readonly Border _previewHost = new() { Width = 300, CornerRadius = new CornerRadius(4), ClipToBounds = true, BorderThickness = new Thickness(1) };
    private readonly TextBlock _previewName = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _previewDesc = Muted("");

    // AI
    private AiProviderKind? _ai;
    private readonly Dictionary<AiProviderKind, Border> _aiCards = new();
    private readonly StackPanel _aiForm = new();
    private readonly TextBlock _aiKeyLabel = Muted("API key");
    private readonly PasswordBox _aiKey = new();
    private readonly TextBlock _aiEndpointLabel = Muted("Server address");
    private readonly TextBox _aiEndpoint = new();
    private readonly TextBox _aiModel = new();
    private readonly CheckBox _aiContext = new() { Content = "Send the active session's recent output with each message", IsChecked = true };
    private readonly TextBlock _aiResult = Muted("Not using AI? Press Skip; the assistant stays hidden until you set it up.");

    // Integrations
    private readonly CheckBox _zbxOn = new() { Content = "Use" };
    private readonly StackPanel _zbxForm = new();
    private readonly TextBox _zbxUrl = new();
    private readonly PasswordBox _zbxToken = new();
    private readonly CheckBox _zbxCert = new() { Content = "Ignore certificate errors", IsChecked = true };
    private readonly CheckBox _oxOn = new() { Content = "Use" };
    private readonly StackPanel _oxForm = new();
    private readonly TextBox _oxUrl = new();
    private readonly TextBox _oxUser = new();
    private readonly PasswordBox _oxPass = new();
    private readonly TextBlock _intResult = Muted("Need a jump host? Set it in More → Zabbix or Oxidized settings once the SSH entry exists.");

    // Import
    private string? _import;
    private bool _importForced;
    private readonly Dictionary<string, Border> _importCards = new();
    private readonly Border _importAutoNote = new() { Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 14), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };

    // Startup
    private ReopenMode _reopen;
    private readonly Dictionary<ReopenMode, Button> _reopenButtons = new();
    private readonly CheckBox _backup = new() { Content = "Back up the data source every day (encrypted .rmx file)", Margin = new Thickness(0, 6, 0, 0) };
    private readonly CheckBox _chat = new() { Content = "Open the team chat drawer when RemoteMan starts", Margin = new Thickness(0, 6, 0, 0) };

    // Done
    private readonly Grid _summary = new();

    /// <summary>The import source picked ("rdm", "rdp" or "putty"), to open the import window with once RemoteMan is up.</summary>
    public string? ImportAfter { get; private set; }

    /// <summary>A theme, shading or data source change that only takes effect after a restart.</summary>
    public bool NeedsRestart { get; private set; }

    /// <summary>Whether Finish was pressed (rather than Skip setup or closing the window).</summary>
    public bool Finished { get; private set; }

    /// <param name="rerun">Opened again from More: starts from the current settings, and skipped steps leave them as they are.</param>
    public SetupWindow(AppSettings settings, bool rerun)
    {
        _settings = settings;
        _rerun = rerun;
        _theme = Themes.Find(settings.Theme).Id;
        _shading = settings.Shading;
        _reopen = settings.ReopenSessions;

        Title = rerun ? "RemoteMan setup" : "Welcome to RemoteMan";
        Width = 900; Height = 620; MinWidth = 720; MinHeight = 500;
        WindowStartupLocation = rerun ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        ShowInTaskbar = !rerun;
        if (TryFindResource("RemoteManWindow") is Style ws) Style = ws;

        _back = new Button { Content = "Back", MinWidth = 76, Margin = new Thickness(6, 0, 0, 0) };
        _skip = new Button { Content = "Skip", MinWidth = 76, Margin = new Thickness(6, 0, 0, 0), ToolTip = "Leave this step as it is and go on" };
        _next = new Button { Content = "Next", MinWidth = 96, Margin = new Thickness(6, 0, 0, 0), IsDefault = true };
        if (TryFindResource("PrimaryButton") is Style primary) _next.Style = primary;
        _skipAll = new Button { Content = "Skip setup", ToolTip = "Close setup and keep the defaults for the steps you haven't confirmed" };
        if (TryFindResource("FlatButton") is Style flat) _skipAll.Style = flat;
        _skipAll.SetResourceReference(ForegroundProperty, "Link");
        _back.Click += (_, _) => Go(_cur - 1, -1);
        _skip.Click += (_, _) => { _state[Steps[_cur].Step] = State.Skipped; Go(_cur + 1, 1); };
        _next.Click += (_, _) => OnNext();
        _skipAll.Click += (_, _) => Close();

        LoadCurrent();
        BuildPages();
        if (_ai is not null) RefreshAi();
        Content = BuildChrome();
        Closing += (_, _) => Apply();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        Go(0, 0);
    }

    // ---------------------------------------------------------------- layout

    private DockPanel BuildChrome()
    {
        var root = new DockPanel();

        var footer = new Border { Padding = new Thickness(12, 8, 12, 8), BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.SetResourceReference(Border.BackgroundProperty, "Header");
        footer.SetResourceReference(Border.BorderBrushProperty, "Border");
        DockPanel.SetDock(footer, Dock.Bottom);
        var bar = new DockPanel { LastChildFill = false };
        bar.Children.Add(_skipAll);
        foreach (var b in new[] { _next, _skip, _back }) { DockPanel.SetDock(b, Dock.Right); bar.Children.Add(b); }
        footer.Child = bar;
        root.Children.Add(footer);

        var rail = new Border { Width = 200, BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(0, 14, 0, 14), Child = _rail };
        rail.SetResourceReference(Border.BackgroundProperty, "Panel");
        rail.SetResourceReference(Border.BorderBrushProperty, "Border");
        DockPanel.SetDock(rail, Dock.Left);
        root.Children.Add(rail);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _content, Padding = new Thickness(28, 22, 28, 22) };
        root.Children.Add(scroll);
        return root;
    }

    private void BuildPages()
    {
        _pages[Step.Welcome] = WelcomePage();
        _pages[Step.DataSource] = DataSourcePage();
        _pages[Step.Look] = LookPage();
        _pages[Step.Ai] = AiPage();
        _pages[Step.Integrations] = IntegrationsPage();
        _pages[Step.Import] = ImportPage();
        _pages[Step.Startup] = StartupPage();
        _pages[Step.Done] = DonePage();
    }

    private static TextBlock Muted(string text, double size = 12) => Themed(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size }, "Muted");

    private static TextBlock Themed(TextBlock t, string brush)
    {
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    private static StackPanel Page(string heading, string lead)
    {
        var p = new StackPanel { MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
        p.Children.Add(Themed(new TextBlock { Text = heading, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap }, "TextStrong"));
        var l = Muted(lead);
        l.Margin = new Thickness(0, 4, 0, 18);
        p.Children.Add(l);
        return p;
    }

    private static TextBlock Section(string text) =>
        Themed(new TextBlock { Text = text.ToUpperInvariant(), FontSize = 10.5, Margin = new Thickness(0, 16, 0, 6) }, "Faint");

    /// <summary>A label column and a field column.</summary>
    private static Grid Form(params (string Label, UIElement Field)[] rows) => FormOf(rows.Select(r => ((UIElement)Muted(r.Label), r.Field)).ToArray());

    private static Grid FormOf((UIElement Label, UIElement Field)[] rows)
    {
        var g = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < rows.Length; i++)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var (label, field) = rows[i];
            if (label is FrameworkElement lf) { lf.VerticalAlignment = field is TextBox { AcceptsReturn: true } ? VerticalAlignment.Top : VerticalAlignment.Center; lf.Margin = new Thickness(0, 4, 8, 4); }
            if (field is FrameworkElement ff) ff.Margin = new Thickness(0, 3, 0, 3);
            Grid.SetRow(label, i); Grid.SetRow(field, i); Grid.SetColumn(field, 1);
            g.Children.Add(label); g.Children.Add(field);
        }
        return g;
    }

    /// <summary>A clickable card with a title and a line under it.</summary>
    private static Border Card(string title, string sub, Action onClick, double width = 196)
    {
        var body = new StackPanel();
        body.Children.Add(Themed(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis }, "TextStrong"));
        if (sub.Length > 0) body.Children.Add(Muted(sub, 11));
        return CardAround(body, onClick, width);
    }

    private static Border CardAround(UIElement body, Action onClick, double width)
    {
        var card = new Border
        {
            Width = width, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1), Child = body, Cursor = Cursors.Hand, Focusable = true,
        };
        card.SetResourceReference(Border.BackgroundProperty, "Bg");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.MouseLeftButtonUp += (_, _) => onClick();
        card.KeyDown += (_, e) => { if (e.Key is Key.Space or Key.Enter) { onClick(); e.Handled = true; } };
        return card;
    }

    private static void Mark(Border card, bool on)
    {
        card.SetResourceReference(Border.BorderBrushProperty, on ? "Accent" : "Border");
        card.SetResourceReference(Border.BackgroundProperty, on ? "SelectRow" : "Bg");
    }

    private static void MarkButton(Button b, bool on)
    {
        if (on && Application.Current.TryFindResource("PrimaryButton") is Style p) b.Style = p;
        else b.ClearValue(StyleProperty);
    }

    // ---------------------------------------------------------------- pages

    private FrameworkElement WelcomePage()
    {
        var p = new StackPanel { MaxWidth = 600, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 24, 0, 0) };
        var mark = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 16) };
        mark.SetResourceReference(Border.BackgroundProperty, "Accent");
        mark.Child = Themed(new TextBlock { Text = "R", FontSize = 28, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, "OnAccent");
        p.Children.Add(mark);
        p.Children.Add(Themed(new TextBlock { Text = _rerun ? "Go through setup again" : "Let's get RemoteMan ready", FontSize = 20, FontWeight = FontWeights.SemiBold }, "TextStrong"));
        var lead = Muted(_rerun
            ? "Each step starts from what you have now. Skip a step to leave it as it is."
            : "A few quick choices: where your connections are stored, how it looks, and the extras you use. Each step can be skipped, and you can run this again later from More → Run setup again.");
        lead.Margin = new Thickness(0, 6, 0, 10);
        p.Children.Add(lead);
        var hint = Muted("Takes about a minute. Nothing is saved until you finish or close this window.", 11);
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
        p.Children.Add(hint);
        return p;
    }

    private static readonly (DataSourceKind Kind, string Name, string Sub)[] Sources =
    [
        (DataSourceKind.Sqlite, "Local (this PC)", "A file in AppData. No server needed."),
        (DataSourceKind.PostgreSql, "PostgreSQL", "Shared. Changes reach the team instantly."),
        (DataSourceKind.MySql, "MySQL / MariaDB", "Shared. Changes show within a second."),
        (DataSourceKind.SqlServer, "SQL Server", "Shared. Always On and SSO friendly."),
    ];

    private static readonly Dictionary<DataSourceKind, string> ServerExamples = new()
    {
        [DataSourceKind.PostgreSql] = "Host=pg1;Database=remoteman;Username=remoteman;Password=…",
        [DataSourceKind.MySql] = "Server=db1;Database=remoteman;User ID=remoteman;Password=…",
        [DataSourceKind.SqlServer] = "Server=sql1;Database=RemoteMan;Integrated Security=True;TrustServerCertificate=True",
    };

    private FrameworkElement DataSourcePage()
    {
        var p = Page("Where should your connections live?",
            "Pick local for just this PC, or a shared database so your team works on the same entries and sees each other.");
        var cards = new WrapPanel();
        foreach (var (kind, name, sub) in Sources)
        {
            var c = Card(name, sub, () => { _dsKind = kind; RefreshDataSource(); });
            _dsCards[kind] = c;
            cards.Children.Add(c);
        }
        p.Children.Add(cards);

        _dsLocalNote.Margin = new Thickness(0, 8, 0, 0);
        _dsLocalNote.Text = $"Stored at {AppPaths.LocalDb}. You can add a shared source any time under More → Data sources.";
        p.Children.Add(_dsLocalNote);

        _dsServers.SetResourceReference(FontFamilyProperty, "MonoFont");
        _dsServers.FontSize = 11;
        _dsVault.ToolTip = "Shared by everyone on this data source; encrypts passwords and keys";
        var test = new Button { Content = "Test connection", HorizontalAlignment = HorizontalAlignment.Left };
        test.Click += async (_, _) => await TestDataSourceAsync();
        var testRow = new StackPanel { Orientation = Orientation.Horizontal };
        testRow.Children.Add(test);
        _dsResult.Margin = new Thickness(10, 0, 0, 0);
        _dsResult.VerticalAlignment = VerticalAlignment.Center;
        testRow.Children.Add(_dsResult);
        _dsHint.FontSize = 11;
        _dsShared.Children.Add(Form(("Name", _dsName), ("Servers", _dsServers), ("", _dsHint), ("Vault passphrase", _dsVault), ("", _dsSso), ("", testRow)));
        p.Children.Add(_dsShared);
        return p;
    }

    private void RefreshDataSource()
    {
        foreach (var (k, c) in _dsCards) Mark(c, k == _dsKind);
        var shared = _dsKind != DataSourceKind.Sqlite;
        _dsShared.Visibility = shared ? Visibility.Visible : Visibility.Collapsed;
        _dsLocalNote.Visibility = shared ? Visibility.Collapsed : Visibility.Visible;
        if (shared)
            _dsHint.Text = "One server per line. The first is used; the next ones take over when it's down (a redundant pair).\nExample: " + ServerExamples[_dsKind];
        _dsResult.Text = "";
        RefreshRail();
    }

    private DataSourceConfig ReadDataSource() => new()
    {
        Name = _dsName.Text.Trim() is { Length: > 0 } n ? n : "Team",
        Kind = _dsKind,
        ConnectionStrings = _dsServers.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        UseWindowsLogin = _dsSso.IsChecked == true,
        UseSharedVaultKey = true,
    };

    private async Task TestDataSourceAsync()
    {
        var cfg = ReadDataSource();
        if (cfg.ConnectionStrings.Count == 0) { ShowResult(_dsResult, "Enter at least one server line.", false); return; }
        ShowResult(_dsResult, "Testing…", null);
        try
        {
            await using var src = DataSourceFactory.Create(cfg);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await src.InitializeAsync();
            var n = (await src.LoadAllAsync()).Count;
            ShowResult(_dsResult, $"Connected: {n} entries, {sw.ElapsedMilliseconds} ms", true);
        }
        catch (Exception ex) { ShowResult(_dsResult, ex.GetBaseException().Message, false); }
    }

    private FrameworkElement LookPage()
    {
        var p = Page("Pick a look", "The preview follows along as you click. You can change it any time under More → Theme.");

        var top = new StackPanel { Orientation = Orientation.Horizontal };
        var previewCol = new StackPanel { Width = 300, Margin = new Thickness(0, 0, 20, 0) };
        _previewHost.SetResourceReference(Border.BorderBrushProperty, "Border");
        previewCol.Children.Add(_previewHost);
        _previewName.SetResourceReference(TextBlock.ForegroundProperty, "TextStrong");
        previewCol.Children.Add(_previewName);
        previewCol.Children.Add(_previewDesc);
        top.Children.Add(previewCol);

        var shadingCol = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Width = 300 };
        shadingCol.Children.Add(Section("Shading"));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (level, text, tip) in new[]
                 {
                     (Shading.Flat, "Flat", "Solid fills and thin borders, no shadows"),
                     (Shading.Subtle, "Subtle", "A faint light on buttons and cards, soft shadows along the panels"),
                     (Shading.Raised, "Raised", "Stronger light and shadow; controls clearly stand off the background"),
                 })
        {
            var b = new Button { Content = text, MinWidth = 76, Margin = new Thickness(0, 0, 6, 0), ToolTip = tip };
            b.Click += (_, _) => { _shading = level; RefreshLook(); };
            _shadingButtons[level] = b;
            row.Children.Add(b);
        }
        shadingCol.Children.Add(row);
        var note = Muted("Light and shadow on buttons, fields, cards and panels. Works with every theme.", 11);
        note.Margin = new Thickness(0, 8, 0, 0);
        shadingCol.Children.Add(note);
        top.Children.Add(shadingCol);
        p.Children.Add(top);

        p.Children.Add(Section("Theme"));
        var cards = new WrapPanel();
        foreach (var t in Themes.All)
        {
            var c = t.Resolve();
            var body = new StackPanel();
            var sw = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            foreach (var k in new[] { "Bg", "Panel", "ButtonBorder", "Accent" })
            {
                var brush = new SolidColorBrush(c[k]);
                brush.Freeze();
                sw.Children.Add(new Border { Width = k == "Accent" ? 52 : 36, Height = 14, Background = brush });
            }
            body.Children.Add(sw);
            body.Children.Add(Themed(new TextBlock { Text = t.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis }, "TextStrong"));
            var id = t.Id;
            var card = CardAround(body, () => { _theme = id; RefreshLook(); }, 196);
            card.ToolTip = t.Description;
            _themeCards[id] = card;
            cards.Children.Add(card);
        }
        p.Children.Add(cards);
        return p;
    }

    private void RefreshLook()
    {
        foreach (var (id, c) in _themeCards) Mark(c, id == _theme);
        foreach (var (level, b) in _shadingButtons) MarkButton(b, level == _shading);
        var t = Themes.Find(_theme);
        _previewHost.Child = new Viewbox { Child = new Border { Width = 252, Child = ThemeWindow.MiniPreview(t) }, Stretch = Stretch.Uniform };
        _previewName.Text = t.Name;
        _previewDesc.Text = t.Description;
    }

    private FrameworkElement AiPage()
    {
        var p = Page("Add an AI assistant?",
            "The assistant panel can explain errors, write commands and read the session you're in. Sign in with an API key, or point it at your own Ollama server.");
        var cards = new WrapPanel();
        foreach (var info in AiProviderInfo.All)
        {
            var kind = info.Kind;
            var c = Card(info.Name, info.Vendor, () => { _ai = _ai == kind ? null : kind; RefreshAi(); });
            _aiCards[kind] = c;
            cards.Children.Add(c);
        }
        p.Children.Add(cards);

        var check = new Button { Content = "Check", HorizontalAlignment = HorizontalAlignment.Left };
        check.Click += async (_, _) => await CheckAiAsync();
        var checkRow = new StackPanel { Orientation = Orientation.Horizontal };
        checkRow.Children.Add(check);
        _aiForm.Children.Add(FormOf([(_aiKeyLabel, _aiKey), (_aiEndpointLabel, _aiEndpoint), (Muted("Model"), _aiModel), (Muted(""), _aiContext), (Muted(""), checkRow)]));
        p.Children.Add(_aiForm);
        _aiResult.Margin = new Thickness(0, 10, 0, 0);
        p.Children.Add(_aiResult);
        return p;
    }

    private void RefreshAi()
    {
        foreach (var (k, c) in _aiCards) Mark(c, k == _ai);
        _aiForm.Visibility = _ai is null ? Visibility.Collapsed : Visibility.Visible;
        if (_ai is { } kind)
        {
            var info = AiProviderInfo.Get(kind);
            var keyVis = info.NeedsKey || kind == AiProviderKind.Custom ? Visibility.Visible : Visibility.Collapsed;
            _aiKey.Visibility = _aiKeyLabel.Visibility = keyVis;
            _aiKeyLabel.Text = info.NeedsKey ? "API key" : "API key (optional)";
            _aiEndpoint.Visibility = _aiEndpointLabel.Visibility = info.EndpointEditable ? Visibility.Visible : Visibility.Collapsed;
            var existing = _settings.Ai.Providers.FirstOrDefault(x => x.Kind == kind);
            _aiEndpoint.Text = existing?.Endpoint ?? info.DefaultEndpoint ?? "";
            _aiModel.Text = existing?.Model is { Length: > 0 } m ? m : info.DefaultModel;
            _aiKey.Password = "";
            _aiKey.ToolTip = Secrets.LoadAiKey(kind) is null ? null : "A key is saved. Leave this empty to keep it.";
            ShowResult(_aiResult, "", null);
        }
        else ShowResult(_aiResult, "Not using AI? Press Skip; the assistant stays hidden until you set it up.", null);
    }

    private AiProviderConfig? ReadAi() => _ai is not { } kind ? null : new()
    {
        Kind = kind,
        AuthMode = AiAuthMode.ApiKey,
        Model = _aiModel.Text.Trim(),
        Endpoint = AiProviderInfo.Get(kind).EndpointEditable ? _aiEndpoint.Text.Trim() : null,
    };

    private string AiKey => _aiKey.Password.Trim() is { Length: > 0 } k ? k : _ai is { } kind ? Secrets.LoadAiKey(kind) ?? "" : "";

    private string? AiProblem()
    {
        if (ReadAi() is not { } cfg) return null;
        if (cfg.Info.NeedsKey && AiKey.Length == 0) return "Paste your API key, or press Skip.";
        if (cfg.Info.EndpointEditable && !Uri.TryCreate(cfg.EffectiveEndpoint, UriKind.Absolute, out _)) return "Enter the server address, like http://10.0.10.30:11434/v1";
        return null;
    }

    private async Task CheckAiAsync()
    {
        if (AiProblem() is { } problem) { ShowResult(_aiResult, problem, false); return; }
        var cfg = ReadAi()!;
        ShowResult(_aiResult, "Checking…", null);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var models = await AiClientFactory.Create(cfg, AiKey).ListModelsAsync(cts.Token);
            if (_aiModel.Text.Trim().Length == 0 && models.Count > 0) _aiModel.Text = models[0];
            ShowResult(_aiResult, models.Count > 0 ? $"Works. {models.Count} models available." : "Works.", true);
        }
        catch (Exception ex)
        {
            ShowResult(_aiResult, ex is OperationCanceledException ? "No answer within 15 seconds." : ex.GetBaseException().Message, false);
        }
    }

    private FrameworkElement IntegrationsPage()
    {
        var p = Page("Monitoring and config backups", "Turn on what you run. Both are for you only, and can be changed later under More.");

        _zbxForm.Children.Add(Form(("URL", _zbxUrl), ("API token", _zbxToken), ("", _zbxCert)));
        p.Children.Add(IntegrationRow("Z", "Zabbix", "Problem badges on entries, a trend on the dashboard, and importing hosts.", _zbxOn, _zbxForm));
        _oxForm.Children.Add(Form(("URL", _oxUrl), ("User (optional)", _oxUser), ("Password", _oxPass)));
        p.Children.Add(IntegrationRow("Ox", "Oxidized", "Last config backup and diffs for routers, switches and firewalls.", _oxOn, _oxForm));
        _intResult.Margin = new Thickness(0, 10, 0, 0);
        _intResult.FontSize = 11;
        p.Children.Add(_intResult);

        _zbxOn.Checked += (_, _) => RefreshIntegrations();
        _zbxOn.Unchecked += (_, _) => RefreshIntegrations();
        _oxOn.Checked += (_, _) => RefreshIntegrations();
        _oxOn.Unchecked += (_, _) => RefreshIntegrations();
        return p;
    }

    private static StackPanel IntegrationRow(string mono, string name, string sub, CheckBox on, StackPanel form)
    {
        var wrap = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        var row = new Border { Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
        row.SetResourceReference(Border.BackgroundProperty, "Bg");
        row.SetResourceReference(Border.BorderBrushProperty, "Border");
        var dock = new DockPanel();
        var icon = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 10, 0) };
        icon.SetResourceReference(Border.BackgroundProperty, "ButtonBg");
        icon.SetResourceReference(Border.BorderBrushProperty, "ButtonBorder");
        icon.Child = Themed(new TextBlock { Text = mono, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, "Link");
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        on.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(on, Dock.Right);
        dock.Children.Add(on);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Themed(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold }, "TextStrong"));
        text.Children.Add(Muted(sub, 11));
        dock.Children.Add(text);
        row.Child = dock;
        wrap.Children.Add(row);
        form.Margin = new Thickness(40, 0, 0, 4);
        wrap.Children.Add(form);
        return wrap;
    }

    private void RefreshIntegrations()
    {
        _zbxForm.Visibility = _zbxOn.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _oxForm.Visibility = _oxOn.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private string? IntegrationsProblem()
    {
        if (_zbxOn.IsChecked == true)
        {
            if (_zbxUrl.Text.Trim().Length == 0) return "Enter the Zabbix URL, or untick Zabbix.";
            if (_zbxToken.Password.Trim().Length == 0 && string.IsNullOrEmpty(_settings.Zabbix.ProtectedToken)) return "Paste a Zabbix API token (Users → API tokens in Zabbix).";
        }
        if (_oxOn.IsChecked == true && _oxUrl.Text.Trim().Length == 0) return "Enter the Oxidized URL, or untick Oxidized.";
        return null;
    }

    private static readonly (string Id, string Name, string Sub)[] ImportSources =
    [
        ("rdm", "Devolutions RDM", ".rdm or .json export"),
        ("rdp", ".rdp files", "One or many"),
        ("putty", "PuTTY", "Saved sessions on this PC"),
        ("none", "Start empty", "I'll add entries myself"),
    ];

    private FrameworkElement ImportPage()
    {
        var outer = new StackPanel();
        _importAutoNote.SetResourceReference(Border.BackgroundProperty, "Inset");
        _importAutoNote.SetResourceReference(Border.BorderBrushProperty, "Border");
        var noteRow = new DockPanel();
        var anyway = new Button { Content = "Import anyway", Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        anyway.Click += (_, _) => { _importForced = true; RefreshImport(); };
        DockPanel.SetDock(anyway, Dock.Right);
        noteRow.Children.Add(anyway);
        noteRow.Children.Add(Muted("Skipped because you picked a shared database, which already has your team's entries. If it's brand new, you can still import."));
        _importAutoNote.Child = noteRow;
        outer.Children.Add(_importAutoNote);

        var p = Page("Bring your connections over", "Pick a source and the import preview opens as soon as RemoteMan is up, into the data source you chose.");
        var cards = new WrapPanel();
        foreach (var (id, name, sub) in ImportSources)
        {
            var c = Card(name, sub, () => { _import = id; RefreshImport(); });
            _importCards[id] = c;
            cards.Children.Add(c);
        }
        p.Children.Add(cards);
        outer.Children.Add(p);
        return outer;
    }

    /// <summary>Why a step isn't needed after earlier choices, or null.</summary>
    private string? AutoSkipReason(Step step) =>
        step == Step.Import && !_importForced && _dsKind != DataSourceKind.Sqlite && _state[Step.DataSource] != State.Skipped
            ? "shared database already has entries" : null;

    private void RefreshImport()
    {
        foreach (var (id, c) in _importCards) Mark(c, id == _import);
        _importAutoNote.Visibility = AutoSkipReason(Step.Import) is null ? Visibility.Collapsed : Visibility.Visible;
        RefreshRail();
    }

    private FrameworkElement StartupPage()
    {
        var p = Page("When RemoteMan starts", "Reopen the tabs and session windows that were open when you closed it?");
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (mode, label) in new[] { (ReopenMode.Ask, "Ask me"), (ReopenMode.Always, "Always"), (ReopenMode.Never, "Never") })
        {
            var b = new Button { Content = label, MinWidth = 76, Margin = new Thickness(0, 0, 6, 0) };
            b.Click += (_, _) => { _reopen = mode; RefreshStartup(); };
            _reopenButtons[mode] = b;
            row.Children.Add(b);
        }
        p.Children.Add(row);
        p.Children.Add(Section("Also"));
        p.Children.Add(_backup);
        p.Children.Add(_chat);
        return p;
    }

    private void RefreshStartup()
    {
        foreach (var (mode, b) in _reopenButtons) MarkButton(b, mode == _reopen);
    }

    private FrameworkElement DonePage()
    {
        var p = Page("All set", _rerun ? "Here's what will change. Press Finish to save it." : "Here's what will be saved. Press Finish to open RemoteMan.");
        _summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        _summary.ColumnDefinitions.Add(new ColumnDefinition());
        p.Children.Add(_summary);
        return p;
    }

    private void RefreshSummary()
    {
        _summary.Children.Clear();
        _summary.RowDefinitions.Clear();
        var keep = _rerun ? "Unchanged" : "Skipped (default)";
        void Row(Step step, string label, Func<string> value)
        {
            var i = _summary.RowDefinitions.Count;
            _summary.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = Muted(label);
            l.Margin = new Thickness(0, 3, 0, 3);
            var auto = AutoSkipReason(step);
            var v = auto is not null ? Themed(new TextBlock { Text = $"Not needed ({auto})", FontStyle = FontStyles.Italic }, "Faint")
                : _state[step] == State.Done ? new TextBlock { Text = value(), TextWrapping = TextWrapping.Wrap }
                : Themed(new TextBlock { Text = keep, FontStyle = FontStyles.Italic }, "Faint");
            v.Margin = new Thickness(0, 3, 0, 3);
            Grid.SetRow(l, i); Grid.SetRow(v, i); Grid.SetColumn(v, 1);
            _summary.Children.Add(l); _summary.Children.Add(v);
        }
        Row(Step.DataSource, "Data source", () => _dsKind == DataSourceKind.Sqlite ? "Local (this PC)" : $"{Sources.First(s => s.Kind == _dsKind).Name}, \"{ReadDataSource().Name}\", {ReadDataSource().ConnectionStrings.Count} server(s)");
        Row(Step.Look, "Look", () => $"{Themes.Find(_theme).Name}, {_shading.ToString().ToLowerInvariant()} shading");
        Row(Step.Ai, "AI assistant", () => _ai is { } k ? AiProviderInfo.Get(k).Name : "None");
        Row(Step.Integrations, "Integrations", () => string.Join(", ", new[] { _zbxOn.IsChecked == true ? "Zabbix" : null, _oxOn.IsChecked == true ? "Oxidized" : null }.OfType<string>()) is { Length: > 0 } s ? s : "None");
        Row(Step.Import, "Import", () => _import is null or "none" ? "Start empty" : ImportSources.First(s => s.Id == _import).Name + ", opens when RemoteMan is up");
        Row(Step.Startup, "Reopen sessions", () => _reopen.ToString() + (_backup.IsChecked == true ? ", daily backup" : "") + (_chat.IsChecked == true ? ", chat drawer open" : ""));
    }

    private static void ShowResult(TextBlock t, string text, bool? ok)
    {
        t.Text = text;
        t.SetResourceReference(TextBlock.ForegroundProperty, ok switch { true => "Ok", false => "Bad", null => "Muted" });
    }

    // ---------------------------------------------------------------- navigation

    private void RefreshRail()
    {
        _rail.Children.Clear();
        for (var i = 0; i < Steps.Length; i++)
        {
            var (step, title, sub) = Steps[i];
            var auto = AutoSkipReason(step) is not null;
            var st = _state[step];
            var current = i == _cur;

            var circle = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            var num = new TextBlock { Text = st == State.Done && !auto ? "✓" : auto || st == State.Skipped ? "–" : (i + 1).ToString(), FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (st == State.Done && !auto)
            {
                circle.SetResourceReference(Border.BackgroundProperty, "Accent");
                circle.SetResourceReference(Border.BorderBrushProperty, "Accent");
                num.SetResourceReference(TextBlock.ForegroundProperty, "OnAccent");
            }
            else
            {
                circle.SetResourceReference(Border.BorderBrushProperty, current ? "Accent" : "ButtonBorder");
                num.SetResourceReference(TextBlock.ForegroundProperty, current ? "Accent" : "Muted");
            }
            circle.Child = num;

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(Themed(new TextBlock { Text = title }, current ? "TextStrong" : "Muted"));
            var subText = auto ? "Not needed" : st == State.Skipped ? "Skipped" : sub;
            if (subText.Length > 0) text.Children.Add(Themed(new TextBlock { Text = subText, FontSize = 10.5 }, "Faint"));

            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(circle);
            line.Children.Add(text);
            var item = new Border { Padding = new Thickness(14, 6, 14, 6), BorderThickness = new Thickness(2, 0, 0, 0), Child = line, Cursor = Cursors.Hand, Opacity = auto && !current ? 0.6 : 1 };
            item.SetResourceReference(Border.BorderBrushProperty, current ? "Accent" : "Panel");
            if (current) item.SetResourceReference(Border.BackgroundProperty, "SelectRow");
            else item.Background = Brushes.Transparent;
            var index = i;
            item.MouseLeftButtonUp += (_, _) => Go(index, 0);
            _rail.Children.Add(item);
        }
        var foot = Themed(new TextBlock { Text = $"Step {_cur + 1} of {Steps.Length}", FontSize = 11, Margin = new Thickness(16, 14, 16, 0) }, "Faint");
        _rail.Children.Add(foot);
    }

    /// <summary>Shows step <paramref name="index"/>; with a direction, passes over steps earlier choices made unnecessary.</summary>
    private void Go(int index, int direction)
    {
        _cur = Math.Clamp(index, 0, Steps.Length - 1);
        while (direction != 0 && _cur > 0 && _cur < Steps.Length - 1 && AutoSkipReason(Steps[_cur].Step) is not null) _cur += direction;
        var step = Steps[_cur].Step;
        switch (step)
        {
            case Step.DataSource: RefreshDataSource(); break;
            case Step.Look: RefreshLook(); break;
            case Step.Ai: RefreshAiMarks(); break;
            case Step.Integrations: RefreshIntegrations(); break;
            case Step.Import: RefreshImport(); break;
            case Step.Startup: RefreshStartup(); break;
            case Step.Done: RefreshSummary(); break;
        }
        _content.Content = _pages[step];
        var first = _cur == 0;
        var last = step == Step.Done;
        _back.IsEnabled = !first;
        _skip.Visibility = first || last ? Visibility.Collapsed : Visibility.Visible;
        _skipAll.Visibility = last ? Visibility.Collapsed : Visibility.Visible;
        _next.Content = first ? "Get started" : last ? "Finish" : "Next";
        RefreshRail();
    }

    /// <summary>Re-marks the AI cards without resetting what was typed (RefreshAi also loads the provider's defaults).</summary>
    private void RefreshAiMarks()
    {
        foreach (var (k, c) in _aiCards) Mark(c, k == _ai);
        _aiForm.Visibility = _ai is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnNext()
    {
        var step = Steps[_cur].Step;
        switch (step)
        {
            case Step.Welcome: Go(1, 1); return;
            case Step.Done: Finished = true; Close(); return;
            case Step.DataSource when _dsKind != DataSourceKind.Sqlite:
            {
                var cfg = ReadDataSource();
                if (cfg.ConnectionStrings.Count == 0) { ShowResult(_dsResult, "Enter at least one server line, or pick Local.", false); return; }
                if (_dsVault.Password.Length == 0 && Secrets.LoadVaultPassphrase(cfg.Name) is null)
                { ShowResult(_dsResult, "Set a vault passphrase. Everyone on this data source uses the same one.", false); return; }
                break;
            }
            case Step.Ai when AiProblem() is { } problem: ShowResult(_aiResult, problem, false); return;
            case Step.Integrations when IntegrationsProblem() is { } problem: ShowResult(_intResult, problem, false); return;
        }
        _state[step] = State.Done;
        Go(_cur + 1, 1);
    }

    // ---------------------------------------------------------------- settings

    /// <summary>Fills the steps from the current settings (all defaults on a first start).</summary>
    private void LoadCurrent()
    {
        var active = _settings.Active;
        _dsKind = active.Kind;
        if (active.Kind != DataSourceKind.Sqlite)
        {
            _dsName.Text = active.Name;
            _dsServers.Text = string.Join(Environment.NewLine, active.ConnectionStrings);
            _dsSso.IsChecked = active.UseWindowsLogin;
        }
        _ai = _settings.Ai.ActiveProvider?.Kind;
        _aiContext.IsChecked = _settings.Ai.IncludeSessionContext;
        _zbxOn.IsChecked = _settings.Zabbix.Enabled;
        _zbxUrl.Text = _settings.Zabbix.Url;
        _zbxCert.IsChecked = _settings.Zabbix.IgnoreCertificateErrors;
        _oxOn.IsChecked = _settings.Oxidized.Enabled;
        _oxUrl.Text = _settings.Oxidized.Url;
        _oxUser.Text = _settings.Oxidized.UserName ?? "";
        _backup.IsChecked = _settings.Backup.Enabled;
        _chat.IsChecked = _settings.ChatOpen;
        if (_ai is { } kind)
        {
            var cfg = _settings.Ai.Providers.First(p => p.Kind == kind);
            _aiModel.Text = cfg.EffectiveModel;
            _aiEndpoint.Text = cfg.EffectiveEndpoint;
        }
    }

    /// <summary>Writes the confirmed steps to the settings (once, as the window closes). Skipped steps are left alone.</summary>
    private void Apply()
    {
        if (_applied) return;
        _applied = true;
        bool Confirmed(Step s) => _state[s] == State.Done && AutoSkipReason(s) is null;
        var before = (_settings.ActiveDataSource, _settings.Active.Kind);

        if (Confirmed(Step.DataSource))
        {
            if (_dsKind == DataSourceKind.Sqlite)
            {
                var local = _settings.DataSources.FirstOrDefault(d => d.Kind == DataSourceKind.Sqlite);
                if (local is null) _settings.DataSources.Add(local = new DataSourceConfig { Name = "Local", Kind = DataSourceKind.Sqlite, ConnectionStrings = { AppPaths.LocalDb } });
                _settings.ActiveDataSource = local.Name;
            }
            else
            {
                var cfg = ReadDataSource();
                var existing = _settings.DataSources.FirstOrDefault(d => d.Name == cfg.Name);
                if (existing is null) _settings.DataSources.Add(cfg);
                else
                {
                    existing.Kind = cfg.Kind;
                    existing.ConnectionStrings = cfg.ConnectionStrings;
                    existing.UseWindowsLogin = cfg.UseWindowsLogin;
                    existing.UseSharedVaultKey = true;
                }
                if (_dsVault.Password.Length > 0) Secrets.SaveVaultPassphrase(cfg.Name, _dsVault.Password);
                _settings.ActiveDataSource = cfg.Name;
            }
        }

        if (Confirmed(Step.Look))
        {
            _settings.Theme = _theme;
            _settings.Shading = _shading;
        }

        var aiChanged = false;
        if (Confirmed(Step.Ai) && ReadAi() is { } ai)
        {
            if (_aiKey.Password.Trim().Length > 0) Secrets.SaveAiKey(ai.Kind, _aiKey.Password.Trim());
            _settings.Ai.Providers.RemoveAll(x => x.Kind == ai.Kind);
            _settings.Ai.Providers.Add(ai);
            _settings.Ai.Active = ai.Kind;
            _settings.Ai.IncludeSessionContext = _aiContext.IsChecked == true;
            aiChanged = true;
        }

        var integrationsChanged = false;
        if (Confirmed(Step.Integrations))
        {
            var z = _settings.Zabbix;
            z.Enabled = _zbxOn.IsChecked == true;
            if (z.Enabled)
            {
                z.Url = _zbxUrl.Text.Trim();
                if (_zbxToken.Password.Trim().Length > 0) z.ProtectedToken = ZabbixConnector.ProtectToken(_zbxToken.Password);
                z.IgnoreCertificateErrors = _zbxCert.IsChecked == true;
            }
            var o = _settings.Oxidized;
            o.Enabled = _oxOn.IsChecked == true;
            if (o.Enabled)
            {
                o.Url = _oxUrl.Text.Trim();
                o.UserName = _oxUser.Text.Trim() is { Length: > 0 } u ? u : null;
                if (o.UserName is null) o.ProtectedPassword = null;
                else if (_oxPass.Password.Length > 0) o.ProtectedPassword = OxidizedConnector.ProtectPassword(_oxPass.Password);
            }
            integrationsChanged = true;
            // The data source holds the integrations for everyone: these go there the next time it opens.
            if (!_settings.IntegrationsPending.Contains(_settings.Active.Name)) _settings.IntegrationsPending.Add(_settings.Active.Name);
        }

        if (Confirmed(Step.Import) && _import is "rdm" or "rdp" or "putty") ImportAfter = _import;

        if (Confirmed(Step.Startup))
        {
            _settings.ReopenSessions = _reopen;
            _settings.Backup.Enabled = _backup.IsChecked == true;
            _settings.ChatOpen = _chat.IsChecked == true;
        }

        try { _settings.Save(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Saving setup failed: {ex.Message}"); }

        // Colours are looked up once as windows are built, so a new theme needs a fresh start. When run again from
        // More, the open window also holds the data source, AI and integration connections made at startup.
        var themeChanged = Themes.Find(_settings.Theme).Id != Themes.Current.Id || _settings.Shading != Themes.CurrentShading;
        var sourceChanged = before != (_settings.ActiveDataSource, _settings.Active.Kind);
        NeedsRestart = themeChanged || (_rerun && (sourceChanged || aiChanged || integrationsChanged));
    }
}
