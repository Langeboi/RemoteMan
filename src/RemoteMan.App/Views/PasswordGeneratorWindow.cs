using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using RemoteMan.App.Services;
using RemoteMan.Core.Security;

namespace RemoteMan.App.Views;

/// <summary>
/// The password generator, laid out like RDM's: Mode and Template on top, character sets with a minimum each,
/// how many to make and Generate, a grid of candidates with length and strength, and an analysis of the
/// selected one. From the entry editor, OK hands the selected password back in <see cref="Result"/>;
/// opened on its own (More menu) it only copies.
/// </summary>
public sealed class PasswordGeneratorWindow : Window
{
    private static readonly (PasswordMode Mode, string Name)[] Modes =
    [
        (PasswordMode.Characters, "Default"),
        (PasswordMode.Pronounceable, "Pronounceable"),
        (PasswordMode.Words, "Readable passphrase"),
        (PasswordMode.Pattern, "Pattern"),
        (PasswordMode.Hex, "Hexadecimal"),
    ];

    private readonly PasswordGeneratorSettings _settings = PasswordGeneratorSettings.Load();
    private readonly bool _standalone;
    private bool _loading;
    private List<GeneratedPassword> _cands = new();
    private int _sel;

    /// <summary>The password picked with OK.</summary>
    public string? Result { get; private set; }

    /// <summary>Strength of <see cref="Result"/> in bits.</summary>
    public double ResultBits { get; private set; }

    // Top bar
    private readonly ComboBox _mode = new() { Width = 170 };
    private readonly ComboBox _template = new() { Width = 200 };
    private readonly Button _deleteTemplate;

    // Default mode
    private readonly TextBox _length = NumBox(PasswordGenerator.MinLength, PasswordGenerator.MaxLength);
    private readonly Dictionary<string, (CheckBox On, TextBox Min)> _sets = new();
    private readonly TextBox _custom = TextField("Extra characters to use");
    private readonly TextBox _exclude = TextField("Characters never to use, e.g. \\ ` ; $");
    private readonly CheckBox _lookAlikes = new() { Content = "Exclude look-alikes (I l 1 O 0 o)" };
    private readonly CheckBox _noRepeats = new() { Content = "No repeating characters" };
    private readonly CheckBox _startLetter = new() { Content = "Start with a letter" };

    // Pronounceable
    private readonly TextBox _pronLength = NumBox(6, PasswordGenerator.MaxLength);
    private readonly CheckBox _mixCaps = new() { Content = "Mix in capitals" };
    private readonly CheckBox _endDigits = new() { Content = "End with two digits" };
    private readonly CheckBox _endSymbol = new() { Content = "End with a special character" };

    // Passphrase
    private readonly TextBox _words = NumBox(2, 12);
    private readonly TextBox _separator = TextField(null, 60);
    private readonly CheckBox _capitalize = new() { Content = "Capitalise words" };
    private readonly CheckBox _addNumber = new() { Content = "Add a number" };

    // Pattern and hex
    private readonly TextBox _pattern = TextField(null);
    private readonly TextBox _hexLength = NumBox(8, 256);
    private readonly CheckBox _hexUpper = new() { Content = "Uppercase A–F" };

    private readonly Dictionary<PasswordMode, FrameworkElement> _modePanels = new();

    // Right side
    private readonly StackPanel _rows = new();
    private readonly ScrollViewer _rowsScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = true, FocusVisualStyle = null };
    private readonly TextBox _count = NumBox(1, 50);
    private readonly TextBlock _big = new() { FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _meter = new() { Height = 8, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(3) };
    private readonly Grid _meterTrack = new() { Height = 8, Margin = new Thickness(0, 6, 0, 6) };
    private readonly TextBlock _facts = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
    private readonly Button _ok;

    public PasswordGeneratorWindow(Window? owner, bool standalone = false)
    {
        _standalone = standalone;
        if (owner is not null) Owner = owner;
        Title = "Password generator";
        Width = 820;
        Height = 600;
        MinWidth = 700;
        MinHeight = 480;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        ShowInTaskbar = owner is null;
        SetResourceReference(StyleProperty, "RemoteManWindow");

        var root = new DockPanel();

        // ---- Top bar: mode and template ----
        var top = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(12, 8, 12, 8) };
        top.SetResourceReference(Border.BackgroundProperty, "Panel");
        top.SetResourceReference(Border.BorderBrushProperty, "Border");
        var topBar = new DockPanel { LastChildFill = false };
        foreach (var (_, name) in Modes) _mode.Items.Add(name);
        topBar.Children.Add(Muted("Mode", new Thickness(0, 0, 6, 0)));
        topBar.Children.Add(_mode);
        topBar.Children.Add(Muted("Template", new Thickness(16, 0, 6, 0)));
        topBar.Children.Add(_template);
        var saveTemplate = LinkButton("Save as template…");
        saveTemplate.Margin = new Thickness(10, 0, 0, 0);
        saveTemplate.Click += (_, _) => SaveTemplate();
        topBar.Children.Add(saveTemplate);
        _deleteTemplate = LinkButton("Delete template");
        _deleteTemplate.Click += (_, _) => DeleteTemplate();
        DockPanel.SetDock(_deleteTemplate, Dock.Right);
        topBar.Children.Add(_deleteTemplate);
        top.Child = topBar;
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        // ---- Footer ----
        var foot = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(10, 8, 10, 8) };
        foot.SetResourceReference(Border.BackgroundProperty, "Header");
        foot.SetResourceReference(Border.BorderBrushProperty, "Border");
        var footBar = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        _ok = new Button { Content = standalone ? "Copy" : "OK", MinWidth = 76, IsDefault = true };
        _ok.SetResourceReference(StyleProperty, "PrimaryButton");
        _ok.Click += (_, _) => Use();
        var cancel = new Button { Content = standalone ? "Close" : "Cancel", MinWidth = 76, IsCancel = true, Margin = new Thickness(6, 0, 0, 0) };
        buttons.Children.Add(_ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Right);
        footBar.Children.Add(buttons);
        footBar.Children.Add(Muted(standalone ? "Double-click a row to copy it. The clipboard clears after 30 seconds."
                                              : "Double-click a row or press Enter to use it", default, 11.5));
        foot.Child = footBar;
        DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(foot);

        // ---- Body ----
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(296) });
        body.ColumnDefinitions.Add(new ColumnDefinition());

        var optsHost = new Border { BorderThickness = new Thickness(0, 0, 1, 0) };
        optsHost.SetResourceReference(Border.BackgroundProperty, "Panel");
        optsHost.SetResourceReference(Border.BorderBrushProperty, "Border");
        var opts = new StackPanel { Margin = new Thickness(12, 10, 12, 10) };
        _modePanels[PasswordMode.Characters] = BuildDefaultPanel();
        _modePanels[PasswordMode.Pronounceable] = BuildPronounceablePanel();
        _modePanels[PasswordMode.Words] = BuildWordsPanel();
        _modePanels[PasswordMode.Pattern] = BuildPatternPanel();
        _modePanels[PasswordMode.Hex] = BuildHexPanel();
        foreach (var p in _modePanels.Values) opts.Children.Add(p);
        optsHost.Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = opts };
        body.Children.Add(optsHost);

        var right = new DockPanel();
        right.SetResourceReference(Panel.BackgroundProperty, "Panel2");
        Grid.SetColumn(right, 1);

        var head = RowGrid();
        head.Height = 26;
        head.Children.Add(Cell(HeadText("#", TextAlignment.Right), 0));
        head.Children.Add(Cell(HeadText("Password"), 1));
        head.Children.Add(Cell(HeadText("Len", TextAlignment.Right), 2));
        head.Children.Add(Cell(HeadText("Strength"), 3));
        var headBorder = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = head };
        headBorder.SetResourceReference(Border.BackgroundProperty, "Header");
        headBorder.SetResourceReference(Border.BorderBrushProperty, "Border");
        DockPanel.SetDock(headBorder, Dock.Top);
        right.Children.Add(headBorder);

        // Analysis of the selected password
        var analysis = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(10, 9, 10, 9) };
        analysis.SetResourceReference(Border.BackgroundProperty, "Panel");
        analysis.SetResourceReference(Border.BorderBrushProperty, "Border");
        var an = new StackPanel();
        var bigBox = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(8, 4, 8, 4), Child = _big, CornerRadius = new CornerRadius(2) };
        bigBox.SetResourceReference(Border.BackgroundProperty, "Work");
        bigBox.SetResourceReference(Border.BorderBrushProperty, "InputBorder");
        _big.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        _big.SetResourceReference(TextBlock.ForegroundProperty, "TextStrong");
        an.Children.Add(bigBox);
        var track = new Border { CornerRadius = new CornerRadius(3) };
        track.SetResourceReference(Border.BackgroundProperty, "BorderSoft");
        _meterTrack.Children.Add(track);
        _meterTrack.Children.Add(_meter);
        _meterTrack.SizeChanged += (_, _) => UpdateAnalysis();
        an.Children.Add(_meterTrack);
        _facts.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        an.Children.Add(_facts);
        analysis.Child = an;
        DockPanel.SetDock(analysis, Dock.Bottom);
        right.Children.Add(analysis);

        // How many + Copy + Generate
        var gen = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
        gen.SetResourceReference(Border.BorderBrushProperty, "BorderSoft");
        var genBar = new DockPanel();
        genBar.Children.Add(Muted("Passwords", new Thickness(0, 0, 6, 0)));
        genBar.Children.Add(_count);
        var genButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var copy = new Button { Content = "Copy", Padding = new Thickness(10, 0, 10, 0), ToolTip = "Copy the selected password (the clipboard clears after 30 seconds)" };
        copy.Click += (_, _) => Copy();
        var regen = new Button { Content = "↻  Generate", Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Make a new batch (F5)" };
        regen.Click += (_, _) => Regenerate();
        if (!standalone) genButtons.Children.Add(copy);
        genButtons.Children.Add(regen);
        genBar.Children.Add(genButtons);
        gen.Child = genBar;
        DockPanel.SetDock(gen, Dock.Bottom);
        right.Children.Add(gen);

        _rowsScroll.Content = _rows;
        right.Children.Add(_rowsScroll);
        body.Children.Add(right);
        root.Children.Add(body);
        Content = root;

        // ---- Wire up ----
        LoadTemplates();
        Apply(_settings.Last);
        _count.Text = Math.Clamp(_settings.Count, 1, 50).ToString();
        if (_settings.LastTemplate is { } lt && Templates().FindIndex(t => t.Name == lt) is >= 0 and var ti) { _loading = true; _template.SelectedIndex = ti; _loading = false; }
        UpdateDeleteButton();

        _mode.SelectionChanged += (_, _) => { if (_loading) return; ShowModePanel(); Regenerate(); };
        _template.SelectionChanged += (_, _) =>
        {
            UpdateDeleteButton();
            if (_loading || _template.SelectedIndex < 0) return;
            Apply(Templates()[_template.SelectedIndex]);
            Regenerate();
        };
        _count.TextChanged += (_, _) => { if (!_loading) Regenerate(); };
        foreach (var t in new[] { _length, _custom, _exclude, _pronLength, _words, _separator, _pattern, _hexLength }.Concat(_sets.Values.Select(s => s.Min)))
            t.TextChanged += (_, _) => OptionChanged();
        foreach (var cb in new[] { _lookAlikes, _noRepeats, _startLetter, _mixCaps, _endDigits, _endSymbol, _capitalize, _addNumber, _hexUpper })
        {
            cb.Checked += (_, _) => OptionChanged();
            cb.Unchecked += (_, _) => OptionChanged();
        }
        PreviewKeyDown += OnKey;
        Loaded += (_, _) => { Regenerate(); _rowsScroll.Focus(); };
    }

    // ------------------------------------------------------------------ option panels

    private FrameworkElement BuildDefaultPanel()
    {
        var p = new StackPanel();
        p.Children.Add(Heading("Length"));
        p.Children.Add(Spinner(_length, "characters"));

        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
        void AddRow(UIElement a, UIElement? b, UIElement? c)
        {
            var r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            foreach (var (el, col) in new[] { (a, 0), (b, 1), (c, 2) })
            {
                if (el is null) continue;
                Grid.SetRow(el, r);
                Grid.SetColumn(el, col);
                grid.Children.Add(el);
            }
        }
        var minHead = Heading("Min");
        minHead.HorizontalAlignment = HorizontalAlignment.Right;
        AddRow(Heading("Characters"), null, minHead);
        foreach (var (key, label, chars) in new[]
                 {
                     ("up", "Uppercase", "A–Z"), ("lo", "Lowercase", "a–z"), ("dig", "Digits", "0–9"),
                     ("sym", "Special", PasswordGenerator.Symbols), ("minus", "Minus", "-"), ("under", "Underline", "_"),
                     ("space", "Space", "␣"), ("brk", "Brackets", PasswordGenerator.Brackets),
                 })
        {
            var on = new CheckBox { Content = label, Margin = new Thickness(0, 3, 0, 3), VerticalAlignment = VerticalAlignment.Center };
            var sample = new TextBlock { Text = chars, FontSize = 11, Margin = new Thickness(6, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            sample.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            sample.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
            var min = NumBox(0, PasswordGenerator.MaxLength);
            min.HorizontalAlignment = HorizontalAlignment.Right;
            min.Margin = new Thickness(0, 1, 0, 1);
            AutomationPropertiesName(min, $"Minimum {label.ToLowerInvariant()}");
            on.Checked += (_, _) => { min.IsEnabled = true; OptionChanged(); };
            on.Unchecked += (_, _) => { min.IsEnabled = false; OptionChanged(); };
            _sets[key] = (on, min);
            AddRow(on, sample, min);
        }
        p.Children.Add(grid);

        p.Children.Add(Rule());
        p.Children.Add(Labeled("Custom", _custom));
        p.Children.Add(Labeled("Exclude", _exclude));
        foreach (var cb in new[] { _lookAlikes, _noRepeats, _startLetter })
        {
            cb.Margin = new Thickness(0, 6, 0, 0);
            p.Children.Add(cb);
        }
        return p;
    }

    private FrameworkElement BuildPronounceablePanel()
    {
        var p = new StackPanel();
        p.Children.Add(Heading("Length"));
        p.Children.Add(Spinner(_pronLength, "characters"));
        foreach (var cb in new[] { _mixCaps, _endDigits, _endSymbol }) { cb.Margin = new Thickness(0, 6, 0, 0); p.Children.Add(cb); }
        p.Children.Add(Hint("Syllables like kavotemu. Easier to read out over the phone or type on a console."));
        return p;
    }

    private FrameworkElement BuildWordsPanel()
    {
        var p = new StackPanel();
        p.Children.Add(Heading("Words"));
        p.Children.Add(Spinner(_words, "words"));
        p.Children.Add(Labeled("Separator", _separator));
        foreach (var cb in new[] { _capitalize, _addNumber }) { cb.Margin = new Thickness(0, 6, 0, 0); p.Children.Add(cb); }
        p.Children.Add(Hint($"Words come from a list of {PasswordGenerator.WordList.Length}, so each one adds 8 bits."));
        return p;
    }

    private FrameworkElement BuildPatternPanel()
    {
        var p = new StackPanel();
        p.Children.Add(Heading("Pattern"));
        _pattern.Margin = new Thickness(0, 4, 0, 0);
        p.Children.Add(_pattern);
        p.Children.Add(Hint("A upper · a lower · 9 digit · ! special · h hex · * any · \\x the literal x. Anything else is kept as typed. Look-alikes are left out."));
        return p;
    }

    private FrameworkElement BuildHexPanel()
    {
        var p = new StackPanel();
        p.Children.Add(Heading("Length"));
        p.Children.Add(Spinner(_hexLength, "hex digits"));
        _hexUpper.Margin = new Thickness(0, 6, 0, 0);
        p.Children.Add(_hexUpper);
        return p;
    }

    // ------------------------------------------------------------------ options <-> controls

    private PasswordOptions Read()
    {
        int N(TextBox t, int fallback) => int.TryParse(t.Text, out var v) ? v : fallback;
        bool On(string k) => _sets[k].On.IsChecked == true;
        int Min(string k) => Math.Max(0, N(_sets[k].Min, 0));
        return new PasswordOptions
        {
            Name = "Last used",
            Mode = Modes[Math.Max(0, _mode.SelectedIndex)].Mode,
            Length = N(_length, 20),
            Upper = On("up"), MinUpper = Min("up"),
            Lower = On("lo"), MinLower = Min("lo"),
            Digits = On("dig"), MinDigits = Min("dig"),
            Symbols = On("sym"), MinSymbols = Min("sym"),
            Minus = On("minus"), MinMinus = Min("minus"),
            Underline = On("under"), MinUnderline = Min("under"),
            Space = On("space"), MinSpace = Min("space"),
            Brackets = On("brk"), MinBrackets = Min("brk"),
            Custom = _custom.Text,
            Exclude = _exclude.Text,
            SkipLookAlikes = _lookAlikes.IsChecked == true,
            NoRepeats = _noRepeats.IsChecked == true,
            StartWithLetter = _startLetter.IsChecked == true,
            PronounceableLength = N(_pronLength, 16),
            MixCapitals = _mixCaps.IsChecked == true,
            EndWithDigits = _endDigits.IsChecked == true,
            EndWithSymbol = _endSymbol.IsChecked == true,
            WordCount = N(_words, 4),
            Separator = _separator.Text,
            Capitalize = _capitalize.IsChecked == true,
            AddNumber = _addNumber.IsChecked == true,
            Pattern = _pattern.Text,
            HexLength = N(_hexLength, 32),
            HexUpper = _hexUpper.IsChecked == true,
        };
    }

    private void Apply(PasswordOptions o)
    {
        _loading = true;
        _mode.SelectedIndex = Math.Max(0, Array.FindIndex(Modes, m => m.Mode == o.Mode));
        _length.Text = o.Length.ToString();
        void S(string k, bool on, int min) { _sets[k].On.IsChecked = on; _sets[k].Min.Text = min.ToString(); _sets[k].Min.IsEnabled = on; }
        S("up", o.Upper, o.MinUpper);
        S("lo", o.Lower, o.MinLower);
        S("dig", o.Digits, o.MinDigits);
        S("sym", o.Symbols, o.MinSymbols);
        S("minus", o.Minus, o.MinMinus);
        S("under", o.Underline, o.MinUnderline);
        S("space", o.Space, o.MinSpace);
        S("brk", o.Brackets, o.MinBrackets);
        _custom.Text = o.Custom ?? "";
        _exclude.Text = o.Exclude ?? "";
        _lookAlikes.IsChecked = o.SkipLookAlikes;
        _noRepeats.IsChecked = o.NoRepeats;
        _startLetter.IsChecked = o.StartWithLetter;
        _pronLength.Text = o.PronounceableLength.ToString();
        _mixCaps.IsChecked = o.MixCapitals;
        _endDigits.IsChecked = o.EndWithDigits;
        _endSymbol.IsChecked = o.EndWithSymbol;
        _words.Text = o.WordCount.ToString();
        _separator.Text = o.Separator ?? "";
        _capitalize.IsChecked = o.Capitalize;
        _addNumber.IsChecked = o.AddNumber;
        _pattern.Text = o.Pattern ?? "";
        _hexLength.Text = o.HexLength.ToString();
        _hexUpper.IsChecked = o.HexUpper;
        ShowModePanel();
        _loading = false;
    }

    private void ShowModePanel()
    {
        var mode = Modes[Math.Max(0, _mode.SelectedIndex)].Mode;
        foreach (var (m, panel) in _modePanels) panel.Visibility = m == mode ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OptionChanged()
    {
        if (_loading) return;
        Regenerate();
    }

    // ------------------------------------------------------------------ templates

    private List<PasswordOptions> Templates() => [.. PasswordGenerator.BuiltIn, .. _settings.Templates];

    private void LoadTemplates()
    {
        _loading = true;
        _template.Items.Clear();
        foreach (var t in PasswordGenerator.BuiltIn) _template.Items.Add(t.Name);
        foreach (var t in _settings.Templates) _template.Items.Add(t.Name + "  (yours)");
        _loading = false;
    }

    private bool IsCustomTemplate => _template.SelectedIndex >= PasswordGenerator.BuiltIn.Count;

    private void UpdateDeleteButton() => _deleteTemplate.Visibility = IsCustomTemplate ? Visibility.Visible : Visibility.Collapsed;

    private void SaveTemplate()
    {
        var name = new TextBox { Margin = new Thickness(0, 8, 0, 0), Text = IsCustomTemplate ? _settings.Templates[_template.SelectedIndex - PasswordGenerator.BuiltIn.Count].Name : "" };
        name.Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
        if (ChoiceDialog.Show(this, "Save as template", "Name for these settings. Using the name of one of your templates replaces it.", ["Save", "Cancel"], extra: name) != 0) return;
        var n = name.Text.Trim();
        if (n.Length == 0) return;
        if (PasswordGenerator.BuiltIn.Any(b => b.Name.Equals(n, StringComparison.OrdinalIgnoreCase))) n += " (mine)";
        var o = Read();
        o.Name = n;
        var i = _settings.Templates.FindIndex(t => t.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) _settings.Templates[i] = o; else _settings.Templates.Add(o);
        _settings.Save();
        LoadTemplates();
        _loading = true;
        _template.SelectedIndex = PasswordGenerator.BuiltIn.Count + _settings.Templates.FindIndex(t => t.Name == n);
        _loading = false;
        UpdateDeleteButton();
    }

    private void DeleteTemplate()
    {
        if (!IsCustomTemplate) return;
        var i = _template.SelectedIndex - PasswordGenerator.BuiltIn.Count;
        if (!ChoiceDialog.Confirm(this, "Delete template", $"Delete the template \"{_settings.Templates[i].Name}\"?", "Delete", danger: true)) return;
        _settings.Templates.RemoveAt(i);
        _settings.Save();
        LoadTemplates();
        UpdateDeleteButton();
    }

    // ------------------------------------------------------------------ candidates

    private void Regenerate()
    {
        var count = int.TryParse(_count.Text, out var c) ? Math.Clamp(c, 1, 50) : 10;
        _cands = PasswordGenerator.Batch(Read(), count);
        _sel = 0;
        DrawRows();
    }

    private void DrawRows()
    {
        _rows.Children.Clear();
        for (var i = 0; i < _cands.Count; i++)
        {
            var index = i;
            var cand = _cands[i];
            var g = RowGrid();
            g.Height = 28;
            var n = new TextBlock { Text = (i + 1).ToString(), FontSize = 11, TextAlignment = TextAlignment.Right };
            n.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
            g.Children.Add(Cell(n, 0));
            var pw = Painted(cand.Value);
            pw.FontSize = 13;
            pw.TextTrimming = TextTrimming.CharacterEllipsis;
            g.Children.Add(Cell(pw, 1));
            var len = new TextBlock { Text = cand.Value.Length.ToString(), FontSize = 11.5, TextAlignment = TextAlignment.Right };
            len.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            len.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            g.Children.Add(Cell(len, 2));
            g.Children.Add(Cell(StrengthBar(cand), 3));

            var row = new Border { Child = g, BorderThickness = new Thickness(0, 0, 0, 1), Cursor = Cursors.Hand, Tag = i };
            row.SetResourceReference(Border.BorderBrushProperty, "BorderSoft");
            row.MouseLeftButtonDown += (_, e) =>
            {
                Select(index);
                if (e.ClickCount == 2) Use();
            };
            row.MouseEnter += (_, _) => { if (index != _sel) row.SetResourceReference(Border.BackgroundProperty, "Hover"); };
            row.MouseLeave += (_, _) => { if (index != _sel) row.Background = Brushes.Transparent; };
            _rows.Children.Add(row);
        }
        Select(_sel);
    }

    private void Select(int index)
    {
        if (_cands.Count == 0) { UpdateAnalysis(); return; }
        _sel = Math.Clamp(index, 0, _cands.Count - 1);
        for (var i = 0; i < _rows.Children.Count; i++)
        {
            var row = (Border)_rows.Children[i];
            if (i == _sel) row.SetResourceReference(Border.BackgroundProperty, "Select");
            else row.Background = Brushes.Transparent;
        }
        if (_rows.Children.Count > _sel) ((FrameworkElement)_rows.Children[_sel]).BringIntoView();
        UpdateAnalysis();
    }

    private GeneratedPassword Selected => _cands.Count > _sel ? _cands[_sel] : new("", 0);

    private void UpdateAnalysis()
    {
        var c = Selected;
        _ok.IsEnabled = c.Value.Length > 0;
        _big.Inlines.Clear();
        if (c.Value.Length == 0)
        {
            _big.Inlines.Add(new Run("Pick at least one character set") { Foreground = Res("Faint") });
            _meter.Width = 0;
            _facts.Text = "";
            return;
        }
        Paint(_big.Inlines, c.Value);
        var color = StrengthBrush(c.Strength);
        _meter.Background = color;
        _meter.Width = Math.Max(0, _meterTrack.ActualWidth * Math.Min(1, c.Bits / 128));

        int Count(Func<char, bool> f) => c.Value.Count(f);
        _facts.Inlines.Clear();
        _facts.Inlines.Add(new Bold(new Run(PasswordGenerator.Label(c.Strength))) { Foreground = color });
        _facts.Inlines.Add(new Run("    "));
        _facts.Inlines.Add(new Bold(new Run(Math.Round(c.Bits).ToString())) { Foreground = Res("Text") });
        _facts.Inlines.Add(new Run(" bits    Brute force: "));
        _facts.Inlines.Add(new Bold(new Run(CrackTime(c.Bits))) { Foreground = Res("Text") });
        _facts.Inlines.Add(new Run($"    {Count(char.IsAsciiLetterUpper)} upper · {Count(char.IsAsciiLetterLower)} lower · {Count(char.IsAsciiDigit)} digits · {Count(ch => !char.IsAsciiLetterOrDigit(ch))} special"));
    }

    /// <summary>Time to try half the possibilities at 100 billion guesses a second (an offline attack on a fast hash).</summary>
    private static string CrackTime(double bits)
    {
        var seconds = Math.Pow(2, bits - 1) / 1e11;
        const double year = 365.25 * 86400;
        return seconds switch
        {
            < 1 => "instantly",
            < 60 => "seconds",
            < 3600 => "minutes",
            < 86400 => "hours",
            < year => "days",
            < 100 * year => "years",
            < 1e6 * year => "centuries",
            _ => "millions of years",
        };
    }

    private void Use()
    {
        var pw = Selected.Value;
        if (pw.Length == 0) return;
        _settings.Last = Read();
        _settings.LastTemplate = _template.SelectedIndex >= 0 ? Templates()[_template.SelectedIndex].Name : null;
        _settings.Count = int.TryParse(_count.Text, out var c) ? Math.Clamp(c, 1, 50) : 10;
        _settings.Save();
        if (_standalone) { Copy(); return; }
        Result = pw;
        ResultBits = Selected.Bits;
        DialogResult = true;
    }

    private void Copy()
    {
        if (Selected.Value.Length == 0) return;
        CredentialClipboard.CopyText(Selected.Value, secret: true);
        Title = "Password generator · copied, the clipboard clears in 30 s";
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5) { Regenerate(); e.Handled = true; return; }
        if (Keyboard.FocusedElement is TextBox or ComboBox) return;
        if (e.Key == Key.Down) { Select(_sel + 1); e.Handled = true; }
        else if (e.Key == Key.Up) { Select(_sel - 1); e.Handled = true; }
    }

    // ------------------------------------------------------------------ small builders

    private static Brush Res(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    private static Brush StrengthBrush(PasswordStrength s) => s switch
    {
        PasswordStrength.Weak => Res("Bad"),
        PasswordStrength.Fair => Res("Warn"),
        _ => Res("Ok"),
    };

    /// <summary>The password in the mono font, digits and specials coloured so they're easy to tell apart.</summary>
    private static TextBlock Painted(string value)
    {
        var t = new TextBlock();
        t.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        t.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        Paint(t.Inlines, value);
        return t;
    }

    private static void Paint(InlineCollection inlines, string value)
    {
        static int Kind(char c) => char.IsAsciiDigit(c) ? 1 : char.IsAsciiLetter(c) ? 0 : 2;
        var start = 0;
        for (var i = 1; i <= value.Length; i++)
        {
            if (i < value.Length && Kind(value[i]) == Kind(value[start])) continue;
            var run = new Run(value[start..i].Replace(' ', '·'));
            switch (Kind(value[start]))
            {
                case 1: run.Foreground = Res("Link"); break;
                case 2: run.Foreground = Res("Warn"); break;
            }
            inlines.Add(run);
            start = i;
        }
    }

    private static FrameworkElement StrengthBar(GeneratedPassword c)
    {
        var d = new DockPanel();
        var label = new TextBlock { Text = PasswordGenerator.Label(c.Strength), FontSize = 10.5, Width = 62, Margin = new Thickness(6, 0, 0, 0) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
        DockPanel.SetDock(label, Dock.Right);
        d.Children.Add(label);
        var track = new Grid { Height = 5, VerticalAlignment = VerticalAlignment.Center };
        var bg = new Border { CornerRadius = new CornerRadius(2) };
        bg.SetResourceReference(Border.BackgroundProperty, "BorderSoft");
        track.Children.Add(bg);
        var fill = c.Strength switch { PasswordStrength.Weak => 0.22, PasswordStrength.Fair => 0.48, PasswordStrength.Strong => 0.75, _ => 1.0 };
        var bar = new Grid();
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fill, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - fill, GridUnitType.Star) });
        bar.Children.Add(new Border { Background = StrengthBrush(c.Strength), CornerRadius = new CornerRadius(2) });
        track.Children.Add(bar);
        d.Children.Add(track);
        return d;
    }

    private static Grid RowGrid()
    {
        var g = new Grid { Margin = new Thickness(10, 0, 10, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        return g;
    }

    private static FrameworkElement Cell(FrameworkElement el, int col)
    {
        el.VerticalAlignment = VerticalAlignment.Center;
        el.Margin = new Thickness(col == 0 ? 0 : 8, 0, 0, 0);
        Grid.SetColumn(el, col);
        return el;
    }

    private static TextBlock HeadText(string text, TextAlignment align = TextAlignment.Left)
    {
        var t = new TextBlock { Text = text.ToUpperInvariant(), FontSize = 10.5, TextAlignment = align };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
        return t;
    }

    private static TextBlock Heading(string text)
    {
        var t = new TextBlock { Text = text.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 2), VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
        return t;
    }

    private static TextBlock Muted(string text, Thickness margin = default, double size = 12)
    {
        var t = new TextBlock { Text = text, Margin = margin, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return t;
    }

    private static TextBlock Hint(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
        return t;
    }

    private static Border Rule()
    {
        var b = new Border { Height = 1, Margin = new Thickness(0, 9, 0, 7) };
        b.SetResourceReference(Border.BackgroundProperty, "BorderSoft");
        return b;
    }

    private static Button LinkButton(string text)
    {
        var b = new Button { Content = text, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
        b.SetResourceReference(StyleProperty, "FlatButton");
        b.SetResourceReference(ForegroundProperty, "Link");
        return b;
    }

    private FrameworkElement Labeled(string label, Control field)
    {
        var d = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };
        var l = Muted(label);
        l.Width = 72;
        d.Children.Add(l);
        d.Children.Add(field);
        return d;
    }

    private static TextBox TextField(string? tooltip, double width = double.NaN)
    {
        var t = new TextBox { ToolTip = tooltip, Width = width, HorizontalAlignment = double.IsNaN(width) ? HorizontalAlignment.Stretch : HorizontalAlignment.Left };
        t.SetResourceReference(FontFamilyProperty, "MonoFont");
        return t;
    }

    /// <summary>A small number box: type a number, or use the mouse wheel or the arrow keys.</summary>
    private static TextBox NumBox(int min, int max)
    {
        var t = new TextBox { Width = 50, TextAlignment = TextAlignment.Right };
        t.SetResourceReference(FontFamilyProperty, "MonoFont");
        void Step(int by)
        {
            var v = int.TryParse(t.Text, out var x) ? x : min;
            t.Text = Math.Clamp(v + by, min, max).ToString();
            t.CaretIndex = t.Text.Length;
        }
        t.PreviewMouseWheel += (_, e) => { if (t.IsKeyboardFocusWithin || t.IsMouseOver) { Step(e.Delta > 0 ? 1 : -1); e.Handled = true; } };
        t.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Up) { Step(1); e.Handled = true; }
            else if (e.Key == Key.Down) { Step(-1); e.Handled = true; }
        };
        t.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsAsciiDigit);
        t.LostKeyboardFocus += (_, _) => { if (!int.TryParse(t.Text, out var v) || v < min || v > max) t.Text = Math.Clamp(int.TryParse(t.Text, out v) ? v : min, min, max).ToString(); };
        return t;
    }

    private FrameworkElement Spinner(TextBox box, string unit)
    {
        var d = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        d.Children.Add(box);
        d.Children.Add(Muted(unit, new Thickness(6, 0, 0, 0)));
        return d;
    }

    private static void AutomationPropertiesName(DependencyObject o, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(o, name);
}
