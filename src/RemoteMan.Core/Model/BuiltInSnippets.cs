namespace RemoteMan.Core.Model;

/// <summary>
/// The starter snippet library, added on first run (and from the Snippets panel's menu) as normal
/// snippet entries, so they can be edited, deleted and shared like any other.
/// </summary>
public static class BuiltInSnippets
{
    public sealed record Def(string Group, string Name, string Command, bool PressEnter = true);

    public static IReadOnlyList<Def> All { get; } =
    [
        // Linux: system
        new("Linux", "System info", "hostnamectl; uptime; free -h"),
        new("Linux", "Disk usage", "df -h -x tmpfs -x devtmpfs -x squashfs"),
        new("Linux", "Biggest folders here", "du -xh --max-depth=1 . 2>/dev/null | sort -rh | head -20"),
        new("Linux", "Top processes (CPU)", "ps aux --sort=-%cpu | head -15"),
        new("Linux", "Top processes (memory)", "ps aux --sort=-%mem | head -15"),
        new("Linux", "Failed services", "systemctl --failed"),
        new("Linux", "Recent errors in the log", "journalctl -p err -b --no-pager | tail -50"),
        new("Linux", "Follow the system log", "journalctl -f"),
        new("Linux", "Who is logged in", "w"),
        new("Linux", "Last logins", "last -n 20"),
        new("Linux", "Become root", "sudo -i"),
        // Linux: packages
        new("Packages", "Update (Debian/Ubuntu)", "sudo apt update && sudo apt -y full-upgrade"),
        new("Packages", "Update (RHEL/Rocky/Fedora)", "sudo dnf -y upgrade"),
        new("Packages", "Update (Alpine)", "sudo apk update && sudo apk upgrade"),
        new("Packages", "Reboot needed?", "[ -f /var/run/reboot-required ] && cat /var/run/reboot-required || echo 'No reboot needed'"),
        // Network
        new("Network", "Addresses", "ip -br -c a"),
        new("Network", "Routes", "ip r"),
        new("Network", "Listening ports", "sudo ss -tulpn"),
        new("Network", "DNS servers", "resolvectl status 2>/dev/null | grep -A2 'DNS Servers' || cat /etc/resolv.conf"),
        new("Network", "Public IP", "curl -s https://ifconfig.me; echo"),
        new("Network", "Ping gateway", "ping -c 4 $(ip r | awk '/default/ {print $3; exit}')"),
        new("Network", "Firewall rules", "sudo nft list ruleset 2>/dev/null || sudo iptables -L -n -v"),
        // Docker
        new("Docker", "Running containers", "docker ps --format 'table {{.Names}}\\t{{.Status}}\\t{{.Ports}}'"),
        new("Docker", "All containers", "docker ps -a"),
        new("Docker", "Resource use", "docker stats --no-stream"),
        new("Docker", "Compose: pull and restart", "docker compose pull && docker compose up -d"),
        new("Docker", "Compose: logs", "docker compose logs -f --tail=100"),
        new("Docker", "Clean up unused", "docker system prune -f"),
        // Proxmox
        new("Proxmox", "List VMs", "qm list"),
        new("Proxmox", "List containers", "pct list"),
        new("Proxmox", "Storage status", "pvesm status"),
        new("Proxmox", "Cluster status", "pvecm status"),
        new("Proxmox", "Version", "pveversion -v | head -5"),
        new("Proxmox", "Running tasks", "cat /var/log/pve/tasks/active"),
        // Cisco IOS
        new("Cisco IOS", "No paging", "terminal length 0"),
        new("Cisco IOS", "Running config", "terminal length 0\nshow running-config"),
        new("Cisco IOS", "Interfaces brief", "show ip interface brief"),
        new("Cisco IOS", "Interface status", "show interfaces status"),
        new("Cisco IOS", "VLANs", "show vlan brief"),
        new("Cisco IOS", "Trunks", "show interfaces trunk"),
        new("Cisco IOS", "Routing table", "show ip route"),
        new("Cisco IOS", "Neighbours (CDP)", "show cdp neighbors detail"),
        new("Cisco IOS", "MAC table", "show mac address-table"),
        new("Cisco IOS", "Spanning tree", "show spanning-tree summary"),
        new("Cisco IOS", "Save config", "copy running-config startup-config\n"),
        // MikroTik RouterOS
        new("MikroTik", "Resources", "/system resource print"),
        new("MikroTik", "Interfaces", "/interface print"),
        new("MikroTik", "Addresses", "/ip address print"),
        new("MikroTik", "Routes", "/ip route print"),
        new("MikroTik", "Firewall filter", "/ip firewall filter print"),
        new("MikroTik", "DHCP leases", "/ip dhcp-server lease print"),
        new("MikroTik", "Export config", "/export terse"),
        // Windows (OpenSSH server, PowerShell)
        new("Windows", "System info", "systeminfo | findstr /B /C:\"OS Name\" /C:\"OS Version\" /C:\"System Boot Time\""),
        new("Windows", "IP config", "ipconfig /all"),
        new("Windows", "Listening ports", "netstat -ano | findstr LISTENING"),
        new("Windows", "Stopped automatic services", "powershell -c \"Get-Service | ? {$_.StartType -eq 'Automatic' -and $_.Status -ne 'Running'}\""),
        new("Windows", "Restart now", "shutdown /r /t 0", PressEnter: false),
    ];

    public static Entry ToEntry(Def d) => new()
    {
        Kind = EntryKind.Snippet,
        Name = d.Name,
        Snippet = new SnippetData { Group = d.Group, Command = d.Command, PressEnter = d.PressEnter },
    };
}
