namespace RemoteMan.Core.Model;

/// <summary>
/// The starter snippet library, added on first run (and from the Snippets panel's menu) as normal
/// snippet entries, so they can be edited, deleted and shared like any other.
/// Groups are "Category/Group": the panel shows one category at a time, with collapsible groups.
/// Snippets with PressEnter = false are typed but not run, because they need a value filled in
/// (an IP, an interface name) or they reboot or change something.
/// </summary>
public static class BuiltInSnippets
{
    public sealed record Def(string Group, string Name, string Command, bool PressEnter = true);

    public const string Debian = "Debian";
    public const string FortiOs = "FortiOS 7.6";
    public const string MikroTik = "MikroTik 7.25";

    public static IReadOnlyList<Def> All { get; } =
    [
        // ---------------- Debian ----------------
        new($"{Debian}/System", "Debian version", "cat /etc/debian_version; grep PRETTY_NAME /etc/os-release"),
        new($"{Debian}/System", "System overview", "hostnamectl; uptime; free -h"),
        new($"{Debian}/System", "CPU and memory hogs", "ps -eo pid,user,%cpu,%mem,etime,cmd --sort=-%cpu | head -15"),
        new($"{Debian}/System", "Time and NTP sync", "timedatectl"),
        new($"{Debian}/System", "Kernel messages (errors)", "sudo dmesg --level=err,warn -T | tail -30"),
        new($"{Debian}/System", "Reboot", "sudo systemctl reboot", PressEnter: false),

        new($"{Debian}/Packages", "Update everything", "sudo apt update && sudo apt full-upgrade -y"),
        new($"{Debian}/Packages", "List available updates", "sudo apt update -qq && apt list --upgradable"),
        new($"{Debian}/Packages", "Remove unused packages", "sudo apt autoremove --purge -y && sudo apt clean"),
        new($"{Debian}/Packages", "Reboot needed?", "[ -f /var/run/reboot-required ] && cat /var/run/reboot-required /var/run/reboot-required.pkgs 2>/dev/null || echo 'No reboot needed'"),
        new($"{Debian}/Packages", "Is a package installed?", "dpkg -l | grep -i ", PressEnter: false),
        new($"{Debian}/Packages", "Package versions available", "apt-cache policy ", PressEnter: false),
        new($"{Debian}/Packages", "Automatic updates status", "systemctl status unattended-upgrades --no-pager; tail -5 /var/log/unattended-upgrades/unattended-upgrades.log 2>/dev/null"),

        new($"{Debian}/Services and logs", "Failed services", "systemctl --failed --no-pager"),
        new($"{Debian}/Services and logs", "Running services", "systemctl list-units --type=service --state=running --no-pager"),
        new($"{Debian}/Services and logs", "Errors since boot", "journalctl -p err -b --no-pager | tail -50"),
        new($"{Debian}/Services and logs", "Follow the log", "journalctl -f"),
        new($"{Debian}/Services and logs", "Log of one service", "journalctl -e --no-pager -n 100 -u ", PressEnter: false),
        new($"{Debian}/Services and logs", "Restart a service", "sudo systemctl restart ", PressEnter: false),
        new($"{Debian}/Services and logs", "Journal disk use", "journalctl --disk-usage"),

        new($"{Debian}/Network", "Addresses", "ip -br -c address"),
        new($"{Debian}/Network", "Routes", "ip route"),
        new($"{Debian}/Network", "Link stats and errors", "ip -s -c link"),
        new($"{Debian}/Network", "Listening ports", "sudo ss -tulpn"),
        new($"{Debian}/Network", "DNS servers", "resolvectl status 2>/dev/null || cat /etc/resolv.conf"),
        new($"{Debian}/Network", "Interfaces config", "cat /etc/network/interfaces /etc/network/interfaces.d/* 2>/dev/null"),
        new($"{Debian}/Network", "Ping the gateway", "ping -c 4 $(ip route | awk '/default/ {print $3; exit}')"),
        new($"{Debian}/Network", "Firewall rules (nftables)", "sudo nft list ruleset"),

        new($"{Debian}/Disks", "Disk usage", "df -hT -x tmpfs -x devtmpfs"),
        new($"{Debian}/Disks", "Block devices", "lsblk -f"),
        new($"{Debian}/Disks", "Biggest folders here", "sudo du -xh --max-depth=1 . 2>/dev/null | sort -rh | head -20"),
        new($"{Debian}/Disks", "Files over 500 MB", "sudo find / -xdev -type f -size +500M -exec ls -lh {} + 2>/dev/null"),
        new($"{Debian}/Disks", "Software RAID status", "cat /proc/mdstat"),
        new($"{Debian}/Disks", "Disk health (SMART)", "sudo smartctl -H /dev/", PressEnter: false),

        new($"{Debian}/Users and security", "Who is logged in", "w"),
        new($"{Debian}/Users and security", "Last logins", "last -n 20"),
        new($"{Debian}/Users and security", "Failed SSH logins today", "sudo journalctl -u ssh --since today --no-pager | grep -iE 'failed|invalid' | tail -30"),
        new($"{Debian}/Users and security", "Members of sudo", "getent group sudo"),
        new($"{Debian}/Users and security", "Fail2ban SSH jail", "sudo fail2ban-client status sshd"),
        new($"{Debian}/Users and security", "Become root", "sudo -i"),

        // ---------------- FortiOS 7.6 ----------------
        new($"{FortiOs}/Basics", "No paging (more)", "config system console\nset output standard\nend"),
        new($"{FortiOs}/Basics", "System status", "get system status"),
        new($"{FortiOs}/Basics", "CPU, memory, uptime", "get system performance status"),
        new($"{FortiOs}/Basics", "Top processes", "diagnose sys top 2 20 1"),
        new($"{FortiOs}/Basics", "Logged-in admins", "get system info admin status"),

        new($"{FortiOs}/Interfaces", "Physical ports", "get system interface physical"),
        new($"{FortiOs}/Interfaces", "Interface config", "show system interface"),
        new($"{FortiOs}/Interfaces", "ARP table", "get system arp"),
        new($"{FortiOs}/Interfaces", "DHCP leases", "execute dhcp lease-list"),
        new($"{FortiOs}/Interfaces", "Port details", "diagnose hardware deviceinfo nic ", PressEnter: false),

        new($"{FortiOs}/Routing", "Routing table", "get router info routing-table all"),
        new($"{FortiOs}/Routing", "Route lookup", "get router info routing-table details ", PressEnter: false),
        new($"{FortiOs}/Routing", "BGP summary", "get router info bgp summary"),
        new($"{FortiOs}/Routing", "OSPF neighbours", "get router info ospf neighbor"),
        new($"{FortiOs}/Routing", "SD-WAN members", "diagnose sys sdwan member"),
        new($"{FortiOs}/Routing", "SD-WAN health checks", "diagnose sys sdwan health-check"),

        new($"{FortiOs}/Firewall", "Policies", "show firewall policy"),
        new($"{FortiOs}/Firewall", "Address objects", "show firewall address"),
        new($"{FortiOs}/Firewall", "Session count", "get system session status"),
        new($"{FortiOs}/Firewall", "Session stats", "diagnose sys session stat"),

        new($"{FortiOs}/VPN", "IPsec tunnels", "get vpn ipsec tunnel summary"),
        new($"{FortiOs}/VPN", "IPsec tunnel details", "diagnose vpn tunnel list"),
        new($"{FortiOs}/VPN", "IKE gateways", "diagnose vpn ike gateway list"),
        new($"{FortiOs}/VPN", "SSL VPN users", "get vpn ssl monitor"),

        new($"{FortiOs}/Troubleshooting", "Debug flow (add the IP)",
            "diagnose debug reset\ndiagnose debug flow filter clear\ndiagnose debug flow show function-name enable\ndiagnose debug console timestamp enable\ndiagnose debug flow trace start 20\ndiagnose debug enable\ndiagnose debug flow filter addr ",
            PressEnter: false),
        new($"{FortiOs}/Troubleshooting", "Stop debugging", "diagnose debug disable\ndiagnose debug reset"),
        new($"{FortiOs}/Troubleshooting", "Packet sniffer", "diagnose sniffer packet any 'host ' 4 20 l", PressEnter: false),
        new($"{FortiOs}/Troubleshooting", "Ping", "execute ping ", PressEnter: false),
        new($"{FortiOs}/Troubleshooting", "Traceroute", "execute traceroute ", PressEnter: false),
        new($"{FortiOs}/Troubleshooting", "FortiGuard connection", "diagnose debug rating"),

        new($"{FortiOs}/HA and maintenance", "HA status", "get system ha status"),
        new($"{FortiOs}/HA and maintenance", "HA config in sync?", "diagnose sys ha checksum cluster"),
        new($"{FortiOs}/HA and maintenance", "Signature versions", "diagnose autoupdate versions"),
        new($"{FortiOs}/HA and maintenance", "Reboot", "execute reboot", PressEnter: false),

        // ---------------- MikroTik RouterOS 7.25 ----------------
        new($"{MikroTik}/System", "Resources", "/system resource print"),
        new($"{MikroTik}/System", "RouterBOARD and firmware", "/system routerboard print"),
        new($"{MikroTik}/System", "Health (voltage, temperature)", "/system health print"),
        new($"{MikroTik}/System", "Packages", "/system package print"),
        new($"{MikroTik}/System", "Clock and NTP", "/system clock print; /system ntp client print"),
        new($"{MikroTik}/System", "Active users", "/user active print"),
        new($"{MikroTik}/System", "Recent errors", "/log print where topics~\"error|critical\""),

        new($"{MikroTik}/Interfaces", "Interfaces", "/interface print"),
        new($"{MikroTik}/Interfaces", "Ethernet ports", "/interface ethernet print"),
        new($"{MikroTik}/Interfaces", "Bridge ports", "/interface bridge port print"),
        new($"{MikroTik}/Interfaces", "Bridge VLANs", "/interface bridge vlan print"),
        new($"{MikroTik}/Interfaces", "VLAN interfaces", "/interface vlan print"),
        new($"{MikroTik}/Interfaces", "Live traffic on a port", "/interface monitor-traffic ", PressEnter: false),
        new($"{MikroTik}/Interfaces", "WiFi clients", "/interface wifi registration-table print"),
        new($"{MikroTik}/Interfaces", "WireGuard peers", "/interface wireguard peers print"),

        new($"{MikroTik}/IP", "Addresses", "/ip address print"),
        new($"{MikroTik}/IP", "Routes", "/ip route print"),
        new($"{MikroTik}/IP", "ARP", "/ip arp print"),
        new($"{MikroTik}/IP", "DHCP leases", "/ip dhcp-server lease print"),
        new($"{MikroTik}/IP", "DNS settings", "/ip dns print"),
        new($"{MikroTik}/IP", "Neighbours", "/ip neighbor print"),

        new($"{MikroTik}/Firewall", "Filter rules (with counters)", "/ip firewall filter print stats"),
        new($"{MikroTik}/Firewall", "NAT rules", "/ip firewall nat print"),
        new($"{MikroTik}/Firewall", "Address lists", "/ip firewall address-list print"),
        new($"{MikroTik}/Firewall", "Connection count", "/ip firewall connection print count-only"),

        new($"{MikroTik}/Routing", "OSPF neighbours", "/routing ospf neighbor print"),
        new($"{MikroTik}/Routing", "BGP sessions", "/routing bgp session print"),

        new($"{MikroTik}/Tools", "Ping", "/ping count=4 ", PressEnter: false),
        new($"{MikroTik}/Tools", "Traceroute", "/tool traceroute ", PressEnter: false),
        new($"{MikroTik}/Tools", "Torch (live flows on a port)", "/tool torch interface=", PressEnter: false),

        new($"{MikroTik}/Backup and updates", "Show config", "/export terse"),
        new($"{MikroTik}/Backup and updates", "Show config with secrets", "/export terse show-sensitive"),
        new($"{MikroTik}/Backup and updates", "Save backup file", "/system backup save name={name} password=", PressEnter: false),
        new($"{MikroTik}/Backup and updates", "Check for updates", "/system package update check-for-updates"),
        new($"{MikroTik}/Backup and updates", "Install update and reboot", "/system package update install", PressEnter: false),
        new($"{MikroTik}/Backup and updates", "Upgrade RouterBOARD firmware", "/system routerboard upgrade", PressEnter: false),
        new($"{MikroTik}/Backup and updates", "Reboot", "/system reboot", PressEnter: false),
    ];

    public static Entry ToEntry(Def d) => new()
    {
        Kind = EntryKind.Snippet,
        Name = d.Name,
        Snippet = new SnippetData { Group = d.Group, Command = d.Command, PressEnter = d.PressEnter },
    };
}
