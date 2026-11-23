using System.Windows;
using System.Windows.Media;

namespace RemoteMan.App.Services;

/// <summary>One colour theme: a name, a line about it and the colours it sets. Colours it leaves out are worked out from the others.</summary>
public sealed record ThemeDef(string Id, string Name, string Description, IReadOnlyDictionary<string, string> Colors)
{
    /// <summary>Every colour key, with the ones this theme leaves out filled in.</summary>
    public IReadOnlyDictionary<string, Color> Resolve() => Themes.Resolve(this);
}

/// <summary>
/// The app's colour themes. Dark.xaml holds the classic teal values; at startup, before any window is built,
/// <see cref="Apply"/> writes the chosen theme's colours over those keys. Everything in the app looks its colours up
/// with StaticResource, so a different theme takes effect on the next start.
/// </summary>
/// <summary>How much light and shadow the controls get. Subtle is the default.</summary>
public enum Shading { Flat, Subtle, Raised }

public static class Themes
{
    public const string DefaultId = "graphite";

    /// <summary>The theme that was applied at startup.</summary>
    public static ThemeDef Current { get; private set; } = null!;

    private static Dictionary<string, string> D(params (string Key, string Value)[] p) => p.ToDictionary(x => x.Key, x => x.Value);

    public static IReadOnlyList<ThemeDef> All { get; } =
    [
        new("graphite", "Graphite + Cobalt", "Neutral dark greys with a clear cobalt blue. Closest to RDM.", D(
            ("Bg", "#111214"), ("Panel", "#18191C"), ("Panel2", "#141518"), ("Work", "#0C0D0E"), ("Border", "#2C2E32"), ("BorderSoft", "#232528"),
            ("Hover", "#1E2023"), ("Button", "#1E2023"), ("ButtonBorder", "#383B40"), ("InputBorder", "#303338"),
            ("Accent", "#3B82F6"), ("OnAccent", "#FFFFFF"), ("Link", "#8AB4F8"), ("SelectRow", "#1F2A3A"), ("EntryIcon", "#6FA3EE"), ("FolderIcon", "#7F8794"))),
        new("midnight", "Midnight Navy", "The whole window in deep navy, with a bright blue accent.", D(
            ("Bg", "#0C111A"), ("Panel", "#111827"), ("Panel2", "#0F1521"), ("Work", "#080C13"), ("Border", "#223047"), ("BorderSoft", "#1A2436"),
            ("Hover", "#162033"), ("Button", "#152035"), ("ButtonBorder", "#2C3D5A"), ("InputBorder", "#283852"),
            ("Text", "#E1E7F0"), ("TextStrong", "#F3F6FA"), ("Muted", "#94A3B8"), ("Faint", "#7486A0"),
            ("Accent", "#4C8DFF"), ("OnAccent", "#FFFFFF"), ("Link", "#93B9FF"), ("SelectRow", "#1C2C47"), ("EntryIcon", "#7FAEF5"), ("FolderIcon", "#7A8BA6"))),
        new("steel", "Slate Steel", "Charcoal greys with a muted steel blue. Quiet, very little colour.", D(
            ("Bg", "#121314"), ("Panel", "#1A1B1D"), ("Panel2", "#161719"), ("Work", "#0D0E0F"), ("Border", "#2E3033"), ("BorderSoft", "#25272A"),
            ("Hover", "#202225"), ("Button", "#202225"), ("ButtonBorder", "#3A3D41"), ("InputBorder", "#33363A"),
            ("Text", "#E2E4E7"), ("TextStrong", "#F3F4F6"), ("Muted", "#9A9FA6"), ("Faint", "#7E848C"),
            ("Accent", "#6E8FBA"), ("OnAccent", "#0D1520"), ("Link", "#A3BCDC"), ("SelectRow", "#262D36"), ("EntryIcon", "#8AA6CC"), ("FolderIcon", "#858A92"))),
        new("abyss", "Abyss", "Near-black blue-grey, deep royal blue on buttons, lighter blue for lines and links.", D(
            ("Bg", "#0E1015"), ("Panel", "#14171F"), ("Panel2", "#11141B"), ("Work", "#0A0B0F"), ("Border", "#262B38"), ("BorderSoft", "#1D212B"),
            ("Hover", "#191D27"), ("Button", "#181C26"), ("ButtonBorder", "#323849"), ("InputBorder", "#2C3242"),
            ("Accent", "#5B86E8"), ("AccentDim", "#2F5FD0"), ("OnAccent", "#FFFFFF"), ("Link", "#86A8F0"), ("SelectRow", "#1B2540"), ("EntryIcon", "#7096E6"), ("FolderIcon", "#7C849A"))),
        new("fjord", "Fjord", "Cool arctic blue-greys with a frosty ice-blue accent. A little lighter than the rest.", D(
            ("Bg", "#1A1E26"), ("Panel", "#20252E"), ("Panel2", "#1D222A"), ("Work", "#161A21"), ("Border", "#343B48"), ("BorderSoft", "#2A303B"),
            ("Hover", "#272D38"), ("Button", "#262C37"), ("ButtonBorder", "#3E4757"), ("InputBorder", "#394150"),
            ("Text", "#E5E9F0"), ("TextStrong", "#F2F4F8"), ("Muted", "#A3ACBC"), ("Faint", "#8590A2"),
            ("Accent", "#88C0D0"), ("OnAccent", "#14202A"), ("Link", "#A9D2DE"), ("EntryIcon", "#81A1C1"), ("FolderIcon", "#8794A8"))),
        new("copper", "Copper", "Warm graphite with a burnished copper accent, like an old rack of gear.", D(
            ("Bg", "#141211"), ("Panel", "#1B1917"), ("Panel2", "#171513"), ("Work", "#0F0E0D"), ("Border", "#312C28"), ("BorderSoft", "#27231F"),
            ("Hover", "#221F1C"), ("Button", "#221F1C"), ("ButtonBorder", "#3D3631"), ("InputBorder", "#37312C"),
            ("Text", "#ECE6E0"), ("TextStrong", "#F7F3EF"), ("Muted", "#ABA197"), ("Faint", "#8C8278"),
            ("Accent", "#D9844A"), ("OnAccent", "#1A0F08"), ("Link", "#EBAA7E"), ("EntryIcon", "#7FA6D6"), ("FolderIcon", "#998A7C"))),
        new("amethyst", "Amethyst", "Dark grey with a violet tint and a soft purple accent.", D(
            ("Bg", "#121117"), ("Panel", "#19171F"), ("Panel2", "#15141B"), ("Work", "#0D0C11"), ("Border", "#2E2B38"), ("BorderSoft", "#24222D"),
            ("Hover", "#1F1D27"), ("Button", "#1F1D27"), ("ButtonBorder", "#3A3646"), ("InputBorder", "#34303F"),
            ("Text", "#E6E3EE"), ("TextStrong", "#F5F3FA"), ("Muted", "#A39DB3"), ("Faint", "#857F96"),
            ("Accent", "#9D7CF2"), ("OnAccent", "#FFFFFF"), ("Link", "#BFA9F7"), ("EntryIcon", "#8FA6F0"), ("FolderIcon", "#8C86A0"))),
        new("synthwave", "Synthwave", "Deep purple night with hot pink and neon cyan. Loud on purpose.", D(
            ("Bg", "#140F22"), ("Panel", "#1B1430"), ("Panel2", "#171128"), ("Work", "#0E0A18"), ("Border", "#34284F"), ("BorderSoft", "#281F3F"),
            ("Hover", "#221A38"), ("Button", "#221A38"), ("ButtonBorder", "#45366A"), ("InputBorder", "#3D305E"),
            ("Text", "#ECE4FA"), ("TextStrong", "#FBF7FF"), ("Muted", "#AE9FCB"), ("Faint", "#8E7FAE"),
            ("Accent", "#FF4FA3"), ("OnAccent", "#1A0B1F"), ("Link", "#5CE1FF"), ("SelectRow", "#35204D"), ("EntryIcon", "#5CE1FF"), ("FolderIcon", "#9A86C4"))),
        new("oled", "OLED Black", "True black with white highlights and no colour at all. Easy on OLED screens at night.", D(
            ("Bg", "#000000"), ("Panel", "#0A0A0A"), ("Panel2", "#050505"), ("Work", "#000000"), ("Border", "#262626"), ("BorderSoft", "#1A1A1A"),
            ("Hover", "#141414"), ("Button", "#121212"), ("ButtonBorder", "#333333"), ("InputBorder", "#2E2E2E"),
            ("Text", "#E6E6E6"), ("TextStrong", "#FFFFFF"), ("Muted", "#A0A0A0"), ("Faint", "#808080"),
            ("Accent", "#F2F2F2"), ("AccentDim", "#3A3A3A"), ("OnAccent", "#000000"), ("Link", "#D0D0D0"), ("SelectRow", "#1F1F1F"),
            ("EntryIcon", "#C8C8C8"), ("FolderIcon", "#8A8A8A"))),
        new("teal", "Teal (classic)", "The original RemoteMan look.", D()),
    ];

    /// <summary>The classic values (Dark.xaml), used for anything a theme leaves out and can't be worked out.</summary>
    private static readonly Dictionary<string, string> Classic = D(
        ("Bg", "#0F1113"), ("Panel", "#16191C"), ("Panel2", "#131619"), ("Header", "#16191C"), ("Border", "#2A2F35"), ("BorderSoft", "#22272C"),
        ("Work", "#0B0D0F"), ("Text", "#E3E6EA"), ("TextStrong", "#F4F6F8"), ("Muted", "#9AA3AD"), ("Faint", "#7D8690"), ("Select", "#22282D"),
        ("Hover", "#1C2024"), ("Accent", "#3FB8A4"), ("InputBorder", "#2E343A"), ("Button", "#1D2125"), ("ButtonBorder", "#353B42"),
        ("Rail", "#121417"), ("EntryIcon", "#5B9BEA"), ("FolderIcon", "#7D8A99"),
        ("ButtonHover", "#262B30"), ("AccentDim", "#1F5E55"), ("OnAccent", "#0F1113"), ("SelectRow", "#262D33"), ("RailChecked", "#1F2327"),
        ("MenuHover", "#2A3036"), ("TabLine", "#4A525B"), ("Thumb", "#3A4048"), ("ThumbHover", "#4A525B"), ("Link", "#7FD3C4"), ("Inset", "#1A1E22"), ("Soft", "#B7BEC6"));

    /// <summary>Keys whose brush has a different name from the colour (C.Button is the ButtonBg brush).</summary>
    private static readonly Dictionary<string, string> BrushNames = new() { ["Button"] = "ButtonBg" };

    public static ThemeDef Find(string? id) => All.FirstOrDefault(t => t.Id == id) ?? All.First(t => t.Id == DefaultId);

    internal static IReadOnlyDictionary<string, Color> Resolve(ThemeDef t)
    {
        var src = t.Colors;
        Color Get(string k) => Parse(src.TryGetValue(k, out var v) ? v : Classic[k]);
        var c = new Dictionary<string, Color>();
        foreach (var k in new[] { "Bg", "Panel", "Panel2", "Border", "BorderSoft", "Work", "Text", "TextStrong", "Muted", "Faint", "Hover", "Accent",
                                  "InputBorder", "Button", "ButtonBorder", "EntryIcon", "FolderIcon" })
            c[k] = Get(k);
        // Classic keeps its exact values; other themes work out what they leave out from their own colours.
        Color Or(string k, Func<Color> derive) => src.TryGetValue(k, out var v) ? Parse(v) : t.Id == "teal" ? Parse(Classic[k]) : derive();
        c["Header"] = Or("Header", () => c["Panel"]);
        c["Rail"] = Or("Rail", () => c["Panel2"]);
        c["Select"] = Or("Select", () => Mix(c["Panel"], c["Text"], 0.07));
        c["SelectRow"] = Or("SelectRow", () => Mix(c["Bg"], c["Accent"], 0.2));
        c["ButtonHover"] = Or("ButtonHover", () => Mix(c["Button"], c["TextStrong"], 0.06));
        c["AccentDim"] = Or("AccentDim", () => Mix(c["Bg"], c["Accent"], 0.4));
        c["OnAccent"] = Or("OnAccent", () => Luminance(c["Accent"]) > 0.45 ? Mix(c["Bg"], Colors.Black, 0.5) : Colors.White);
        c["RailChecked"] = Or("RailChecked", () => Mix(c["Button"], c["Text"], 0.03));
        c["MenuHover"] = Or("MenuHover", () => Mix(c["Button"], c["Text"], 0.09));
        c["Thumb"] = Or("Thumb", () => Mix(c["Panel"], c["Text"], 0.16));
        c["ThumbHover"] = Or("ThumbHover", () => Mix(c["Panel"], c["Text"], 0.26));
        c["TabLine"] = Or("TabLine", () => c["ThumbHover"]);
        c["Link"] = Or("Link", () => Mix(c["Accent"], Colors.White, 0.35));
        c["Inset"] = Or("Inset", () => Mix(c["Panel"], c["Text"], 0.03));
        c["Soft"] = Or("Soft", () => Mix(c["Muted"], c["Text"], 0.45));
        return c;
    }

    /// <summary>Writes the theme's colours and brushes over the classic ones. Call before any window is created.</summary>
    public static void Apply(ResourceDictionary appResources, string? id, Shading shading = Shading.Subtle)
    {
        var theme = Find(id);
        Current = theme;
        CurrentShading = shading;
        var target = appResources.MergedDictionaries.FirstOrDefault(d => d.Contains("C.Accent")) ?? appResources;
        foreach (var (key, color) in theme.Resolve())
        {
            target["C." + key] = color;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            target[BrushNames.GetValueOrDefault(key, key)] = brush;
        }
        // Menus share the button colours.
        target["MenuBg"] = target["ButtonBg"];
        target["MenuBorder"] = target["ButtonBorder"];
        var colors = theme.Resolve();
        ApplySystemColors(target, colors);
        ApplyShading(target, colors, shading);
    }

    /// <summary>
    /// Points the Windows system colours at the theme. Stock WPF control templates (and anything a control builds
    /// for itself, like a TextBox's edit menu or a TreeView's selected row) draw with these; left alone they are the
    /// light Windows palette, which is where stray white panels, black text and pale-blue selections came from.
    /// </summary>
    private static void ApplySystemColors(ResourceDictionary target, IReadOnlyDictionary<string, Color> c)
    {
        void Set(ResourceKey brushKey, ResourceKey? colorKey, string name)
        {
            var brush = new SolidColorBrush(c[name]);
            brush.Freeze();
            target[brushKey] = brush;
            if (colorKey is not null) target[colorKey] = c[name];
        }
        Set(SystemColors.WindowBrushKey, SystemColors.WindowColorKey, "Bg");
        Set(SystemColors.WindowTextBrushKey, SystemColors.WindowTextColorKey, "Text");
        Set(SystemColors.ControlBrushKey, SystemColors.ControlColorKey, "Panel");
        Set(SystemColors.ControlTextBrushKey, SystemColors.ControlTextColorKey, "Text");
        Set(SystemColors.ControlLightBrushKey, SystemColors.ControlLightColorKey, "Border");
        Set(SystemColors.ControlLightLightBrushKey, SystemColors.ControlLightLightColorKey, "Panel2");
        Set(SystemColors.ControlDarkBrushKey, SystemColors.ControlDarkColorKey, "Border");
        Set(SystemColors.ControlDarkDarkBrushKey, SystemColors.ControlDarkDarkColorKey, "Bg");
        Set(SystemColors.HighlightBrushKey, SystemColors.HighlightColorKey, "SelectRow");
        Set(SystemColors.HighlightTextBrushKey, SystemColors.HighlightTextColorKey, "TextStrong");
        Set(SystemColors.InactiveSelectionHighlightBrushKey, null, "SelectRow");
        Set(SystemColors.InactiveSelectionHighlightTextBrushKey, null, "TextStrong");
        Set(SystemColors.MenuBrushKey, SystemColors.MenuColorKey, "Button");
        Set(SystemColors.MenuBarBrushKey, SystemColors.MenuBarColorKey, "Button");
        Set(SystemColors.MenuTextBrushKey, SystemColors.MenuTextColorKey, "TextStrong");
        Set(SystemColors.MenuHighlightBrushKey, SystemColors.MenuHighlightColorKey, "MenuHover");
        Set(SystemColors.InfoBrushKey, SystemColors.InfoColorKey, "Button");
        Set(SystemColors.InfoTextBrushKey, SystemColors.InfoTextColorKey, "TextStrong");
        Set(SystemColors.GrayTextBrushKey, SystemColors.GrayTextColorKey, "Faint");
        Set(SystemColors.HotTrackBrushKey, SystemColors.HotTrackColorKey, "Link");
        Set(SystemColors.ActiveBorderBrushKey, SystemColors.ActiveBorderColorKey, "Border");
        Set(SystemColors.InactiveBorderBrushKey, SystemColors.InactiveBorderColorKey, "Border");
        Set(SystemColors.WindowFrameBrushKey, SystemColors.WindowFrameColorKey, "Border");
        Set(SystemColors.AppWorkspaceBrushKey, SystemColors.AppWorkspaceColorKey, "Work");
        Set(SystemColors.ScrollBarBrushKey, SystemColors.ScrollBarColorKey, "Panel");
        Set(SystemColors.ActiveCaptionBrushKey, SystemColors.ActiveCaptionColorKey, "Header");
        Set(SystemColors.InactiveCaptionBrushKey, SystemColors.InactiveCaptionColorKey, "Header");
        Set(SystemColors.ActiveCaptionTextBrushKey, SystemColors.ActiveCaptionTextColorKey, "Text");
        Set(SystemColors.InactiveCaptionTextBrushKey, SystemColors.InactiveCaptionTextColorKey, "Muted");
    }

    /// <summary>The shading that was applied at startup.</summary>
    public static Shading CurrentShading { get; private set; } = Shading.Subtle;

    /// <summary>
    /// Shading is drawn with brushes only (a light wash and highlight line on buttons, an inner shadow in fields, gradient
    /// borders on cards, shadow strips along the title bar and tree). No Effects, which would put sessions in software rendering.
    /// </summary>
    private static void ApplyShading(ResourceDictionary target, IReadOnlyDictionary<string, Color> c, Shading shading)
    {
        static Brush Frozen(Brush b) { b.Freeze(); return b; }
        static Brush Solid(Color x) => Frozen(new SolidColorBrush(x));
        static Brush Vertical(Color top, Color bottom, double stop = 1) =>
            Frozen(new LinearGradientBrush(new GradientStopCollection { new(top, 0), new(bottom, stop) }, new Point(0, 0), new Point(0, 1)));
        static Brush Horizontal(Color left, Color right) =>
            Frozen(new LinearGradientBrush(new GradientStopCollection { new(left, 0), new(right, 1) }, new Point(0, 0), new Point(1, 0)));
        static Color A(Color x, byte alpha) => System.Windows.Media.Color.FromArgb(alpha, x.R, x.G, x.B);

        if (shading == Shading.Flat)
        {
            var none = Solid(Colors.Transparent);
            foreach (var k in new[] { "Depth.Sheen", "Depth.HiLine", "Depth.Inset", "Depth.ShadowDown", "Depth.ShadowRight" }) target[k] = none;
            target["Depth.Header"] = Solid(c["Header"]);
            target["Depth.Card"] = Solid(c["Panel"]);
            target["Depth.CardBorder"] = Solid(c["Border"]);
            target["Depth.CardBorderThickness"] = new Thickness(1);
            target["Depth.ButtonBorder"] = Solid(c["ButtonBorder"]);
            return;
        }

        var raised = shading == Shading.Raised;
        Color white = Colors.White, black = Colors.Black;
        target["Depth.Sheen"] = Vertical(A(white, (byte)(raised ? 0x16 : 0x0B)), A(white, 0));
        target["Depth.HiLine"] = Solid(A(white, (byte)(raised ? 0x1C : 0x0E)));
        target["Depth.Inset"] = Vertical(A(black, (byte)(raised ? 0x66 : 0x40)), A(black, 0), 0.4);
        target["Depth.ShadowDown"] = Vertical(A(black, (byte)(raised ? 0x8C : 0x55)), A(black, 0));
        target["Depth.ShadowRight"] = Horizontal(A(black, (byte)(raised ? 0x70 : 0x40)), A(black, 0));
        target["Depth.Header"] = Vertical(Mix(c["Header"], white, raised ? 0.06 : 0.035), c["Header"]);
        target["Depth.Card"] = Vertical(Mix(c["Panel"], white, raised ? 0.055 : 0.03), c["Panel"], 0.55);
        target["Depth.CardBorder"] = Vertical(Mix(c["Border"], white, raised ? 0.14 : 0.08), Mix(c["Border"], black, raised ? 0.5 : 0.35));
        target["Depth.CardBorderThickness"] = raised ? new Thickness(1, 1, 1, 2) : new Thickness(1);
        target["Depth.ButtonBorder"] = Vertical(Mix(c["ButtonBorder"], white, raised ? 0.1 : 0.06), Mix(c["ButtonBorder"], black, raised ? 0.45 : 0.3));
    }

    /// <summary>A colour of the applied theme, for code that draws its own (charts, the title bar).</summary>
    public static Color GetColor(string key) =>
        Application.Current?.TryFindResource("C." + key) is Color c ? c : Parse(Classic.GetValueOrDefault(key, "#808080"));

    public static Brush GetBrush(string key) =>
        Application.Current?.TryFindResource(BrushNames.GetValueOrDefault(key, key)) as Brush ?? new SolidColorBrush(GetColor(key));

    /// <summary>Whether two colours are hard to tell apart (used to keep chart highlights visible).</summary>
    public static bool IsNear(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) < 90;

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static Color Mix(Color a, Color b, double t) => System.Windows.Media.Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
}
