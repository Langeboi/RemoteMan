using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteMan.Core.Model;

namespace RemoteMan.Core.Integrations.Oxidized;

/// <summary>How Oxidized groups the devices RemoteMan sends it.</summary>
public enum OxidizedFeedGroup
{
    /// <summary>No group (Oxidized's default).</summary>
    None,
    /// <summary>The entry's top-level folder.</summary>
    TopFolder,
}

/// <summary>What RemoteMan's server publishes as Oxidized's device list. Saved with the other Oxidized settings.</summary>
public sealed record OxidizedFeedOptions(OxidizedFeedGroup Group = OxidizedFeedGroup.None, string? DefaultModel = null,
    string? LeaveOutTag = OxidizedFeed.DefaultLeaveOutTag, bool SendLogins = true);

/// <summary>The public part the RemoteMan server writes to the shared setting <c>oxidized.feed</c>.</summary>
public sealed class OxidizedFeedInfo
{
    /// <summary>Base64 SubjectPublicKeyInfo of the server's RSA key: logins are sealed with it.</summary>
    public string? PublicKey { get; set; }
    public int Version { get; set; }
    public string? Path { get; set; }
}

/// <summary>
/// The shared setting <c>oxidized.feed.sync</c>, written by an admin's RemoteMan and read by the server:
/// each SSH entry's login sealed for the server, the Oxidized names of entries that already matched a device
/// (so their backup history carries on), and devices that were only in Oxidized and should stay there.
/// </summary>
public sealed class OxidizedFeedSync
{
    /// <summary>HMAC key for the fingerprints, encrypted with the vault key so only RemoteMan users can use it.</summary>
    public string? Key { get; set; }
    public Dictionary<string, OxidizedSealedLogin> Logins { get; set; } = new();
    /// <summary>Entry id (lower case) to Oxidized "group/name".</summary>
    public Dictionary<string, string> Links { get; set; } = new();
    public List<OxidizedKeptNode> Keep { get; set; } = new();
    /// <summary>Entry ids (lower case) unticked in the device list: never sent.</summary>
    public List<string> Excluded { get; set; } = new();
    /// <summary>Entry ids ticked in the device list: sent even when new entries aren't added automatically.</summary>
    public List<string> Included { get; set; } = new();
    /// <summary>Entry id (lower case) to the Oxidized model picked in the device list; wins over everything else.</summary>
    public Dictionary<string, string> Models { get; set; } = new();

    public OxidizedFeedChoice Choice(bool autoAdd) => new(new HashSet<string>(Excluded, StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(Included, StringComparer.OrdinalIgnoreCase), autoAdd);
}

/// <summary>Which SSH entries go to Oxidized: all except unticked ones, or (no auto add) only ticked ones.</summary>
public sealed record OxidizedFeedChoice(IReadOnlySet<string> Excluded, IReadOnlySet<string> Included, bool AutoAdd)
{
    public bool IsChosen(Guid id)
    {
        var k = OxidizedFeed.IdKey(id);
        return !Excluded.Contains(k) && (AutoAdd || Included.Contains(k));
    }
}

/// <summary><see cref="F"/>: fingerprint of the login (to see when it changed); <see cref="S"/>: the sealed login.</summary>
public sealed class OxidizedSealedLogin
{
    public string F { get; set; } = "";
    public string S { get; set; } = "";
}

public sealed class OxidizedKeptNode
{
    public string Name { get; set; } = "";
    public string? Group { get; set; }
    public string? Ip { get; set; }
    public string? Model { get; set; }
}

public enum OxidizedFeedState
{
    /// <summary>Oxidized already has it.</summary>
    InOxidized,
    /// <summary>Not in Oxidized yet: it arrives on Oxidized's next reload.</summary>
    Missing,
    /// <summary>Not sent: no host, or the leave-out tag.</summary>
    LeftOut,
    /// <summary>Not sent: unticked in the device list (or new while new entries aren't added automatically).</summary>
    Off,
}

/// <summary>One SSH entry as Oxidized will see it.</summary>
public sealed record OxidizedFeedItem(Entry Entry, string Name, string? Group, string? Ip, int Port, string? Model,
    OxidizedFeedState State, OxidizedNode? Node, string Why)
{
    public string FullName => string.IsNullOrEmpty(Group) ? Name : $"{Group}/{Name}";
}

/// <summary>
/// RemoteMan's SSH entries as an Oxidized device list. The server (deploy/docker/rmserver/oxidized.py) builds the
/// same list for Oxidized; this side shows it in the app and keeps the logins and names up to date.
/// Keep the model tables in step with the server.
/// </summary>
public static class OxidizedFeed
{
    public const string FeedKey = "oxidized.feed";
    public const string SyncKey = "oxidized.feed.sync";
    public const string DefaultLeaveOutTag = "no-oxidized";

    public static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public static readonly IReadOnlyDictionary<string, string> IconModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["cisco"] = "ios", ["aruba"] = "aoscx", ["mikrotik"] = "routeros", ["fortigate"] = "fortios", ["opnsense"] = "opnsense",
        ["linux"] = "linuxgeneric", ["ubuntu"] = "linuxgeneric", ["debian"] = "linuxgeneric", ["redhat"] = "linuxgeneric",
        ["proxmox"] = "linuxgeneric",
    };

    public static readonly IReadOnlyDictionary<string, string> TagAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["fortinet"] = "fortios", ["fortigate"] = "fortios", ["mikrotik"] = "routeros", ["aruba-cx"] = "aoscx", ["arubaos-cx"] = "aoscx",
        ["juniper"] = "junos", ["arista"] = "eos", ["edgerouter"] = "edgeos", ["paloalto"] = "panos", ["palo-alto"] = "panos",
        ["linux"] = "linuxgeneric", ["nexus"] = "nxos", ["hp-procurve"] = "procurve", ["dell-os10"] = "os10",
    };

    public static readonly IReadOnlySet<string> KnownModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ios", "iosxe", "iosxr", "nxos", "asa", "aoscx", "aosw", "procurve", "comware", "routeros", "fortios", "junos",
        "eos", "edgeos", "edgeswitch", "airos", "vyos", "vyatta", "pfsense", "opnsense", "panos", "powerconnect", "dnos",
        "os10", "ftos", "ironware", "screenos", "sonicos", "mlnxos", "linuxgeneric", "cumulus", "awplus", "netgear",
        "zynos", "tplink", "dlink", "ciscosmb", "openwrt", "omniswitch", "firewareos", "sros", "timos",
    };

    private static readonly string[] ModelFieldNames = ["oxidized model", "oxidized"];
    /// <summary>Custom fields sent to Oxidized as the enable password (Cisco, ProCurve).</summary>
    public static readonly string[] EnableFieldNames = ["enable", "enable password", "enable secret"];

    public static IEnumerable<string> Tags(string? tags) =>
        (tags ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => t.ToLowerInvariant());

    /// <summary>Words in an entry's name that give the model away (HH-ROS-LEAF1, RD-CHR-02, PVE-ROSKILDE).</summary>
    public static readonly IReadOnlyDictionary<string, string> NameModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ros"] = "routeros", ["chr"] = "routeros", ["routeros"] = "routeros", ["mikrotik"] = "routeros", ["crs"] = "routeros", ["ccr"] = "routeros",
        ["fgt"] = "fortios", ["fortigate"] = "fortios", ["pve"] = "linuxgeneric", ["proxmox"] = "linuxgeneric",
    };

    /// <summary>
    /// The model picked in the device list, then custom field "Oxidized model", then a tag naming a model, then the vendor
    /// icon, then a word in the name, then the default.
    /// </summary>
    public static string? ModelFor(Entry e, string? defaultModel, string? chosen = null)
    {
        if (!string.IsNullOrWhiteSpace(chosen)) return chosen.Trim();
        return InferModel(e) ?? (string.IsNullOrWhiteSpace(defaultModel) ? null : defaultModel.Trim());
    }

    /// <summary>The model RemoteMan works out by itself, or null.</summary>
    public static string? InferModel(Entry e)
    {
        foreach (var f in e.Fields ?? [])
            if (!f.Secret && ModelFieldNames.Contains(f.Name.Trim(), StringComparer.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(f.Value))
                return f.Value.Trim();
        foreach (var t in Tags(e.Tags))
        {
            if (KnownModels.Contains(t)) return t;
            if (TagAliases.TryGetValue(t, out var m)) return m;
        }
        if (e.Icon is { } icon && IconModels.TryGetValue(icon.Trim(), out var byIcon)) return byIcon;
        foreach (var w in System.Text.RegularExpressions.Regex.Split(e.Name ?? "", "[^A-Za-z0-9]+"))
            if (w.Length > 0 && NameModels.TryGetValue(w, out var byName)) return byName;
        return null;
    }

    /// <summary>Common Oxidized models for the picker in the device list.</summary>
    public static readonly string[] CommonModels =
    [
        "routeros", "fortios", "procurve", "aoscx", "aosw", "ios", "iosxe", "nxos", "asa", "ftos", "dnos", "os10", "powerconnect",
        "edgeos", "edgeswitch", "airos", "junos", "eos", "comware", "vyos", "pfsense", "opnsense", "panos", "linuxgeneric",
    ];

    public static string IdKey(Guid id) => id.ToString().ToLowerInvariant();

    /// <summary>
    /// Every SSH entry as Oxidized will get it, in the server's order. <paramref name="snapshot"/> (Oxidized's current
    /// list) tells which are already there; <paramref name="links"/> are the names saved for matched entries.
    /// </summary>
    public static IReadOnlyList<OxidizedFeedItem> Plan(IEnumerable<Entry> all, OxidizedFeedOptions options,
        IReadOnlyDictionary<string, string>? links, OxidizedSnapshot? snapshot, OxidizedFeedChoice? choice = null,
        IReadOnlyDictionary<string, string>? models = null)
    {
        var list = all.Where(e => !e.Deleted).ToList();
        var folders = list.Where(e => e.Kind == EntryKind.Folder).ToDictionary(e => e.Id);
        string? TopFolder(Guid? parent)
        {
            string? name = null;
            for (var hops = 0; parent is { } p && folders.TryGetValue(p, out var f) && hops < 64; hops++) { name = f.Name; parent = f.ParentId; }
            return name;
        }
        var leaveOut = string.IsNullOrWhiteSpace(options.LeaveOutTag) ? null : options.LeaveOutTag.Trim().ToLowerInvariant();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<OxidizedFeedItem>();
        foreach (var e in list.Where(e => e.Kind == EntryKind.Ssh).OrderBy(e => IdKey(e.Id), StringComparer.Ordinal))
        {
            var model = ModelFor(e, options.DefaultModel, models?.GetValueOrDefault(IdKey(e.Id)));
            if (leaveOut is not null && Tags(e.Tags).Contains(leaveOut))
            {
                items.Add(new(e, e.Name, null, null, 0, model, OxidizedFeedState.LeftOut, null, $"Tagged {leaveOut}"));
                continue;
            }
            var ep = e.Endpoints.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.Host));
            if (ep is null)
            {
                items.Add(new(e, e.Name, null, null, 0, model, OxidizedFeedState.LeftOut, null, "No host"));
                continue;
            }
            var host = ep.Host.Trim();
            var port = ep.Port > 0 ? ep.Port : 22;
            string name; string? group;
            var link = !string.IsNullOrEmpty(e.OxidizedNode) ? e.OxidizedNode : links?.GetValueOrDefault(IdKey(e.Id));
            if (!string.IsNullOrEmpty(link))
            {
                var slash = link.LastIndexOf('/');
                (group, name) = slash < 0 ? (null, link) : (link[..slash], link[(slash + 1)..]);
                if (group?.Length == 0) group = null;
            }
            else
            {
                name = e.Name.Trim().Length > 0 ? e.Name.Trim() : host;
                group = options.Group == OxidizedFeedGroup.TopFolder ? TopFolder(e.ParentId) : null;
            }
            if (choice is not null && !choice.IsChosen(e.Id))
            {
                var had = snapshot?.Matcher.Get(string.IsNullOrEmpty(group) ? name : $"{group}/{name}");
                items.Add(new(e, name, group, host, port, model, OxidizedFeedState.Off, had,
                    had is not null ? "Not ticked: leaves Oxidized on its next reload" : "Not ticked"));
                continue;
            }
            // Oxidized finds devices by name alone, so names are unique across groups.
            if (!seen.Add(name)) { name = $"{name}-{host}"; seen.Add(name); }

            var full = string.IsNullOrEmpty(group) ? name : $"{group}/{name}";
            var node = snapshot?.Matcher.Get(full);
            items.Add(node is not null
                ? new(e, name, group, host, port, model, OxidizedFeedState.InOxidized, node, $"In Oxidized as {node.FullName}")
                : new(e, name, group, host, port, model, OxidizedFeedState.Missing, null,
                    snapshot is null ? "Oxidized hasn't answered yet" : "Not in Oxidized yet"));
        }
        return items;
    }

    /// <summary>Devices in Oxidized that no SSH entry will send: they drop out when Oxidized switches to RemoteMan's list, unless kept.</summary>
    public static IReadOnlyList<OxidizedNode> OnlyInOxidized(OxidizedSnapshot snapshot, IReadOnlyList<OxidizedFeedItem> plan, IEnumerable<OxidizedKeptNode> kept)
    {
        // Devices that belong to an entry (sent or unticked) are listed with that entry, not here.
        var sent = new HashSet<string>(plan.Where(i => i.Ip is not null).Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var i in plan) if (i.Node is not null) sent.Add(i.Node.Name);
        var keptNames = new HashSet<string>(kept.Select(k => k.Name), StringComparer.OrdinalIgnoreCase);
        return snapshot.Nodes.Where(n => !sent.Contains(n.Name) && !keptNames.Contains(n.Name)).ToList();
    }

    // ---------------- sealing logins for the server ----------------

    /// <summary>
    /// base64( "OX1" | length of the wrapped key (2 bytes, big endian) | RSA-OAEP-SHA256(AES key) | nonce(12) | ciphertext | tag(16) ).
    /// The entry id is the associated data, so a sealed login can't be moved to another entry.
    /// </summary>
    public static string Seal(string publicKeyBase64, string entryId, string plaintext)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var wrapped = rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA256);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var ct = new byte[plain.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(key, 16)) gcm.Encrypt(nonce, plain, ct, tag, Encoding.UTF8.GetBytes(entryId));
        CryptographicOperations.ZeroMemory(key);
        using var ms = new MemoryStream();
        ms.Write("OX1"u8);
        ms.WriteByte((byte)(wrapped.Length >> 8));
        ms.WriteByte((byte)wrapped.Length);
        ms.Write(wrapped);
        ms.Write(nonce);
        ms.Write(ct);
        ms.Write(tag);
        return Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>The JSON the server opens: u (username), p (password), e (enable password).</summary>
    public static string LoginJson(string? user, string? password, string? enable) =>
        JsonSerializer.Serialize(new Dictionary<string, string?> { ["u"] = user, ["p"] = password, ["e"] = enable }
            .Where(kv => !string.IsNullOrEmpty(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value));

    /// <summary>Changes when the login or the server's key changes; keyed so the stored value says nothing about the password.</summary>
    public static string Fingerprint(byte[] hmacKey, string publicKeyBase64, string entryId, string loginJson)
    {
        var mac = HMACSHA256.HashData(hmacKey, Encoding.UTF8.GetBytes($"{publicKeyBase64}\n{entryId}\n{loginJson}"));
        return Convert.ToBase64String(mac, 0, 16);
    }
}
