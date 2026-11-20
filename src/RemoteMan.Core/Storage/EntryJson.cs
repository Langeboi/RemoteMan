using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteMan.Core.Model;

namespace RemoteMan.Core.Storage;

/// <summary>Serializes the protocol payload of an entry (everything except the indexed columns).</summary>
public static class EntryJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed class Payload
    {
        public string? Description { get; set; }
        public string? Tags { get; set; }
        public List<HostEndpoint>? Endpoints { get; set; }
        public Guid? CredentialId { get; set; }
        public LoginSource? Login { get; set; }
        /// <summary>Older versions stored SSO as this flag; still read.</summary>
        public bool? UseWindowsLogin { get; set; }
        public bool? Favorite { get; set; }
        public RdpSettings? Rdp { get; set; }
        public SshSettings? Ssh { get; set; }
        public WebSettings? Web { get; set; }
        public VncSettings? Vnc { get; set; }
        public CredentialData? Credential { get; set; }
    }

    public static string ToPayload(Entry e) => JsonSerializer.Serialize(new Payload
    {
        Description = e.Description,
        Tags = e.Tags,
        Endpoints = e.Endpoints.Count > 0 ? e.Endpoints : null,
        CredentialId = e.CredentialId,
        Login = e.Login == LoginSource.None ? null : e.Login,
        Favorite = e.Favorite ? true : null,
        Rdp = e.Rdp, Ssh = e.Ssh, Web = e.Web, Vnc = e.Vnc, Credential = e.Credential,
    }, Options);

    public static void ApplyPayload(Entry e, string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        var p = JsonSerializer.Deserialize<Payload>(json, Options) ?? new Payload();
        e.Description = p.Description;
        e.Tags = p.Tags;
        e.Endpoints = p.Endpoints ?? new();
        e.CredentialId = p.CredentialId;
        // Entries from before login sources: SSO flag, else a linked credential means "saved".
        e.Login = p.Login ?? (p.UseWindowsLogin == true ? LoginSource.WindowsLogin
            : p.CredentialId is not null ? LoginSource.Saved : LoginSource.None);
        e.Favorite = p.Favorite == true;
        e.Rdp = p.Rdp; e.Ssh = p.Ssh; e.Web = p.Web; e.Vnc = p.Vnc; e.Credential = p.Credential;
    }
}
