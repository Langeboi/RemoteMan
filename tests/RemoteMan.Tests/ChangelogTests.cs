using RemoteMan.Core.Updates;
using Xunit;

namespace RemoteMan.Tests;

public class ChangelogTests
{
    private static ChangelogItem Item(int pr) => new() { Key = $"pr:{pr}", Title = $"Change {pr}" };

    private static ChangelogFile File(params int[] prs) => new() { Items = prs.Select(Item).ToList() };

    [Fact]
    public void Fresh_install_shows_nothing_but_remembers_the_build()
    {
        var (show, save) = Changelog.OnStart(File(3, 2, 1), "0.1.0-ci.3", null, freshInstall: true);
        Assert.Empty(show);
        Assert.Equal("0.1.0-ci.3", save.Version);
        Assert.Equal(["pr:3", "pr:2", "pr:1"], save.Seen);
    }

    [Fact]
    public void Update_lists_every_change_the_previous_build_lacked_including_skipped_builds()
    {
        var before = new ChangelogState { Version = "0.1.0-ci.1", Seen = ["pr:1"] };
        var (show, _) = Changelog.OnStart(File(4, 3, 2, 1), "0.1.0-ci.4", before, freshInstall: false);
        Assert.Equal(["pr:4", "pr:3", "pr:2"], show.Select(i => i.Key));
    }

    [Fact]
    public void Later_starts_of_the_same_version_show_nothing()
    {
        var (_, save) = Changelog.OnStart(File(2, 1), "0.1.0-ci.2", new ChangelogState { Version = "0.1.0-ci.1", Seen = ["pr:1"] }, false);
        var (show, _) = Changelog.OnStart(File(2, 1), "0.1.0-ci.2", save, false);
        Assert.Empty(show);
    }

    [Fact]
    public void Upgrade_from_a_build_without_the_tab_shows_this_builds_change()
    {
        var (show, _) = Changelog.OnStart(File(5, 4), "0.1.0-ci.5", null, freshInstall: false);
        Assert.Equal(["pr:5"], show.Select(i => i.Key));
    }

    [Fact]
    public void Build_without_a_list_keeps_the_old_state()
    {
        var before = new ChangelogState { Version = "0.1.0-ci.1", Seen = ["pr:1"] };
        var (show, save) = Changelog.OnStart(new ChangelogFile(), "dev", before, false);
        Assert.Empty(show);
        Assert.Same(before, save);
    }

    [Fact]
    public void Parses_the_file_ci_writes()
    {
        var f = ChangelogFile.Parse("""
            {"version":"0.1.0-ci.9","items":[{"key":"pr:46","title":"Open maximized","notes":["Fills the screen"],"date":"2026-11-23T03:46:35-08:00","pr":46,"version":"0.1.0-ci.9"}]}
            """);
        var i = Assert.Single(f.Items);
        Assert.Equal("Open maximized", i.Title);
        Assert.Equal(["Fills the screen"], i.Notes);
        Assert.Equal(46, i.Pr);
        Assert.Empty(ChangelogFile.Parse("not json").Items);
    }
}
