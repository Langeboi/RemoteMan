using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RemoteMan.Core.Chat;

public enum ChatRoomKind { Everyone = 0, Group = 1, Direct = 2 }

/// <summary>A conversation: the built-in Everyone room, a named group, or two people.</summary>
public sealed record ChatRoom(Guid Id, ChatRoomKind Kind, string? Name, IReadOnlyList<string> Members, string? CreatedBy, DateTimeOffset UpdatedAt, bool Deleted = false)
{
    /// <summary>Everyone using the data source. Never stored with members; every user is in it.</summary>
    public static readonly Guid EveryoneId = new("c4a70000-0000-4000-8000-000000000001");

    public static ChatRoom Everyone { get; } = new(EveryoneId, ChatRoomKind.Everyone, "Everyone", [], null, DateTimeOffset.UnixEpoch);

    public bool Includes(string user) => Kind == ChatRoomKind.Everyone || Members.Any(m => SameUser(m, user));

    /// <summary>The same id on every PC for the same two people, so both sides find one conversation.</summary>
    public static Guid DirectId(string a, string b)
    {
        var pair = new[] { a.Trim().ToLowerInvariant(), b.Trim().ToLowerInvariant() }.Order(StringComparer.Ordinal);
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("remoteman-dm\n" + string.Join("\n", pair)));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);   // version 3 (name based)
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash);
    }

    public static ChatRoom Direct(string me, string other) =>
        new(DirectId(me, other), ChatRoomKind.Direct, null, [me, other], me, DateTimeOffset.UtcNow);

    /// <summary>The other person in a direct conversation.</summary>
    public string? Other(string me) => Kind == ChatRoomKind.Direct ? Members.FirstOrDefault(m => !SameUser(m, me)) ?? me : null;

    public static bool SameUser(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One message. <see cref="Seq"/> is a database-wide counter (like entry revisions), so
/// "what's new" is one range query and unread counts compare numbers.
/// <see cref="Body"/> is the plain text with links written as <see cref="ChatLink"/> tokens.
/// </summary>
public sealed record ChatMessage(long Seq, Guid Id, Guid RoomId, string Sender, DateTimeOffset SentAt, string Body, bool Pending = false, bool Unreadable = false);

/// <summary>A message as stored: the body is encrypted with the team vault key.</summary>
public sealed record StoredChatMessage(long Seq, Guid Id, Guid RoomId, string Sender, DateTimeOffset SentAt, string Payload);

/// <summary>
/// A link to something in RemoteMan inside a message: <c>[[e:guid|label]]</c>.
/// Kinds: <c>e</c> an entry of any kind (connection, folder, credential, snippet),
/// <c>b</c> an entry's config backups. Unknown kinds are kept and shown as their label,
/// so newer versions can add kinds (a session invite, say) without breaking older ones.
/// </summary>
public sealed record ChatLink(string Kind, string Target, string Label)
{
    public const string Entry = "e";
    public const string Backups = "b";
    /// <summary>A shared-session invite; the target is the invite id.</summary>
    public const string Invite = "invite";

    private static readonly Regex Token = new(@"\[\[([a-z]{1,12}):([^|\]\r\n]{1,200})\|([^\]\r\n]{0,200})\]\]", RegexOptions.Compiled);

    public Guid? EntryId => Guid.TryParse(Target, out var g) ? g : null;

    public string ToToken() => $"[[{Kind}:{Target}|{Clean(Label)}]]";

    public static string Clean(string label) => label.Replace("]]", "] ]").Replace("|", "¦").Replace("\r", " ").Replace("\n", " ");

    /// <summary>Splits a body into text and links, in order.</summary>
    public static IReadOnlyList<ChatPart> Parse(string body)
    {
        var parts = new List<ChatPart>();
        var at = 0;
        foreach (Match m in Token.Matches(body))
        {
            if (m.Index > at) parts.Add(new ChatPart(body[at..m.Index], null));
            parts.Add(new ChatPart(m.Value, new ChatLink(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value)));
            at = m.Index + m.Length;
        }
        if (at < body.Length) parts.Add(new ChatPart(body[at..], null));
        return parts;
    }

    /// <summary>The body as plain text (links become their labels), for previews and toasts.</summary>
    public static string Plain(string body) => string.Concat(Parse(body).Select(p => p.Link?.Label ?? p.Text));
}

public sealed record ChatPart(string Text, ChatLink? Link);
