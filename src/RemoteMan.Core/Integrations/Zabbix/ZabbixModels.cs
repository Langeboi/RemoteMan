namespace RemoteMan.Core.Integrations.Zabbix;

/// <summary>Zabbix trigger severities, in Zabbix's own numbering.</summary>
public enum ZabbixSeverity
{
    NotClassified = 0,
    Information = 1,
    Warning = 2,
    Average = 3,
    High = 4,
    Disaster = 5,
}

/// <summary>What the glyph on an entry shows. Ordered from least to most urgent.</summary>
public enum ZabbixHealthState
{
    /// <summary>The entry isn't linked to any Zabbix host: no glyph.</summary>
    None = 0,
    Ok = 1,
    Maintenance = 2,
    /// <summary>Zabbix can't reach the host's agent (or SNMP/IPMI/JMX interface).</summary>
    Unreachable = 3,
    Warning = 4,
    High = 5,
}

public sealed record ZabbixInterface(string Ip, string Dns, bool UseIp, string Port, int Type, int Available)
{
    /// <summary>Zabbix interface types: 1 agent, 2 SNMP, 3 IPMI, 4 JMX.</summary>
    public string TypeName => Type switch { 1 => "agent", 2 => "SNMP", 3 => "IPMI", 4 => "JMX", _ => "interface" };

    /// <summary>The address Zabbix itself connects to.</summary>
    public string Address => UseIp || string.IsNullOrWhiteSpace(Dns) ? Ip : Dns;
}

public sealed record ZabbixHost(
    string HostId,
    string Host,
    string Name,
    bool Enabled,
    bool InMaintenance,
    IReadOnlyList<ZabbixInterface> Interfaces,
    IReadOnlyList<string> Groups,
    IReadOnlyList<string> Templates)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Host : Name;

    /// <summary>True when every interface Zabbix has checked is down and none is up.</summary>
    public bool Unreachable =>
        Interfaces.Any(i => i.Available == 2) && !Interfaces.Any(i => i.Available == 1);

    public ZabbixInterface? MainInterface =>
        Interfaces.FirstOrDefault(i => i.Type == 1) ?? Interfaces.FirstOrDefault();
}

public sealed record ZabbixProblem(
    string EventId,
    string HostId,
    string Name,
    ZabbixSeverity Severity,
    DateTimeOffset Since,
    bool Acknowledged,
    string? AcknowledgedBy,
    bool Suppressed);

/// <summary>Health for one entry: the glyph state, why it matched and the open problems behind it.</summary>
public sealed record ZabbixHealth(
    ZabbixHealthState State,
    ZabbixHost? Host,
    string? MatchReason,
    IReadOnlyList<ZabbixProblem> Problems)
{
    public static readonly ZabbixHealth Unlinked = new(ZabbixHealthState.None, null, null, []);

    public bool IsLinked => Host is not null;

    /// <summary>A problem worth counting on a folder: warning or worse, or the agent is unreachable.</summary>
    public bool IsBad => State is ZabbixHealthState.Warning or ZabbixHealthState.High or ZabbixHealthState.Unreachable;

    public string Summary => State switch
    {
        ZabbixHealthState.None => Host is null ? "Not linked" : "Monitoring disabled in Zabbix",
        ZabbixHealthState.Maintenance => Problems.Count > 0 ? $"In maintenance, {Plural(Problems.Count, "open problem")}" : "In maintenance",
        ZabbixHealthState.Unreachable when Problems.Count == 0 => "Agent unreachable",
        _ when Problems.Count > 0 => Plural(Problems.Count, "open problem"),
        _ => "OK",
    };

    private static string Plural(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";
}
