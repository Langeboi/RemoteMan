using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RemoteMan.App.Services;
using RemoteMan.Gateway;

namespace RemoteMan.App.Views;

/// <summary>
/// The box the Gateway pill (bottom left) opens: This PC → relay → site with bubbles drifting along the wires,
/// relay latency, open tunnels, a traffic sparkline and the sites. It only animates while it's open.
/// </summary>
public sealed class GatewayStatusBox : Border
{
    private readonly GatewayFlow _flow = new() { Height = 104 };
    private readonly TextBlock _state = new() { FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _chip = new() { CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 1, 8, 2), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _sub = Muted(11);
    private readonly TextBlock _latency = Big(), _tunnels = Big(), _rate = Big();
    private readonly Sparkline _spark = new() { Height = 22, Margin = new Thickness(0, 4, 0, 0) };
    private readonly StackPanel _sites = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private string _sitesKey = "";
    private readonly Border[] _modeButtons = new Border[3];

    /// <summary>"Gateway settings…" was clicked.</summary>
    public event Action? SettingsRequested;

    public GatewayStatusBox()
    {
        Width = 330;
        CornerRadius = new CornerRadius(10);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(14, 12, 14, 10);
        SetResourceReference(BackgroundProperty, "ButtonBg");
        SetResourceReference(BorderBrushProperty, "ButtonBorder");
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.45, Color = Colors.Black };
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Bad");
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "Warn");

        var title = new TextBlock { Text = "Gateway", FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextStrong");
        _chip.Child = _state;
        var head = new DockPanel();
        DockPanel.SetDock(_chip, Dock.Right);
        head.Children.Add(_chip);
        head.Children.Add(title);

        var modes = new Grid { Margin = new Thickness(0, 10, 0, 2) };
        var modeTrack = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(2), Child = modes };
        modeTrack.SetResourceReference(BackgroundProperty, "Inset");
        (GatewayMode Mode, string Label, string Tip)[] options =
        [
            (GatewayMode.Auto, "Auto", "Directly when the host answers, through the gateway when it doesn't"),
            (GatewayMode.Connected, "Connected", "Everything goes through the gateway"),
            (GatewayMode.Disconnected, "Disconnected", "Nothing goes through the gateway: direct connections only"),
        ];
        for (var i = 0; i < options.Length; i++)
        {
            var (mode, label, tip) = options[i];
            modes.ColumnDefinitions.Add(new ColumnDefinition());
            var text = new TextBlock { Text = label, FontSize = 11.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
            var b = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(0, 5, 0, 6), Child = text, Cursor = Cursors.Hand, ToolTip = tip, Background = Brushes.Transparent };
            b.MouseLeftButtonUp += (_, _) => GatewayService.Instance.SetMode(mode);
            Grid.SetColumn(b, i);
            modes.Children.Add(b);
            _modeButtons[i] = b;
        }

        var kpis = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        for (var i = 0; i < 3; i++) kpis.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(i == 2 ? 1.4 : 1, GridUnitType.Star) });
        kpis.Children.Add(Kpi("Relay latency", _latency, null, 0));
        kpis.Children.Add(Kpi("Tunnels", _tunnels, null, 1));
        kpis.Children.Add(Kpi("Traffic", _rate, _spark, 2));

        var sitesHead = Muted(10);
        sitesHead.Text = "SITES";
        sitesHead.Margin = new Thickness(0, 12, 0, 0);

        var settings = new TextBlock { Text = "Gateway settings…", FontSize = 12, Cursor = Cursors.Hand, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        settings.SetResourceReference(TextBlock.ForegroundProperty, "Link");
        settings.MouseLeftButtonUp += (_, _) => SettingsRequested?.Invoke();

        var rule = new Border { Height = 1, Margin = new Thickness(-14, 10, -14, 0) };
        rule.SetResourceReference(BackgroundProperty, "BorderSoft");

        Child = new StackPanel { Children = { head, _sub, modeTrack, _flow, _error, kpis, sitesHead, _sites, _hint, rule, settings } };

        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                GatewayService.Instance.Changed += OnChanged;
                GatewayService.Instance.RefreshSoon();
                Update();
            }
            else GatewayService.Instance.Changed -= OnChanged;
        };
    }

    private void OnChanged() => Dispatcher.BeginInvoke(Update);

    private void Update()
    {
        var gw = GatewayService.Instance;
        var site = gw.ActiveSite;
        var siteInfo = gw.LiveSites.FirstOrDefault(s => string.Equals(s.Name, site, StringComparison.OrdinalIgnoreCase));
        var open = GatewayActivity.OpenTunnels;
        var opening = GatewayActivity.Opening > 0;

        var off = gw.Mode == GatewayMode.Disconnected;
        for (var i = 0; i < _modeButtons.Length; i++)
        {
            var on = (int)gw.Mode == i;
            var accent = Themes.GetColor(i == 2 ? "Faint" : i == 1 ? "Ok" : "Accent");
            _modeButtons[i].Background = on ? new SolidColorBrush(Color.FromArgb(0x40, accent.R, accent.G, accent.B)) : Brushes.Transparent;
            ((TextBlock)_modeButtons[i].Child).Foreground = Themes.GetBrush(on ? "TextStrong" : "Muted");
        }
        var (text, key) = off ? ("Disconnected", "Faint") : gw.State switch
        {
            GatewayState.Down => ("Relay down", "Bad"),
            GatewayState.Connecting => ("Connecting", "Accent"),
            _ when gw.LiveSites.Count == 0 => ("No sites", "Warn"),
            _ when siteInfo is { Online: false } => ("Site offline", "Warn"),
            _ when opening => ("Opening tunnel", "Accent"),
            _ when open > 0 => ("Connected", "Ok"),
            _ => ("Ready", "Ok"),
        };
        _state.Text = text;
        _state.Foreground = Themes.GetBrush(key);
        var tint = Themes.GetColor(key);
        _chip.Background = new SolidColorBrush(Color.FromArgb(0x26, tint.R, tint.G, tint.B));
        _sub.Text = $"{gw.RelayHost}{(gw.Device is { } d ? $" · this PC is {d}" : "")}";

        _flow.Relay = gw.RelayHost ?? "Relay";
        _flow.Site = site ?? "Site";
        _flow.Mode = off ? FlowMode.Off : gw.State switch
        {
            GatewayState.Down => FlowMode.RelayDown,
            GatewayState.Connecting => FlowMode.Waiting,
            _ when siteInfo is null || !siteInfo.Online => FlowMode.SiteDown,
            _ when opening => FlowMode.Waiting,
            _ => FlowMode.Up,
        };
        _flow.Activity = open + (gw.InPerSecond + gw.OutPerSecond) / 20_000.0;

        _error.Text = gw.Error ?? "";
        _error.Visibility = gw.State == GatewayState.Down && gw.Error is not null ? Visibility.Visible : Visibility.Collapsed;

        // The gateway only carries what names a site; say so when nothing has gone through it yet.
        var idle = !off && open == 0 && !opening && GatewayActivity.BytesIn == 0 && gw.State == GatewayState.Ok && gw.LiveSites.Count > 0;
        var usage = idle ? gw.UsageSummary?.Invoke() : null;
        _hint.Text = !idle ? "" : usage is null
            ? $"Nothing uses the gateway yet. Pick {site ?? "a site"} under Gateway on a folder or entry (Edit > Advanced > Connection), " +
              "or for the database in Data sources (then restart)."
            : $"Set to use it: {usage}. Nothing has needed it yet: each connection first tries directly, unless Always is ticked. " +
              "A data source change applies after a restart.";
        _hint.Visibility = idle ? Visibility.Visible : Visibility.Collapsed;
        _latency.Text = gw.Latency is { } l ? $"{l.TotalMilliseconds:0} ms" : "–";
        _tunnels.Text = opening ? $"{open} +1" : open.ToString(CultureInfo.CurrentCulture);
        _rate.Text = Rate(gw.InPerSecond + gw.OutPerSecond);
        _spark.Values = gw.Traffic;

        // Rebuild the site rows only when something about them changed (this runs every second).
        var key2 = string.Join("|", gw.LiveSites.Select(s => $"{s.Name}:{s.Online}:{s.Connectors}:{GatewayActivity.OpenFor(s.Name)}")) + gw.State;
        if (key2 == _sitesKey) return;
        _sitesKey = key2;
        _sites.Children.Clear();
        if (gw.LiveSites.Count == 0)
        {
            var none = Muted(11);
            none.Text = gw.State == GatewayState.Ok ? "The relay has no sites for this PC yet (gateway site add <name>)." : "–";
            none.TextWrapping = TextWrapping.Wrap;
            _sites.Children.Add(none);
        }
        foreach (var s in gw.LiveSites) _sites.Children.Add(SiteRow(s, gw.State == GatewayState.Ok));
    }

    private static FrameworkElement SiteRow(SiteInfo s, bool relayUp)
    {
        var online = relayUp && s.Online;
        var led = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Margin = new Thickness(1, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        led.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, online ? "Ok" : relayUp ? "Bad" : "Faint");
        var name = new TextBlock { Text = s.Name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var open = GatewayActivity.OpenFor(s.Name);
        var info = Muted(11);
        info.VerticalAlignment = VerticalAlignment.Center;
        info.Text = !relayUp ? "unknown"
            : !s.Online ? "offline"
            : $"{s.Connectors} connector{(s.Connectors == 1 ? "" : "s")}{(open > 0 ? $" · {open} open" : "")}";
        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        DockPanel.SetDock(info, Dock.Right);
        row.Children.Add(info);
        row.Children.Add(led);
        row.Children.Add(name);
        row.ToolTip = s.Keys.Count > 0 ? $"Connector key {string.Join(", ", s.Fingerprints.Select(f => f[..16]))}" : null;
        return row;
    }

    private static FrameworkElement Kpi(string label, TextBlock value, FrameworkElement? extra, int column)
    {
        var l = Muted(10);
        l.Text = label.ToUpperInvariant();
        var p = new StackPanel { Margin = new Thickness(0, 0, column < 2 ? 8 : 0, 0) };
        p.Children.Add(l);
        p.Children.Add(value);
        if (extra is not null) p.Children.Add(extra);
        Grid.SetColumn(p, column);
        return p;
    }

    private static TextBlock Muted(double size)
    {
        var t = new TextBlock { FontSize = size };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        return t;
    }

    private static TextBlock Big()
    {
        var t = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 1, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextStrong");
        return t;
    }

    internal static string Rate(double bytesPerSecond) => bytesPerSecond switch
    {
        < 1 => "idle",
        < 1024 => $"{bytesPerSecond:0} B/s",
        < 1024 * 1024 => $"{bytesPerSecond / 1024:0.#} KB/s",
        _ => $"{bytesPerSecond / 1048576:0.#} MB/s",
    };
}

public enum FlowMode { Up, Waiting, SiteDown, RelayDown, Off }

/// <summary>
/// This PC → relay → site, drawn as three nodes on two wires with glowing bubbles that drift end to end at random:
/// random sizes, speeds and wobble, both ways, in bursts, busier with more traffic. Blue and one way while waiting;
/// a broken red wire where the path stops.
/// </summary>
public sealed class GatewayFlow : FrameworkElement
{
    private struct Bubble
    {
        public double T, Speed, Radius, Amp, Freq, Phase;
        public int Dir;
        public int Hue;
    }

    private static readonly Geometry PcGlyph = Geometry.Parse("M-8,-6 h16 v10 h-16 z M-4,8 h8 M0,4 v4");
    private static readonly Geometry RelayGlyph = Geometry.Parse("M-7,4 a4,4 0 0 1 0.6,-7.8 a5.2,5.2 0 0 1 9.8,0.8 a3.6,3.6 0 0 1 3.4,7 z");
    private static readonly Geometry SiteGlyph = Geometry.Parse("M-6,7 v-13 h8 v13 M2,-1 h5 v8 M-3.5,-3 h0 M-0.5,-3 h0 M-3.5,0 h0 M-0.5,0 h0 M-3.5,3 h0 M-0.5,3 h0 M-9,7 h18");

    private readonly List<Bubble> _bubbles = [];
    private readonly Random _rng = new();
    private readonly Stopwatch _clock = new();
    private TimeSpan _last;
    private double _spawnIn, _burst;
    private Brush[] _glow = [], _core = [];
    private FlowMode _paletteFor = (FlowMode)(-1);

    public string Relay { get; set; } = "Relay";
    public string Site { get; set; } = "Site";
    public FlowMode Mode { get; set; } = FlowMode.Waiting;
    /// <summary>Open tunnels plus a little per traffic: more means more bubbles.</summary>
    public double Activity { get; set; }

    public GatewayFlow()
    {
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _clock.Restart();
                _last = TimeSpan.Zero;
                CompositionTarget.Rendering += OnFrame;
            }
            else
            {
                CompositionTarget.Rendering -= OnFrame;
                _bubbles.Clear();
            }
        };
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed;
        var dt = Math.Min(0.05, (now - _last).TotalSeconds);
        _last = now;
        Step(dt);
        InvalidateVisual();
    }

    private void Step(double dt)
    {
        // Spawn: a random gap that shrinks with activity, and now and then a burst of a few at once.
        var rate = Mode switch
        {
            FlowMode.Up => 1.4 + Math.Min(10, Activity * 2.2),
            FlowMode.Waiting => 3.0,
            _ => 1.0,
        };
        _spawnIn -= dt;
        if (_spawnIn <= 0 && Mode != FlowMode.Off)
        {
            Spawn();
            if (_burst <= 0 && _rng.NextDouble() < (Mode == FlowMode.Up ? 0.12 : 0.05)) _burst = 2 + _rng.Next(4);
            if (_burst > 0) { _burst--; _spawnIn = 0.04 + _rng.NextDouble() * 0.08; }
            else _spawnIn = -Math.Log(1 - _rng.NextDouble()) / rate;   // exponential gaps look random, not metronomic
        }

        var stopAt = StopAt;
        for (var i = _bubbles.Count - 1; i >= 0; i--)
        {
            var b = _bubbles[i];
            b.T += b.Dir * b.Speed * dt;
            _bubbles[i] = b;
            if (b.T > Math.Min(1.02, stopAt + 0.06) || b.T < -0.02) _bubbles.RemoveAt(i);
        }
    }

    /// <summary>How far along the path bubbles get: into the broken wire a little, then they fade.</summary>
    private double StopAt => Mode switch { FlowMode.RelayDown => 0.22, FlowMode.SiteDown => 0.72, _ => 1.0 };

    private void Spawn()
    {
        if (_bubbles.Count > 140) return;
        var back = Mode == FlowMode.Up && _rng.NextDouble() < 0.45;
        _bubbles.Add(new Bubble
        {
            Dir = back ? -1 : 1,
            T = back ? 1 : 0,
            Speed = 0.18 + _rng.NextDouble() * 0.37,
            Radius = 1.2 + Math.Pow(_rng.NextDouble(), 1.8) * 2.4,
            Amp = 1 + _rng.NextDouble() * 5,
            Freq = 4 + _rng.NextDouble() * 9,
            Phase = _rng.NextDouble() * Math.PI * 2,
            Hue = _rng.Next(3),
        });
    }

    private void EnsurePalette()
    {
        if (_paletteFor == Mode && _glow.Length > 0) return;
        _paletteFor = Mode;
        Color[] hues = Mode == FlowMode.Up
            ? [Color.FromRgb(0x34, 0xd3, 0x99), Color.FromRgb(0x2d, 0xd4, 0xbf), Color.FromRgb(0x86, 0xef, 0xac)]
            : [Color.FromRgb(0x60, 0xa5, 0xfa), Color.FromRgb(0x38, 0xbd, 0xf8), Color.FromRgb(0x93, 0xc5, 0xfd)];
        _glow = hues.Select(c => Freeze(new RadialGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(0xb0, c.R, c.G, c.B), 0),
            new(Color.FromArgb(0x40, c.R, c.G, c.B), 0.45),
            new(Color.FromArgb(0x00, c.R, c.G, c.B), 1),
        }))).ToArray();
        _core = hues.Select(c => Freeze(new SolidColorBrush(Color.FromArgb(0xf0,
            (byte)Math.Min(255, c.R + 60), (byte)Math.Min(255, c.G + 40), (byte)Math.Min(255, c.B + 40))))).ToArray();
    }

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    protected override void OnRender(DrawingContext dc)
    {
        EnsurePalette();
        var w = ActualWidth; var h = ActualHeight;
        if (w < 60) return;
        var y = h * 0.42;
        double x0 = 26, x2 = w - 26, x1 = w / 2;
        var t = _clock.Elapsed.TotalSeconds;

        var ok = Themes.GetColor("Ok"); var bad = Themes.GetColor("Bad"); var accent = Themes.GetColor("Accent");
        var muted = Themes.GetBrush("Muted"); var strong = Themes.GetBrush("TextStrong");
        var off = Mode == FlowMode.Off;
        var line = off ? Themes.GetColor("Faint") : Mode == FlowMode.Up ? ok : accent;

        // Wires: a soft wide glow under a thin line; the broken one red and dashed.
        void Wire(double a, double b, bool broken)
        {
            var c = broken ? bad : line;
            if (!broken) dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(0x22, c.R, c.G, c.B)), 7), new Point(a + 20, y), new Point(b - 20, y));
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(broken ? (byte)0xc0 : (byte)0x70, c.R, c.G, c.B)), 1.4);
            if (broken) pen.DashStyle = new DashStyle([3, 3], t * -6 % 6);
            dc.DrawLine(pen, new Point(a + 20, y), new Point(b - 20, y));
        }
        Wire(x0, x1, Mode == FlowMode.RelayDown);
        Wire(x1, x2, Mode == FlowMode.SiteDown);

        // Bubbles, clipped to where the path still works.
        var stop = StopAt;
        foreach (var b in _bubbles)
        {
            var x = x0 + (x2 - x0) * b.T;
            // Squeeze the wobble to nothing at the nodes so bubbles leave and enter them cleanly.
            var near = Math.Min(Math.Abs(x - x0), Math.Min(Math.Abs(x - x1), Math.Abs(x - x2)));
            var squeeze = Math.Clamp((near - 16) / 26, 0, 1);
            var by = y + Math.Sin(b.Phase + b.T * b.Freq + t * 1.3) * b.Amp * squeeze;
            var fade = Math.Clamp((stop - b.T) / 0.08, 0, 1) * Math.Clamp(near > 18 ? 1 : (near - 10) / 8, 0, 1);
            if (fade <= 0.01) continue;
            dc.PushOpacity(fade);
            dc.DrawEllipse(_glow[b.Hue], null, new Point(x, by), b.Radius * 3.2, b.Radius * 3.2);
            dc.DrawEllipse(_core[b.Hue], null, new Point(x, by), b.Radius * 0.75, b.Radius * 0.75);
            dc.Pop();
        }

        // Nodes, with a slow breathing ring on the ones that are up.
        void Node(double x, Geometry glyph, string label, string caption, bool up, bool down, double phase)
        {
            var c = down ? bad : up ? (Mode == FlowMode.Up ? ok : accent) : Themes.GetColor("Faint");
            var pulse = (Math.Sin(t * 2.2 + phase) + 1) / 2;
            if (up && !down)
                dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(0x50 * (1 - pulse)), c.R, c.G, c.B)), 1.2), new Point(x, y), 19 + pulse * 6, 19 + pulse * 6);
            dc.DrawEllipse(Themes.GetBrush("Panel2"), new Pen(new SolidColorBrush(Color.FromArgb(0xd0, c.R, c.G, c.B)), 1.5), new Point(x, y), 18, 18);
            dc.PushTransform(new TranslateTransform(x, y));
            dc.DrawGeometry(null, new Pen(down ? new SolidColorBrush(bad) : strong, 1.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, glyph);
            dc.Pop();
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var maxW = x1 - x0 - 6;
            Text(dc, label, x, y + 24, 11, strong, FontWeights.SemiBold, maxW, dpi);
            Text(dc, caption, x, y + 38, 10, down ? new SolidColorBrush(bad) : muted, FontWeights.Normal, maxW, dpi);
        }
        var relayDown = Mode == FlowMode.RelayDown;
        var siteDown = Mode == FlowMode.SiteDown;
        Node(x0, PcGlyph, "This PC", "", !off, false, 0);
        Node(x1, RelayGlyph, "Relay", off ? "not used" : relayDown ? "unreachable" : Shorten(Relay), !off && !relayDown, relayDown, 1.7);
        Node(x2, SiteGlyph, Shorten(Site), siteDown ? "offline" : relayDown || off ? "" : "site", !off && !relayDown && !siteDown, siteDown, 3.1);
    }

    private static string Shorten(string s) => s.Length > 22 ? s[..21] + "…" : s;

    private static void Text(DrawingContext dc, string s, double cx, double top, double size, Brush brush, FontWeight weight, double maxW, double dpi)
    {
        if (s.Length == 0) return;
        var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal), size, brush, dpi)
        { MaxTextWidth = Math.Max(20, maxW), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center };
        dc.DrawText(ft, new Point(cx - ft.MaxTextWidth / 2, top));
    }
}

/// <summary>The last minute of gateway traffic as a small filled line.</summary>
public sealed class Sparkline : FrameworkElement
{
    private double[] _values = [];
    public double[] Values { get => _values; set { _values = value; InvalidateVisual(); } }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        if (_values.Length < 2 || w < 10) return;
        var max = Math.Max(1024, _values.Max());
        var c = Themes.GetColor("Ok");
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(0, h), true, true);
            for (var i = 0; i < _values.Length; i++)
                ctx.LineTo(new Point(w * i / (_values.Length - 1), h - 1 - (h - 2) * Math.Sqrt(_values[i] / max)), true, true);
            ctx.LineTo(new Point(w, h), false, false);
        }
        g.Freeze();
        var fill = new LinearGradientBrush(Color.FromArgb(0x60, c.R, c.G, c.B), Color.FromArgb(0x05, c.R, c.G, c.B), 90);
        dc.DrawGeometry(fill, new Pen(new SolidColorBrush(c), 1.2) { LineJoin = PenLineJoin.Round }, g);
    }
}
