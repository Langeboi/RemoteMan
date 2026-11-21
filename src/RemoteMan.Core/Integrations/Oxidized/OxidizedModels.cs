namespace RemoteMan.Core.Integrations.Oxidized;

/// <summary>A device Oxidized backs up, as /nodes.json lists it.</summary>
public sealed record OxidizedNode(
    string Name,
    string? Group,
    string? Ip,
    string? Model,
    /// <summary>success, no_connection, fail or never.</summary>
    string Status,
    DateTimeOffset? LastRun,
    /// <summary>When the stored config last changed, if Oxidized reports it.</summary>
    DateTimeOffset? LastChanged)
{
    /// <summary>"group/name", or just the name for nodes without a group. Oxidized's own key for a node.</summary>
    public string FullName => string.IsNullOrEmpty(Group) ? Name : $"{Group}/{Name}";

    public bool IsOk => Status.Equals("success", StringComparison.OrdinalIgnoreCase);
    public bool NeverRan => Status.Equals("never", StringComparison.OrdinalIgnoreCase) || Status.Length == 0;
    /// <summary>The last run didn't get a config (no connection, wrong login, timeout).</summary>
    public bool IsFailing => !IsOk && !NeverRan;

    public string StatusText => Status.ToLowerInvariant() switch
    {
        "success" => "OK",
        "no_connection" => "no connection",
        "fail" => "failed",
        "never" or "" => "not backed up yet",
        var s => s.Replace('_', ' '),
    };
}

/// <summary>One saved config in Oxidized's git output. Newest has the highest <see cref="Number"/>.</summary>
public sealed record OxidizedVersion(string Oid, DateTimeOffset Date, string? Author, string? Message, int Number);

/// <summary>Oxidized's view of one entry: the matched node, or nothing.</summary>
public sealed record OxidizedMatch(OxidizedNode Node, string Reason);
