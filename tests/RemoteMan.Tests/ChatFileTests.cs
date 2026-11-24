using RemoteMan.Core.Chat;
using RemoteMan.Core.Security;
using RemoteMan.Core.Storage;
using RemoteMan.Data;
using Xunit;

namespace RemoteMan.Tests;

public class ChatFileTests
{
    [Fact]
    public void LinkCarriesIdSizeAndKey()
    {
        var (_, key) = ChatFileRef.Encrypt([1, 2, 3]);
        var f = new ChatFileRef(Guid.NewGuid(), 3, key, "party.gif");
        var token = f.ToLink().ToToken();
        var link = Assert.Single(ChatLink.Parse("look " + token), p => p.Link is not null).Link!;
        var back = ChatFileRef.From(link)!;
        Assert.Equal(f.Id, back.Id);
        Assert.Equal(3, back.Size);
        Assert.Equal(key, back.Key);
        Assert.Equal("party.gif", back.Name);
        Assert.True(back.IsGif);
        Assert.True(back.IsImage);
        Assert.Equal("look [GIF: party.gif]", ChatLink.Plain("look " + token));
    }

    [Fact]
    public void EncryptsWithAKeyOfItsOwn()
    {
        var data = System.Text.Encoding.UTF8.GetBytes("secret config");
        var (blob, key) = ChatFileRef.Encrypt(data);
        Assert.DoesNotContain("secret", System.Text.Encoding.UTF8.GetString(blob));
        Assert.Equal(data, new ChatFileRef(Guid.NewGuid(), data.Length, key, "a.txt").Decrypt(blob));
        var (_, other) = ChatFileRef.Encrypt(data);
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => new ChatFileRef(Guid.NewGuid(), data.Length, other, "a.txt").Decrypt(blob));
    }

    [Theory]
    [InlineData(@"C:\Users\x\Desktop\report.pdf", "report.pdf")]
    [InlineData("../../evil.exe", "evil.exe")]
    [InlineData("a|b]].txt", "a_b__.txt")]
    [InlineData("  ", "file")]
    public void NamesAreSafeToSave(string name, string expected) => Assert.Equal(expected, ChatFileRef.SafeName(name));

    [Fact]
    public void ProgramsAreFlagged()
    {
        Assert.True(new ChatFileRef(Guid.Empty, 1, new byte[32], "setup.EXE").IsProgram);
        Assert.False(new ChatFileRef(Guid.Empty, 1, new byte[32], "notes.txt").IsProgram);
    }

    [Fact]
    public void BrokenLinksAreIgnored()
    {
        Assert.Null(ChatFileRef.From(new ChatLink(ChatLink.File, "nope", "x")));
        Assert.Null(ChatFileRef.From(new ChatLink(ChatLink.File, $"{Guid.NewGuid():N};3;AAAA", "x")));
    }

    private static readonly string? Cs = Environment.GetEnvironmentVariable("REMOTEMAN_TEST_PG");
    private static readonly string? Cs2 = Environment.GetEnvironmentVariable("REMOTEMAN_TEST_PG2");

    [Fact]
    public async Task FileReachesTheOtherSideOverPostgres()
    {
        if (Cs is null || Cs2 is null) return;
        var srcA = new PostgresDataSource(Cs, pollMs: 60_000);
        var srcB = new PostgresDataSource(Cs2, pollMs: 60_000);
        await using var storeA = new EntryStore(srcA, "ignored", "chat-pc5");
        await using var storeB = new EntryStore(srcB, "ignored", "chat-pc6");
        await storeA.OpenAsync(); await storeB.OpenAsync();
        var pass = new AesGcmSecretProtector("team");
        await using var a = new ChatHub(srcA, () => storeA.User, pass);
        await using var b = new ChatHub(srcB, () => storeB.User, pass);
        await a.StartAsync(); await b.StartAsync();
        Assert.True(a.SupportsFiles);

        var got = new TaskCompletionSource<ChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.MessageArrived += (_, m) => got.TrySetResult(m);
        var data = new byte[200_000];
        Random.Shared.NextBytes(data);
        await a.SendFileAsync(a.DirectWith(storeB.User), "dump.bin", data);
        var m = await got.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var link = ChatLink.Parse(m.Body).Single(p => p.Link is not null).Link!;
        var file = ChatFileRef.From(link)!;
        Assert.Equal("dump.bin", file.Name);
        Assert.Equal(data, await b.GetFileAsync(file));

        await Assert.ThrowsAsync<InvalidOperationException>(() => a.SendFileAsync(a.DirectWith(storeB.User), "big.iso", new byte[ChatFileRef.MaxBytes + 1]));
    }
}
