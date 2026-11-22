using RemoteMan.Core.Chat;
using RemoteMan.Core.Security;
using RemoteMan.Core.Storage;
using RemoteMan.Data;
using Xunit;

namespace RemoteMan.Tests;

public class ChatTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"remoteman-chat-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _db, _db + "-wal", _db + "-shm" }) if (File.Exists(f)) File.Delete(f);
    }

    private sealed class MemoryOutbox : IChatOutbox
    {
        public List<StoredChatMessage> Items = new();
        public IReadOnlyList<StoredChatMessage> Load() => Items.ToList();
        public void Save(IReadOnlyList<StoredChatMessage> pending) => Items = pending.ToList();
    }

    private async Task<(SqliteDataSource Src, ChatHub Hub)> Open(string user, string pass = "team pass", IChatOutbox? outbox = null)
    {
        var src = new SqliteDataSource(_db);
        await src.InitializeAsync();
        var hub = new ChatHub(src, () => user, new AesGcmSecretProtector(pass), outbox);
        await hub.StartAsync();
        return (src, hub);
    }

    [Fact]
    public void LinksRoundTripAndUnknownKindsSurvive()
    {
        var id = Guid.NewGuid();
        var body = $"check {new ChatLink(ChatLink.Entry, id.ToString(), "fw01 | core").ToToken()} and [[invite:abc|Join DC01]] now";
        var parts = ChatLink.Parse(body);
        Assert.Equal(5, parts.Count);
        Assert.Equal(id, parts[1].Link!.EntryId);
        Assert.Equal("fw01 ¦ core", parts[1].Link!.Label);
        Assert.Equal("invite", parts[3].Link!.Kind);
        Assert.Equal("check fw01 ¦ core and Join DC01 now", ChatLink.Plain(body));
    }

    [Fact]
    public void DirectRoomIdIsTheSameFromBothSides()
    {
        Assert.Equal(ChatRoom.DirectId("LAB\\Lange", "jonas"), ChatRoom.DirectId("jonas", "lab\\lange"));
        Assert.NotEqual(ChatRoom.DirectId("a", "b"), ChatRoom.DirectId("a", "c"));
    }

    [Fact]
    public async Task MessagesAreEncryptedCountedAndMarkedRead()
    {
        var (srcA, a) = await Open("lange");
        var (srcB, b) = await Open("jonas");
        await using var _1 = srcA; await using var _2 = srcB; await using var _3 = a; await using var _4 = b;

        await a.SendAsync(ChatRoom.Everyone, "fw01 is down");
        await b.PullAsync();
        Assert.Equal("fw01 is down", Assert.Single(b.Messages(ChatRoom.EveryoneId)).Body);
        Assert.Equal(1, b.Unread(ChatRoom.EveryoneId));
        Assert.Equal(0, a.Unread(ChatRoom.EveryoneId));
        Assert.Contains("lange", b.Users);

        // Nothing readable in the database.
        var stored = await srcA.GetChatMessagesAsync(0);
        Assert.DoesNotContain("fw01", Assert.Single(stored).Payload);

        await b.MarkReadAsync(ChatRoom.EveryoneId);
        Assert.Equal(0, b.Unread(ChatRoom.EveryoneId));
        // The read marker is stored, so a restart (or another PC) agrees.
        var (srcB2, b2) = await Open("jonas");
        await using var _5 = srcB2; await using var _6 = b2;
        Assert.Equal(0, b2.Unread(ChatRoom.EveryoneId));
    }

    [Fact]
    public async Task DirectMessagesOnlyReachTheTwoPeople()
    {
        var (s1, a) = await Open("lange");
        var (s2, b) = await Open("jonas");
        var (s3, c) = await Open("anna");
        await using var _1 = s1; await using var _2 = s2; await using var _3 = s3;

        await a.SendAsync(a.DirectWith("jonas"), "psst");
        await b.PullAsync(); await c.PullAsync();
        var dm = b.Rooms.Single(r => r.Kind == ChatRoomKind.Direct);
        Assert.Equal("lange", dm.Other("jonas"));
        Assert.Equal(1, b.Unread(dm.Id));
        Assert.DoesNotContain(c.Rooms, r => r.Kind == ChatRoomKind.Direct);
        Assert.Equal(0, c.TotalUnread);

        // Jonas answers in the same room.
        await b.SendAsync(b.DirectWith("lange"), "yes?");
        await a.PullAsync();
        Assert.Equal(["psst", "yes?"], a.Messages(dm.Id).Select(m => m.Body));
    }

    [Fact]
    public async Task GroupsHoldTheirMembers()
    {
        var (s1, a) = await Open("lange");
        var (s2, b) = await Open("jonas");
        var (s3, c) = await Open("anna");
        await using var _1 = s1; await using var _2 = s2; await using var _3 = s3;
        var g = await a.CreateGroupAsync("School lab", ["anna"]);
        await a.SendAsync(g, "week 47 ready");
        await b.PullAsync(); await c.PullAsync();
        Assert.Contains(c.Rooms, r => r.Id == g.Id);
        Assert.DoesNotContain(b.Rooms, r => r.Id == g.Id);
        Assert.Equal(1, c.Unread(g.Id));
        await c.LeaveGroupAsync(g.Id);
        await c.PullAsync();
        Assert.DoesNotContain(c.Rooms, r => r.Id == g.Id);
    }

    [Fact]
    public async Task WrongPassphraseShowsANoteInsteadOfFailing()
    {
        var (s1, a) = await Open("lange");
        var (s2, b) = await Open("jonas", pass: "other");
        await using var _1 = s1; await using var _2 = s2;
        await a.SendAsync(ChatRoom.Everyone, "secret");
        await b.PullAsync();
        var m = Assert.Single(b.Messages(ChatRoom.EveryoneId));
        Assert.True(m.Unreadable);
        Assert.DoesNotContain("secret", m.Body);
    }

    [Fact]
    public async Task OutboxKeepsMessagesUntilTheyAreSent()
    {
        var outbox = new MemoryOutbox();
        var (s1, a) = await Open("lange", outbox: outbox);
        await using var _1 = s1;
        // Simulate offline: the pending message is visible and saved, then sent on the next flush, once.
        var stored = new StoredChatMessage(0, Guid.NewGuid(), ChatRoom.EveryoneId, "lange", DateTimeOffset.UtcNow, "p:queued");
        outbox.Items.Add(stored);
        var (s2, a2) = await Open("lange", outbox: outbox);
        await using var _2 = s2;
        Assert.Empty(outbox.Items);
        Assert.Single(a2.Messages(ChatRoom.EveryoneId), m => m.Body == "queued" && !m.Pending);
        // Sending the same message id again (a retry after a lost reply) doesn't duplicate it.
        await s2.PostChatMessageAsync(stored);
        Assert.Single(await s2.GetChatMessagesAsync(0));
    }

    [Fact]
    public async Task RetentionDeletesOldMessages()
    {
        var src = new SqliteDataSource(_db);
        await using var _ = src;
        await src.InitializeAsync();
        await src.PostChatMessageAsync(new StoredChatMessage(0, Guid.NewGuid(), ChatRoom.EveryoneId, "x", DateTimeOffset.UtcNow.AddDays(-400), "p:old"));
        await src.PostChatMessageAsync(new StoredChatMessage(0, Guid.NewGuid(), ChatRoom.EveryoneId, "x", DateTimeOffset.UtcNow, "p:new"));
        var hub = new ChatHub(src, () => "lange", null) { Retention = TimeSpan.FromDays(365) };
        await hub.StartAsync();
        Assert.Equal("new", Assert.Single(hub.Messages(ChatRoom.EveryoneId)).Body);
    }
}

/// <summary>Chat over a real PostgreSQL (REMOTEMAN_TEST_PG and REMOTEMAN_TEST_PG2, two logins on one database); skipped otherwise.</summary>
public class PostgresChatTests
{
    private static readonly string? Cs = Environment.GetEnvironmentVariable("REMOTEMAN_TEST_PG");
    private static readonly string? Cs2 = Environment.GetEnvironmentVariable("REMOTEMAN_TEST_PG2");

    [Fact]
    public async Task MessagesArriveByNotifyAsTheDatabaseLogin()
    {
        if (Cs is null || Cs2 is null) return;
        var srcA = new PostgresDataSource(Cs, pollMs: 60_000);   // the stores dispose their sources
        var srcB = new PostgresDataSource(Cs2, pollMs: 60_000);
        await using var storeA = new EntryStore(srcA, "ignored", "chat-pc1");
        await using var storeB = new EntryStore(srcB, "ignored", "chat-pc2");
        await storeA.OpenAsync(); await storeB.OpenAsync();
        var pass = new AesGcmSecretProtector("team");
        await using var a = new ChatHub(srcA, () => storeA.User, pass);
        await using var b = new ChatHub(srcB, () => storeB.User, pass);
        await a.StartAsync(); await b.StartAsync();

        var got = new TaskCompletionSource<ChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.MessageArrived += (_, m) => got.TrySetResult(m);
        var text = "hi " + Guid.NewGuid().ToString("N")[..6];
        await a.SendAsync(a.DirectWith(storeB.User), text);
        // Polling is 60 s, so this proves NOTIFY delivered it.
        var m = await got.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(text, m.Body);
        Assert.Equal(storeA.User, m.Sender);
        Assert.Equal(ChatRoom.DirectId(storeA.User, storeB.User), m.RoomId);
        Assert.Contains(storeA.User, b.Users);
    }
}
