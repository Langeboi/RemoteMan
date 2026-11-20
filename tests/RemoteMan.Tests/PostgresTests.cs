using RemoteMan.Core.Model;
using RemoteMan.Core.Storage;
using RemoteMan.Data;
using Xunit;

namespace RemoteMan.Tests;

/// <summary>
/// Runs against a real PostgreSQL when REMOTEMAN_TEST_PG holds a connection string
/// (e.g. "Host=localhost;Database=rm;Username=lange;Password=pw"); otherwise does nothing.
/// </summary>
public class PostgresTests
{
    private static readonly string? Cs = Environment.GetEnvironmentVariable("REMOTEMAN_TEST_PG");

    [Fact]
    public async Task UsersAreDatabaseLoginsAndChangesArrivePushed()
    {
        if (Cs is null) return;
        await using var a = new EntryStore(new PostgresDataSource(Cs, pollMs: 60_000), "ignored-a", "pc1");
        await a.OpenAsync();
        await using var b = new EntryStore(new PostgresDataSource(Cs, pollMs: 60_000), "ignored-b", "pc2");
        await b.OpenAsync();

        var expectedUser = new Npgsql.NpgsqlConnectionStringBuilder(Cs).Username;
        Assert.Equal(expectedUser, a.User);

        var got = new TaskCompletionSource<Entry>(TaskCreationOptions.RunContinuationsAsynchronously);
        var name = "pg-" + Guid.NewGuid().ToString("N")[..8];
        b.EntriesChanged += (_, list) => { foreach (var e in list) if (e.Name == name) got.TrySetResult(e); };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var saved = await a.SaveAsync(new Entry { Kind = EntryKind.Ssh, Name = name, Endpoints = { new("10.0.0.1", 22) } });
        // Polling is set to 60 s, so arriving within 2 s proves LISTEN/NOTIFY delivered it.
        var seen = await got.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(expectedUser, seen.CreatedBy);
        Assert.Equal(expectedUser, seen.UpdatedBy);
        Assert.True(sw.ElapsedMilliseconds < 2000);

        await a.SetMyPresenceAsync(saved.Id, PresenceState.Editing);
        await Task.Delay(300);
        await b.SetMyPresenceAsync(null); // refreshes b's presence view
        Assert.Contains(b.Presence, p => p.EntryId == saved.Id && p.State == PresenceState.Editing);
        await a.DeleteAsync(saved.Id);
    }
}
