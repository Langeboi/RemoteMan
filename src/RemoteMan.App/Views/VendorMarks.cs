using System.Windows;
using System.Windows.Media;

namespace RemoteMan.App.Views;

/// <summary>
/// Vendor and product marks for entry and folder icons: small filled drawings on a 16x16 grid, in each vendor's own colours.
/// Simplified for 16 px, so they are recognisable but not the exact logos. They ignore the icon colour, except when connected: then <see cref="Mark.Tinted"/> recolours them in the theme's connected green.
/// Generated from a small layer list (fill, stroke, stroke width, path); paths use the even-odd fill rule.
/// </summary>
public static class VendorMarks
{
    public sealed record Mark(string Key, string Name, string Group, string Keywords, DrawingGroup Drawing)
    {
        internal IReadOnlyList<Layer> Layers { get; init; } = [];

        /// <summary>
        /// The mark recoloured in one hue (used for the "connected" state): every layer takes the tint's hue and saturation
        /// but keeps its own lightness, so white details, dark tiles and two-tone shading still read and the shape stays recognisable.
        /// </summary>
        public DrawingGroup Tinted(Color tint) => Build(Layers, c => Retint(c, tint));
    }

    /// <summary>Picker sections, in display order.</summary>
    public static readonly IReadOnlyList<string> Groups = ["Network", "Firewall and VPN", "Servers and hypervisors", "Containers and automation", "Monitoring", "Databases", "Self-hosted apps"];

    public static readonly IReadOnlyList<Mark> All =
    [
        M("aruba", "Aruba", "Network", "hpe wifi ap", L(null, "#FF8300", 1.9, "M4.87,11.83 L1.54,11.13 M5.55,10.44 L2.94,8.26 M6.80,9.53 L5.53,6.38 M8.33,9.32 L8.69,5.94 M9.79,9.85 L11.69,7.03 M10.83,11.00 L13.83,9.40"), L(null, "#FF8300", 1.3, "M1.2,13.6 H14.8")),
        M("cisco", "Cisco", "Network", "switch router", L(null, "#049FD9", 1.1, "M2,11.5 V9.70 M3.5,11.5 V7.90 M5,11.5 V6.10 M6.5,11.5 V7.90 M8,11.5 V9.70 M9.5,11.5 V7.90 M11,11.5 V6.10 M12.5,11.5 V7.90 M14,11.5 V9.70")),
        // MikroTik: the charcoal tile with a sharp geometric white M (the wordmark's M, cut square like the RouterOS/WinBox mark); a faint rim keeps the dark tile visible on dark themes.
        M("mikrotik", "MikroTik", "Network", "routeros winbox", L("#293239", "#5B6B7A", 0.8, "M3,1.6 H13 A1.4,1.4 0 0 1 14.4,3 V13 A1.4,1.4 0 0 1 13,14.4 H3 A1.4,1.4 0 0 1 1.6,13 V3 A1.4,1.4 0 0 1 3,1.6 Z"), L("#F4F6F8", null, 0, "M3.6,11.9 L5.1,4.1 H6.9 L8,8.3 L9.1,4.1 H10.9 L12.4,11.9 H10.75 L9.85,7.1 L8.75,11.3 H7.25 L6.15,7.1 L5.25,11.9 Z")),
        M("unifi", "UniFi", "Network", "ubiquiti wifi ap", L(null, "#006FFF", 2.4, "M4.5,2.5 V9 C4.5,11.5 6,13 8,13 C10,13 11.5,11.5 11.5,9 V2.5")),
        M("cloudflare", "Cloudflare", "Network", "dns tunnel cdn", L("#F38020", null, 0, "M2,12 H13.6 C14.6,12 15.2,11.2 14.9,10.3 C14.6,9.5 13.8,9.1 13,9.2 C12.7,6.6 10.6,4.6 8,4.6 C5.9,4.6 4.1,5.9 3.4,7.8 C2.2,8 1.2,9 1.2,10.3 C1.2,11.2 1.5,12 2,12 Z")),
        M("fortigate", "FortiGate", "Firewall and VPN", "fortinet firewall", L("#EE3124", null, 0, "M1.5,2 h4 v3.6 h-4 Z M10.5,2 h4 v3.6 h-4 Z M1.5,6.4 h4 v3.6 h-4 Z M6,6.4 h4 v3.6 h-4 Z M10.5,6.4 h4 v3.6 h-4 Z M1.5,10.8 h4 v3.6 h-4 Z M6,10.8 h4 v3.6 h-4 Z M10.5,10.8 h4 v3.6 h-4 Z ")),
        M("opnsense", "OPNsense", "Firewall and VPN", "firewall", L(null, "#D94F00", 1.6, "M5,2.5 H2.5 V13.5 H5 M11,2.5 H13.5 V13.5 H11 M5.6,6.3 H10.4 M5.6,9.7 H10.4")),
        // WireGuard: the deep red disc with the white serpent curling up into a head at the top right (red eye), and the lower swoosh.
        M("wireguard", "WireGuard", "Firewall and VPN", "vpn", L("#9B1C20", null, 0, "M1.40,8.00 A6.6,6.6 0 1 1 14.60,8.00 A6.6,6.6 0 1 1 1.40,8.00 Z"), L("#F4F6F8", null, 0, "M4,11.6 C3.4,7.7 6.4,4.3 10.2,4.4 C11.5,4.45 12.5,5.2 12.6,6.5 C11.7,6 10.5,6 9.6,6.4 C7.3,7.4 6.1,9.4 6.4,12 Z"), L(null, "#F4F6F8", 1.1, "M7.6,12.4 C9,12.9 10.7,12.3 11.4,10.9 M8.6,9.7 C9.4,8.9 10.6,9.1 10.9,10"), L("#9B1C20", null, 0, "M10.55,5.30 A0.5,0.5 0 1 1 11.55,5.30 A0.5,0.5 0 1 1 10.55,5.30 Z")),
        M("tailscale", "Tailscale", "Firewall and VPN", "vpn wireguard mesh", L("#F4F6F8", null, 0, "M2.05,8.00 A1.45,1.45 0 1 1 4.95,8.00 A1.45,1.45 0 1 1 2.05,8.00 Z M6.55,8.00 A1.45,1.45 0 1 1 9.45,8.00 A1.45,1.45 0 1 1 6.55,8.00 Z M11.05,8.00 A1.45,1.45 0 1 1 13.95,8.00 A1.45,1.45 0 1 1 11.05,8.00 Z M6.55,12.50 A1.45,1.45 0 1 1 9.45,12.50 A1.45,1.45 0 1 1 6.55,12.50 Z"), L("#6B6F76", null, 0, "M2.05,3.50 A1.45,1.45 0 1 1 4.95,3.50 A1.45,1.45 0 1 1 2.05,3.50 Z M6.55,3.50 A1.45,1.45 0 1 1 9.45,3.50 A1.45,1.45 0 1 1 6.55,3.50 Z M11.05,3.50 A1.45,1.45 0 1 1 13.95,3.50 A1.45,1.45 0 1 1 11.05,3.50 Z M2.05,12.50 A1.45,1.45 0 1 1 4.95,12.50 A1.45,1.45 0 1 1 2.05,12.50 Z M11.05,12.50 A1.45,1.45 0 1 1 13.95,12.50 A1.45,1.45 0 1 1 11.05,12.50 Z")),
        M("netbird", "NetBird", "Firewall and VPN", "vpn wireguard mesh", L("#F68330", null, 0, "M 1.00 4.54 Q 4.48 3.84 7.96 4.97 L 5.35 9.32 Z"), L("#F68330", null, 0, "M 7.78 4.97 C 8.83 3.50 9.70 3.06 11.00 3.06 L 14.91 3.06 L 9.00 13.15 L 3.17 13.15 L 5.35 9.32 Z"), L("#F35E32", null, 0, "M 7.78 4.97 C 9.70 5.84 10.83 7.58 11.00 9.76 L 9.00 13.15 L 5.35 9.32 Z")),
        M("twingate", "Twingate", "Firewall and VPN", "zero trust vpn", L("#F2F2F2", null, 0, "M3,1.5 H13 A1.5,1.5 0 0 1 14.5,3 V13 A1.5,1.5 0 0 1 13,14.5 H3 A1.5,1.5 0 0 1 1.5,13 V3 A1.5,1.5 0 0 1 3,1.5 Z M4.5,4.5 H11.5 V6.3 H8.9 V12 H7.1 V6.3 H4.5 Z")),
        M("windows", "Windows", "Servers and hypervisors", "microsoft server", L("#00A4EF", null, 0, "M1.5,1.5 h6.2 v6.2 h-6.2 Z M8.3,1.5 h6.2 v6.2 h-6.2 Z M1.5,8.3 h6.2 v6.2 h-6.2 Z M8.3,8.3 h6.2 v6.2 h-6.2 Z")),
        M("linux", "Linux", "Servers and hypervisors", "tux server", L("#26292D", "#AEB6BF", 0.6, "M8,1.3 C5.9,1.3 5,3 5,4.9 C5,6.2 4.6,6.9 3.8,8.2 C2.9,9.6 2.2,11.2 2.8,12.8 C3.2,13.8 4.3,14.3 5.4,14.4 H10.6 C11.7,14.3 12.8,13.8 13.2,12.8 C13.8,11.2 13.1,9.6 12.2,8.2 C11.4,6.9 11,6.2 11,4.9 C11,3 10.1,1.3 8,1.3 Z"), L("#F4F6F8", null, 0, "M8,6.4 C6.4,6.4 5.4,8.3 5.4,10.3 C5.4,12.2 6.3,13.7 8,13.7 C9.7,13.7 10.6,12.2 10.6,10.3 C10.6,8.3 9.6,6.4 8,6.4 Z"), L("#F4F6F8", null, 0, "M6.40,3.90 A0.6,0.6 0 1 1 7.60,3.90 A0.6,0.6 0 1 1 6.40,3.90 Z M8.40,3.90 A0.6,0.6 0 1 1 9.60,3.90 A0.6,0.6 0 1 1 8.40,3.90 Z"), L("#F5B700", null, 0, "M6.8,5.1 H9.2 L8,6.5 Z M3.3,14.5 L3.7,13.3 C4.4,12.4 5.9,12.4 6.5,13.3 L6.9,14.5 Z M9.1,14.5 L9.5,13.3 C10.1,12.4 11.6,12.4 12.3,13.3 L12.7,14.5 Z")),
        M("ubuntu", "Ubuntu", "Servers and hypervisors", "linux", L("#E95420", null, 0, "M1.50,8.00 A6.5,6.5 0 1 1 14.50,8.00 A6.5,6.5 0 1 1 1.50,8.00 Z"), L(null, "#F4F6F8", 1.2, "M5.00,8.00 A3,3 0 1 1 11.00,8.00 A3,3 0 1 1 5.00,8.00 Z"), L("#F4F6F8", "#E95420", 0.8, "M10.85,8.00 A1.25,1.25 0 1 1 13.35,8.00 A1.25,1.25 0 1 1 10.85,8.00 Z"), L("#F4F6F8", "#E95420", 0.8, "M4.70,4.45 A1.25,1.25 0 1 1 7.20,4.45 A1.25,1.25 0 1 1 4.70,4.45 Z"), L("#F4F6F8", "#E95420", 0.8, "M4.70,11.55 A1.25,1.25 0 1 1 7.20,11.55 A1.25,1.25 0 1 1 4.70,11.55 Z")),
        M("debian", "Debian", "Servers and hypervisors", "linux", L(null, "#D70A53", 1.5, "M10.6,13 A5.6,5.6 0 1 1 13.6,7.6 C13.4,5.3 11.4,3.6 9.1,3.6 C6.7,3.6 4.8,5.5 4.8,7.9 C4.8,10.1 6.5,11.7 8.5,11.7 C10.2,11.7 11.5,10.5 11.5,8.9 C11.5,7.4 10.4,6.4 9.1,6.5")),
        M("redhat", "Red Hat", "Servers and hypervisors", "rhel linux", L("#EE0000", null, 0, "M1.8,9.3 C1,10 2,11.4 4.4,12.1 C7,12.9 10.6,13 12.8,12.2 C14.5,11.6 14.8,10.5 14,9.7 C13.6,10.7 11.9,11 9.9,11 C7.2,11 3.5,10.2 1.8,9.3 Z M4.3,8.4 L5.5,4.3 C5.8,3.4 6.5,3 7.3,3.3 C7.9,3.5 8.4,3.7 9,3.5 C9.9,3.2 10.7,3.6 11,4.5 L12.1,8.1 C11.4,8.7 9.9,9 8.2,8.8 C6.6,8.6 5,8.2 4.3,8.4 Z")),
        // Proxmox: the orange outer chevrons pointing in, with the X crossing between them (light instead of black so it reads on dark themes).
        M("proxmox", "Proxmox", "Servers and hypervisors", "pve hypervisor vm", L("#E57000", null, 0, "M0.8,2 H3.3 L6.3,8 L3.3,14 H0.8 L3.8,8 Z M15.2,2 H12.7 L9.7,8 L12.7,14 H15.2 L12.2,8 Z"), L("#E6E9ED", null, 0, "M4.5,2 H6.6 L8,4.9 L9.4,2 H11.5 L9.05,8 L11.5,14 H9.4 L8,11.1 L6.6,14 H4.5 L6.95,8 Z")),
        M("truenas", "TrueNAS", "Servers and hypervisors", "nas storage zfs", L("#31BEEC", null, 0, "M2,4 L8,1.5 L14,4 L8,6.5 Z"), L("#0095D5", null, 0, "M2,7.4 L8,9.9 L14,7.4 V9.2 L8,11.7 L2,9.2 Z"), L("#1C6FA8", null, 0, "M2,11 L8,13.5 L14,11 V12.8 L8,15.3 L2,12.8 Z")),
        M("docker", "Docker", "Containers and automation", "container", L("#2496ED", null, 0, "M1,8.4 H13.3 C13.8,7.5 14.7,7.3 15.5,7.5 C15.2,8.4 14.6,9 13.7,9.1 C12.7,12 10,13.9 6.2,13.9 C3.3,13.9 1.6,12.3 1,8.4 Z M2.9,6.4 h1.6 v1.6 h-1.6 Z M4.8,6.4 h1.6 v1.6 h-1.6 Z M6.7,6.4 h1.6 v1.6 h-1.6 Z M8.6,6.4 h1.6 v1.6 h-1.6 Z M4.8,4.5 h1.6 v1.6 h-1.6 Z M6.7,4.5 h1.6 v1.6 h-1.6 Z M8.6,4.5 h1.6 v1.6 h-1.6 Z M8.6,2.6 h1.6 v1.6 h-1.6 Z")),
        M("kubernetes", "Kubernetes", "Containers and automation", "k8s container", L("#326CE5", "#326CE5", 1.2, "M8.00,1.40 L13.39,4.00 L14.73,9.84 L10.99,14.52 L5.01,14.52 L1.27,9.84 L2.61,4.00 Z"), L(null, "#F4F6F8", 1.0, "M5.20,8.30 A2.8,2.8 0 1 1 10.80,8.30 A2.8,2.8 0 1 1 5.20,8.30 Z M8.00,6.70 L8.00,3.70 M9.25,7.30 L11.60,5.43 M9.56,8.66 L12.48,9.32 M8.69,9.74 L10.00,12.44 M7.31,9.74 L6.00,12.44 M6.44,8.66 L3.52,9.32 M6.75,7.30 L4.40,5.43")),
        M("portainer", "Portainer", "Containers and automation", "docker containers", L(null, "#13BEF9", 1.3, "M6,14.5 V2.5 M2.5,4 H13.5 M6,2.5 L11,4 M11.5,4 V7"), L("#13BEF9", null, 0, "M8.6,8.6 H14 V14.5 H8.6 Z")),
        M("traefik", "Traefik", "Containers and automation", "proxy reverse", L("#24A1C1", null, 0, "M1.40,8.00 A6.6,6.6 0 1 1 14.60,8.00 A6.6,6.6 0 1 1 1.40,8.00 Z"), L(null, "#F4F6F8", 1.3, "M4.3,5.8 H6.8 L9.2,10.2 H11.7 M4.3,10.2 H6.8 L9.2,5.8 H11.7")),
        M("nginx", "Nginx", "Containers and automation", "proxy web npm", L("#009639", null, 0, "M8.00,1.20 L13.89,4.60 L13.89,11.40 L8.00,14.80 L2.11,11.40 L2.11,4.60 Z"), L(null, "#F4F6F8", 1.5, "M5.8,10.8 V5.2 L10.2,10.8 V5.2")),
        M("ansible", "Ansible", "Containers and automation", "automation red hat", L("#EE0000", null, 0, "M1.40,8.00 A6.6,6.6 0 1 1 14.60,8.00 A6.6,6.6 0 1 1 1.40,8.00 Z"), L("#F4F6F8", null, 0, "M8,3.6 L11.6,12.3 H10.1 L9.3,10.3 L6.4,12.1 L6,11 L8.8,9.1 L8,7 L5.9,12.3 H4.4 Z")),
        M("gitlab", "GitLab", "Containers and automation", "git", L("#FC6D26", null, 0, "M8,14.2 L1.6,9.4 L3.2,2.2 L5.3,7.8 H10.7 L12.8,2.2 L14.4,9.4 Z"), L("#FCA326", null, 0, "M8,14.2 L5.3,7.8 H10.7 Z")),
        M("zabbix", "Zabbix", "Monitoring", "monitoring", L("#D40000", null, 0, "M2.5,1.5 H13.5 A1,1 0 0 1 14.5,2.5 V13.5 A1,1 0 0 1 13.5,14.5 H2.5 A1,1 0 0 1 1.5,13.5 V2.5 A1,1 0 0 1 2.5,1.5 Z"), L("#F4F6F8", null, 0, "M4.5,4.3 H11.5 V5.7 L6.6,10.3 H11.5 V11.7 H4.5 V10.3 L9.4,5.7 H4.5 Z")),
        M("grafana", "Grafana", "Monitoring", "dashboard metrics", L(null, "#F46800", 2.0, "M4.10,8.00 A3.9,3.9 0 1 1 11.90,8.00 A3.9,3.9 0 1 1 4.10,8.00 Z"), L(null, "#F46800", 1.8, "M13.20,8.00 L14.60,8.00 M11.68,11.68 L12.67,12.67 M8.00,13.20 L8.00,14.60 M4.32,11.68 L3.33,12.67 M2.80,8.00 L1.40,8.00 M4.32,4.32 L3.33,3.33 M8.00,2.80 L8.00,1.40 M11.68,4.32 L12.67,3.33")),
        M("prometheus", "Prometheus", "Monitoring", "metrics monitoring", L("#E6522C", null, 0, "M1.40,8.00 A6.6,6.6 0 1 1 14.60,8.00 A6.6,6.6 0 1 1 1.40,8.00 Z"), L("#F4F6F8", null, 0, "M8,3.4 C9.6,5 10.6,6.3 10.4,8.2 C10.3,9.3 9.6,10 8.9,10.3 C9.2,9.4 8.9,8.6 8.2,8 C8.2,9 7.4,9.5 7,10.3 C6.2,10 5.6,9.2 5.6,8.2 C5.6,6.5 7.4,5.6 8,3.4 Z M5.2,11 H10.8 V11.9 H5.2 Z M6,12.5 H10 V13.4 H6 Z")),
        M("uptimekuma", "Uptime Kuma", "Monitoring", "uptime monitor status", L("#5CDD8B", null, 0, "M1.40,8.00 A6.6,6.6 0 1 1 14.60,8.00 A6.6,6.6 0 1 1 1.40,8.00 Z"), L(null, "#F4F6F8", 1.3, "M3.2,8.4 H5.6 L6.8,5.4 L8.6,11 L9.9,7.4 L10.6,8.4 H12.8")),
        M("influxdb", "InfluxDB", "Databases", "influx tsdb database metrics", L("#9394FF", null, 0, "M8.00,1.20 L13.89,4.60 L13.89,11.40 L8.00,14.80 L2.11,11.40 L2.11,4.60 Z"), L(null, "#F4F6F8", 1.1, "M8,8 L8,14.2 M8,8 L2.6,4.9 M8,8 L13.4,4.9")),
        M("redis", "Redis", "Databases", "cache database", L("#DC382D", null, 0, "M8,2 L14,4.6 L8,7.2 L2,4.6 Z"), L("#B52A1F", null, 0, "M2,7.4 L8,10 L14,7.4 V9.2 L8,11.8 L2,9.2 Z M2,11 L8,13.6 L14,11 V12.8 L8,15.4 L2,12.8 Z")),
        M("homeassistant", "Home Assistant", "Self-hosted apps", "hass smart home", L("#18BCF2", null, 0, "M8,1.5 L14.5,7.3 V14.5 H1.5 V7.3 Z"), L(null, "#F4F6F8", 0.9, "M8,14.5 V6.5 M8,10.2 L5.6,8 M8,11.6 L10.4,9.4"), L("#F4F6F8", null, 0, "M7.05,6.30 A0.95,0.95 0 1 1 8.95,6.30 A0.95,0.95 0 1 1 7.05,6.30 Z M4.45,7.80 A0.95,0.95 0 1 1 6.35,7.80 A0.95,0.95 0 1 1 4.45,7.80 Z M9.65,9.20 A0.95,0.95 0 1 1 11.55,9.20 A0.95,0.95 0 1 1 9.65,9.20 Z")),
        // Pi-hole: two green leaves on a red rounded-diamond berry, shaded darker on its right half like the logo.
        M("pihole", "Pi-hole", "Self-hosted apps", "dns adblock", L("#3DAA4E", null, 0, "M8,5.7 C6.6,3.7 4.4,3.3 2.9,4.1 C4.2,5.8 6.3,6.2 8,5.7 Z M8,5.7 C8.2,3.4 9.8,1.8 12.2,1.6 C11.9,3.9 10.2,5.4 8,5.7 Z"), L("#F0121F", null, 0, "M7.3,6.6 Q8,5.9 8.7,6.6 L11.7,9.6 Q12.4,10.3 11.7,11 L8.7,14 Q8,14.7 7.3,14 L4.3,11 Q3.6,10.3 4.3,9.6 Z"), L("#A50B14", null, 0, "M8,6.1 Q8.4,6.2 8.7,6.6 L11.7,9.6 Q12.4,10.3 11.7,11 L8.7,14 Q8.4,14.4 8,14.5 Z")),
        M("nextcloud", "Nextcloud", "Self-hosted apps", "files cloud", L(null, "#0082C9", 1.5, "M5.30,8.00 A2.7,2.7 0 1 1 10.70,8.00 A2.7,2.7 0 1 1 5.30,8.00 Z"), L(null, "#0082C9", 1.3, "M1.40,8.00 A1.4,1.4 0 1 1 4.20,8.00 A1.4,1.4 0 1 1 1.40,8.00 Z M11.80,8.00 A1.4,1.4 0 1 1 14.60,8.00 A1.4,1.4 0 1 1 11.80,8.00 Z")),
        M("plex", "Plex", "Self-hosted apps", "media", L("#E5A00D", null, 0, "M4.5,2.5 H8.2 L12,8 L8.2,13.5 H4.5 L8.3,8 Z")),
        M("jellyfin", "Jellyfin", "Self-hosted apps", "media", L("#9B5DE5", null, 0, "M8,2 L14.5,13.5 H1.5 Z M8,6.2 L5.2,11.3 H10.8 Z"), L("#00A4DC", null, 0, "M8,7.7 L6.5,10.4 H9.5 Z")),
        M("bitwarden", "Bitwarden", "Self-hosted apps", "vaultwarden passwords", L("#175DDC", null, 0, "M2.5,1.8 H13.5 V8 C13.5,11.2 10.8,13.3 8,14.5 C5.2,13.3 2.5,11.2 2.5,8 Z"), L("#F4F6F8", null, 0, "M8,3.4 H11.9 V8 C11.9,10.3 10,12 8,12.9 Z")),
    ];

    public static Mark? Find(string? key) => key is null ? null : All.FirstOrDefault(m => m.Key == key);

    internal readonly record struct Layer(string? Fill, string? Stroke, double Width, string Data);

    private static Layer L(string? fill, string? stroke, double width, string data) => new(fill, stroke, width, data);

    private static Mark M(string key, string name, string group, string keywords, params Layer[] layers) =>
        new(key, name, group, keywords, Build(layers, c => c)) { Layers = layers };

    private static DrawingGroup Build(IReadOnlyList<Layer> layers, Func<Color, Color> map)
    {
        var g = new DrawingGroup();
        foreach (var l in layers)
        {
            var geo = Geometry.Parse(l.Data);
            geo.Freeze();
            var pen = l.Stroke is null ? null : new Pen(Brush(map(Parse(l.Stroke))), l.Width)
            {
                StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round,
            };
            pen?.Freeze();
            g.Children.Add(new GeometryDrawing(l.Fill is null ? null : Brush(map(Parse(l.Fill))), pen, geo));
        }
        g.ClipGeometry = new RectangleGeometry(new Rect(0, 0, 16, 16));
        g.Freeze();
        return g;
    }

    /// <summary>Hue and saturation from the tint, lightness from the original colour (kept off pure black and pure white).</summary>
    private static Color Retint(Color c, Color tint)
    {
        var (_, _, l) = ToHsl(c);
        var (th, ts, _) = ToHsl(tint);
        return FromHsl(th, ts, Math.Clamp(l, 0.18, 0.94), c.A);
    }

    private static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, d = max - min;
        if (d < 1e-9) return (0, 0, l);
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        var h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h / 6, s, l);
    }

    private static Color FromHsl(double h, double s, double l, byte a)
    {
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            return t < 1.0 / 6 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6 : p;
        }
        byte B(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return Color.FromArgb(a, B(Hue(p, q, h + 1.0 / 3)), B(Hue(p, q, h)), B(Hue(p, q, h - 1.0 / 3)));
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static SolidColorBrush Brush(Color color)
    {
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }
}
