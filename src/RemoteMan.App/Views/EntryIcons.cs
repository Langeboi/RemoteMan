using System.Windows;
using System.Windows.Media;

namespace RemoteMan.App.Views;

/// <summary>
/// Custom icons for any entry: a line glyph from <see cref="FolderIcons"/> in a chosen colour, or a vendor mark
/// from <see cref="VendorMarks"/>. Entry.Icon holds the glyph or mark key (null = the normal icon for the kind),
/// Entry.IconColor the colour (null = the normal colour for the kind).
/// </summary>
public static class EntryIcons
{
    private static readonly Dictionary<(string?, string?, string), ImageSource> Cache = new();

    /// <summary>True when the entry has a custom glyph or colour, so the tree shows <see cref="Render"/> instead of the protocol icon.</summary>
    public static bool IsCustom(string? icon, string? color, bool folder) =>
        !string.IsNullOrEmpty(color) || (!string.IsNullOrEmpty(icon) && !(folder && icon == "folder"));

    public static bool IsVendor(string? icon) => VendorMarks.Find(icon) is not null;

    /// <summary>The icon as a 16x16 image. kindTag is the protocol tag (RDP, SSH, KEY, DIR for folders…), used for the defaults.</summary>
    public static ImageSource Render(string? icon, string? color, string kindTag)
    {
        var key = (icon, color?.ToUpperInvariant(), kindTag);
        if (Cache.TryGetValue(key, out var hit)) return hit;
        var img = RenderWith(icon, string.IsNullOrEmpty(color) ? DefaultBrush(kindTag) : FolderIcons.BrushFor(color), kindTag);
        Cache[key] = img;
        return img;
    }

    /// <summary>
    /// The icon for an entry you're connected to: line glyphs drawn in the theme's connected green (like the protocol icons),
    /// vendor marks recoloured in that green with their shading kept (<see cref="VendorMarks.Mark.Tinted"/>).
    /// </summary>
    public static ImageSource RenderConnected(string? icon, string kindTag)
    {
        var ok = RemoteMan.App.Services.Themes.GetBrush("Ok");
        var okColor = ok is SolidColorBrush sb ? sb.Color : Colors.LimeGreen;
        var key = (icon, "OK:" + okColor, kindTag);
        if (Cache.TryGetValue(key, out var hit)) return hit;
        ImageSource img;
        if (VendorMarks.Find(icon) is { } mark)
        {
            var di = new DrawingImage(mark.Tinted(okColor));
            di.Freeze();
            img = di;
        }
        else img = RenderWith(icon, ok, kindTag);
        Cache[key] = img;
        return img;
    }

    /// <summary>Same as <see cref="Render"/> with an explicit stroke brush for line glyphs (vendor marks keep their own colours).</summary>
    public static ImageSource RenderWith(string? icon, Brush stroke, string kindTag)
    {
        DrawingGroup group;
        if (VendorMarks.Find(icon) is { } mark) group = mark.Drawing;
        else
        {
            var geo = FolderIcons.Glyphs.FirstOrDefault(g => g.Key == icon)?.Data ?? DefaultGeometry(kindTag);
            var pen = new Pen(stroke, 1.3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            pen.Freeze();
            group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(null, pen, geo));
            group.ClipGeometry = new RectangleGeometry(new Rect(0, 0, 16, 16));
            group.Freeze();
        }
        var img = new DrawingImage(group);
        img.Freeze();
        return img;
    }

    /// <summary>The kind's normal glyph: the protocol icon, or the plain folder.</summary>
    public static Geometry DefaultGeometry(string kindTag) =>
        kindTag == "DIR" ? FolderIcons.GlyphFor("folder")
        : Application.Current?.TryFindResource("Icon." + kindTag) as Geometry ?? FolderIcons.GlyphFor("folder");

    /// <summary>Readable name of a kind tag, for tooltips and the picker.</summary>
    public static string KindName(string kindTag) => kindTag switch
    {
        "DIR" or "" => "folder", "KEY" => "credential", "WEB" => "web", "TEL" => "Telnet", "SER" => "serial console",
        "PVE" => "Proxmox console", "PS" => "PowerShell", "WBX" => "WinBox", var t => t,
    };

    /// <summary>The kind's normal colour: grey folders, yellow keys, blue for everything else.</summary>
    public static Brush DefaultBrush(string kindTag) => kindTag switch
    {
        "DIR" => FolderIcons.BrushFor(FolderIcons.DefaultColor),
        "KEY" => Application.Current?.TryFindResource("KeyIcon") as Brush ?? Brushes.Gold,
        _ => Application.Current?.TryFindResource("EntryIcon") as Brush ?? Brushes.SteelBlue,
    };
}
