using System.Windows.Media;

namespace RemoteMan.App.Views;

/// <summary>The glyphs and colours a folder can have (16px line icons, same style as the protocol icons).</summary>
public static class FolderIcons
{
    public sealed record Glyph16(string Key, string Name, Geometry Data);
    public sealed record Swatch(string Name, string Hex, Brush Brush);

    public const string DefaultColor = "#7D8A99";

    public static readonly IReadOnlyList<Glyph16> Glyphs =
    [
        G("folder", "Folder", "M1.5,3.5 h4.5 l1.5,1.5 h7 v8 h-13 z"),
        G("server", "Servers", "M2.5,2 h11 v5 h-11 z M2.5,9 h11 v5 h-11 z M5,4.5 h0.01 M5,11.5 h0.01 M8,4.5 h3 M8,11.5 h3"),
        G("network", "Network", "M6,1.5 h4 v3.5 H6 z M1.5,11 h4 v3.5 h-4 z M10.5,11 h4 v3.5 h-4 z M8,5 v3 M3.5,11 V8 h9 v3"),
        G("cloud", "Cloud", "M4.5,12.5 h7.5 a3,3 0 0 0 0.3,-6 a4.2,4.2 0 0 0 -8.1,0.7 A2.7,2.7 0 0 0 4.5,12.5 z"),
        G("lab", "Lab", "M6,1.5 h4 M6.5,1.5 v4.5 L2.5,13 a1,1 0 0 0 0.9,1.5 h9.2 a1,1 0 0 0 0.9,-1.5 L9.5,6 V1.5 M4.5,10 h7"),
        G("building", "Site / office", "M2.5,14.5 v-12 h7 v12 M9.5,6.5 h4 v8 M1,14.5 h14 M4.5,5 h2 M4.5,8 h2 M4.5,11 h2 M11.5,9.5 h0.01 M11.5,12 h0.01"),
        G("star", "Important", "M8,1.8 l1.9,3.9 l4.3,0.6 l-3.1,3 l0.7,4.2 L8,11.5 l-3.8,2 l0.7,-4.2 l-3.1,-3 l4.3,-0.6 z"),
        G("lock", "Restricted", "M3,7 h10 v7.5 H3 z M5,7 V5 a3,3 0 0 1 6,0 v2 M8,10 v2"),
        G("db", "Databases", "M2.5,3.5 c0,-1.1 2.5,-2 5.5,-2 s5.5,0.9 5.5,2 s-2.5,2 -5.5,2 s-5.5,-0.9 -5.5,-2 z M2.5,3.5 v9 c0,1.1 2.5,2 5.5,2 s5.5,-0.9 5.5,-2 v-9 M2.5,8 c0,1.1 2.5,2 5.5,2 s5.5,-0.9 5.5,-2"),
        G("globe", "Internet / web", "M14.5,8 A6.5,6.5 0 1 1 1.5,8 a6.5,6.5 0 0 1 13,0 z M1.5,8 h13 M8,1.5 c-3,3 -3,10 0,13 M8,1.5 c3,3 3,10 0,13"),
        G("shield", "Firewalls", "M8,1.5 l5.5,2 v4.2 c0,3.3 -2.3,5.8 -5.5,6.8 c-3.2,-1 -5.5,-3.5 -5.5,-6.8 V3.5 z M5.5,8 l2,2 l3,-3.5"),
        G("home", "Home", "M1.5,7.5 L8,2 l6.5,5.5 M3.5,6 v8.5 h9 V6 M6.5,14.5 v-4 h3 v4"),
    ];

    public static readonly IReadOnlyList<Swatch> Colors =
    [
        S("Grey (default)", DefaultColor),
        S("Blue", "#5B9BEA"),
        S("Teal", "#3FB8A4"),
        S("Green", "#6CC070"),
        S("Yellow", "#D6C25E"),
        S("Orange", "#E0904F"),
        S("Red", "#E5605B"),
        S("Purple", "#B48CE6"),
    ];

    public static Geometry GlyphFor(string? key) => (Glyphs.FirstOrDefault(g => g.Key == key) ?? Glyphs[0]).Data;

    public static Brush BrushFor(string? hex)
    {
        if (Colors.FirstOrDefault(c => string.Equals(c.Hex, hex, StringComparison.OrdinalIgnoreCase)) is { } known) return known.Brush;
        try { if (!string.IsNullOrWhiteSpace(hex)) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; } }
        catch (FormatException) { }
        return Colors[0].Brush;
    }

    private static Glyph16 G(string key, string name, string data)
    {
        var g = Geometry.Parse(data);
        g.Freeze();
        return new Glyph16(key, name, g);
    }

    private static Swatch S(string name, string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return new Swatch(name, hex, b);
    }
}
