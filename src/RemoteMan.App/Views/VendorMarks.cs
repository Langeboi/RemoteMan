using System.Windows;
using System.Windows.Media;

namespace RemoteMan.App.Views;

/// <summary>
/// Vendor and product marks for entry and folder icons: small filled drawings on a 16x16 grid, in each vendor's own colours.
/// Simplified for 16 px, so they are recognisable but not the exact logos. They ignore the icon colour.
/// Generated from a small layer list (fill, stroke, stroke width, path); paths use the even-odd fill rule.
/// </summary>
public static class VendorMarks
{
    public sealed record Mark(string Key, string Name, string Keywords, DrawingGroup Drawing);

    public static readonly IReadOnlyList<Mark> All =
    [
        M("fortigate", "FortiGate", "fortinet firewall", L("#EE3124", null, 0, "M1.5,2 h4 v3.6 h-4 Z M10.5,2 h4 v3.6 h-4 Z M1.5,6.4 h4 v3.6 h-4 Z M6,6.4 h4 v3.6 h-4 Z M10.5,6.4 h4 v3.6 h-4 Z M1.5,10.8 h4 v3.6 h-4 Z M6,10.8 h4 v3.6 h-4 Z M10.5,10.8 h4 v3.6 h-4 Z ")),
        M("aruba", "Aruba", "hpe wifi ap", L(null, "#FF8300", 1.9, "M4.87,11.83 L1.54,11.13 M5.55,10.44 L2.94,8.26 M6.80,9.53 L5.53,6.38 M8.33,9.32 L8.69,5.94 M9.79,9.85 L11.69,7.03 M10.83,11.00 L13.83,9.40"), L(null, "#FF8300", 1.3, "M1.2,13.6 H14.8")),
        M("mikrotik", "MikroTik", "routeros winbox", L("#4D84C7", null, 0, "M3,1.5 H13 A1.5,1.5 0 0 1 14.5,3 V13 A1.5,1.5 0 0 1 13,14.5 H3 A1.5,1.5 0 0 1 1.5,13 V3 A1.5,1.5 0 0 1 3,1.5 Z M4,11.8 H5.6 V7.3 L8,10.6 L10.4,7.3 V11.8 H12 V4.2 H10.6 L8,7.8 L5.4,4.2 H4 Z")),
        M("cisco", "Cisco", "switch router", L(null, "#049FD9", 1.1, "M2,11.5 V9.70 M3.5,11.5 V7.90 M5,11.5 V6.10 M6.5,11.5 V7.90 M8,11.5 V9.70 M9.5,11.5 V7.90 M11,11.5 V6.10 M12.5,11.5 V7.90 M14,11.5 V9.70")),
        M("windows", "Windows", "microsoft server", L("#00A4EF", null, 0, "M1.5,1.5 h6.2 v6.2 h-6.2 Z M8.3,1.5 h6.2 v6.2 h-6.2 Z M1.5,8.3 h6.2 v6.2 h-6.2 Z M8.3,8.3 h6.2 v6.2 h-6.2 Z")),
        M("linux", "Linux", "tux server", L("#26292D", "#AEB6BF", 0.6, "M8,1.3 C5.9,1.3 5,3 5,4.9 C5,6.2 4.6,6.9 3.8,8.2 C2.9,9.6 2.2,11.2 2.8,12.8 C3.2,13.8 4.3,14.3 5.4,14.4 H10.6 C11.7,14.3 12.8,13.8 13.2,12.8 C13.8,11.2 13.1,9.6 12.2,8.2 C11.4,6.9 11,6.2 11,4.9 C11,3 10.1,1.3 8,1.3 Z"), L("#F4F6F8", null, 0, "M8,6.4 C6.4,6.4 5.4,8.3 5.4,10.3 C5.4,12.2 6.3,13.7 8,13.7 C9.7,13.7 10.6,12.2 10.6,10.3 C10.6,8.3 9.6,6.4 8,6.4 Z"), L("#F4F6F8", null, 0, "M6.40,3.90 A0.6,0.6 0 1 1 7.60,3.90 A0.6,0.6 0 1 1 6.40,3.90 Z M8.40,3.90 A0.6,0.6 0 1 1 9.60,3.90 A0.6,0.6 0 1 1 8.40,3.90 Z"), L("#F5B700", null, 0, "M6.8,5.1 H9.2 L8,6.5 Z M3.3,14.5 L3.7,13.3 C4.4,12.4 5.9,12.4 6.5,13.3 L6.9,14.5 Z M9.1,14.5 L9.5,13.3 C10.1,12.4 11.6,12.4 12.3,13.3 L12.7,14.5 Z")),
        M("ubuntu", "Ubuntu", "linux", L("#E95420", null, 0, "M1.50,8.00 A6.5,6.5 0 1 1 14.50,8.00 A6.5,6.5 0 1 1 1.50,8.00 Z"), L(null, "#F4F6F8", 1.2, "M5.00,8.00 A3,3 0 1 1 11.00,8.00 A3,3 0 1 1 5.00,8.00 Z"), L("#F4F6F8", "#E95420", 0.8, "M10.85,8.00 A1.25,1.25 0 1 1 13.35,8.00 A1.25,1.25 0 1 1 10.85,8.00 Z"), L("#F4F6F8", "#E95420", 0.8, "M4.70,4.45 A1.25,1.25 0 1 1 7.20,4.45 A1.25,1.25 0 1 1 4.70,4.45 Z"), L("#F4F6F8", "#E95420", 0.8, "M4.70,11.55 A1.25,1.25 0 1 1 7.20,11.55 A1.25,1.25 0 1 1 4.70,11.55 Z")),
        M("debian", "Debian", "linux", L(null, "#D70A53", 1.5, "M10.6,13 A5.6,5.6 0 1 1 13.6,7.6 C13.4,5.3 11.4,3.6 9.1,3.6 C6.7,3.6 4.8,5.5 4.8,7.9 C4.8,10.1 6.5,11.7 8.5,11.7 C10.2,11.7 11.5,10.5 11.5,8.9 C11.5,7.4 10.4,6.4 9.1,6.5")),
        M("redhat", "Red Hat", "rhel linux", L("#EE0000", null, 0, "M1.8,9.3 C1,10 2,11.4 4.4,12.1 C7,12.9 10.6,13 12.8,12.2 C14.5,11.6 14.8,10.5 14,9.7 C13.6,10.7 11.9,11 9.9,11 C7.2,11 3.5,10.2 1.8,9.3 Z M4.3,8.4 L5.5,4.3 C5.8,3.4 6.5,3 7.3,3.3 C7.9,3.5 8.4,3.7 9,3.5 C9.9,3.2 10.7,3.6 11,4.5 L12.1,8.1 C11.4,8.7 9.9,9 8.2,8.8 C6.6,8.6 5,8.2 4.3,8.4 Z")),
        M("docker", "Docker", "container", L("#2496ED", null, 0, "M1,8.4 H13.3 C13.8,7.5 14.7,7.3 15.5,7.5 C15.2,8.4 14.6,9 13.7,9.1 C12.7,12 10,13.9 6.2,13.9 C3.3,13.9 1.6,12.3 1,8.4 Z M2.9,6.4 h1.6 v1.6 h-1.6 Z M4.8,6.4 h1.6 v1.6 h-1.6 Z M6.7,6.4 h1.6 v1.6 h-1.6 Z M8.6,6.4 h1.6 v1.6 h-1.6 Z M4.8,4.5 h1.6 v1.6 h-1.6 Z M6.7,4.5 h1.6 v1.6 h-1.6 Z M8.6,4.5 h1.6 v1.6 h-1.6 Z M8.6,2.6 h1.6 v1.6 h-1.6 Z")),
        M("kubernetes", "Kubernetes", "k8s container", L("#326CE5", "#326CE5", 1.2, "M8.00,1.40 L13.39,4.00 L14.73,9.84 L10.99,14.52 L5.01,14.52 L1.27,9.84 L2.61,4.00 Z"), L(null, "#F4F6F8", 1.0, "M5.20,8.30 A2.8,2.8 0 1 1 10.80,8.30 A2.8,2.8 0 1 1 5.20,8.30 Z M8.00,6.70 L8.00,3.70 M9.25,7.30 L11.60,5.43 M9.56,8.66 L12.48,9.32 M8.69,9.74 L10.00,12.44 M7.31,9.74 L6.00,12.44 M6.44,8.66 L3.52,9.32 M6.75,7.30 L4.40,5.43")),
        M("ansible", "Ansible", "automation red hat", L("#EE0000", null, 0, "M1.40,8.00 A6.6,6.6 0 1 1 14.60,8.00 A6.6,6.6 0 1 1 1.40,8.00 Z"), L("#F4F6F8", null, 0, "M8,3.6 L11.6,12.3 H10.1 L9.3,10.3 L6.4,12.1 L6,11 L8.8,9.1 L8,7 L5.9,12.3 H4.4 Z")),
        M("zabbix", "Zabbix", "monitoring", L("#D40000", null, 0, "M2.5,1.5 H13.5 A1,1 0 0 1 14.5,2.5 V13.5 A1,1 0 0 1 13.5,14.5 H2.5 A1,1 0 0 1 1.5,13.5 V2.5 A1,1 0 0 1 2.5,1.5 Z"), L("#F4F6F8", null, 0, "M4.5,4.3 H11.5 V5.7 L6.6,10.3 H11.5 V11.7 H4.5 V10.3 L9.4,5.7 H4.5 Z")),
        M("proxmox", "Proxmox", "pve hypervisor vm", L(null, "#E57000", 2.1, "M1.5,2.5 L6,8 L1.5,13.5 M14.5,2.5 L10,8 L14.5,13.5"), L(null, "#C9CFD6", 1.4, "M5,2.5 L11,13.5 M11,2.5 L5,13.5")),
    ];

    public static Mark? Find(string? key) => key is null ? null : All.FirstOrDefault(m => m.Key == key);

    private readonly record struct Layer(string? Fill, string? Stroke, double Width, string Data);

    private static Layer L(string? fill, string? stroke, double width, string data) => new(fill, stroke, width, data);

    private static Mark M(string key, string name, string keywords, params Layer[] layers)
    {
        var g = new DrawingGroup();
        foreach (var l in layers)
        {
            var geo = Geometry.Parse(l.Data);
            geo.Freeze();
            var pen = l.Stroke is null ? null : new Pen(Brush(l.Stroke), l.Width)
            {
                StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round,
            };
            pen?.Freeze();
            g.Children.Add(new GeometryDrawing(l.Fill is null ? null : Brush(l.Fill), pen, geo));
        }
        g.ClipGeometry = new RectangleGeometry(new Rect(0, 0, 16, 16));
        g.Freeze();
        return new Mark(key, name, keywords, g);
    }

    private static SolidColorBrush Brush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
