using RemoteMan.Core.Model;
using RemoteMan.Core.Networking;
using Xunit;

namespace RemoteMan.Tests;

public class FallbackHostTests
{
    [Theory]
    [InlineData("10.0.0.6", 0, "10.0.0.6", 0)]
    [InlineData(" 10.0.0.6 ", 443, "10.0.0.6", 443)]
    [InlineData("10.0.0.6:8443", 443, "10.0.0.6", 8443)]
    [InlineData("https://10.0.0.6", 0, "10.0.0.6", 0)]
    [InlineData("https://10.0.0.6:8443/login", 0, "10.0.0.6", 8443)]
    [InlineData("10.0.0.6/login", 0, "10.0.0.6", 0)]
    [InlineData("[fe80::1]:8443", 0, "fe80::1", 8443)]
    [InlineData("fe80::1", 443, "fe80::1", 443)]
    [InlineData("srv-02.lab", 0, "srv-02.lab", 0)]
    public void CleansWhatPeopleTypeIntoTheHostBox(string typed, int port, string host, int expectedPort)
    {
        Assert.Equal(new HostEndpoint(host, expectedPort), EndpointResolver.Clean(new HostEndpoint(typed, port)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("10.0.0.6 10.0.0.7")]
    [InlineData("not a host")]
    [InlineData("[fe80::1")]
    [InlineData("host:notaport")]
    public void RejectsJunkWithoutThrowing(string typed)
    {
        var r = EndpointResolver.Clean(new HostEndpoint(typed));
        if (typed == "[fe80::1") Assert.Equal("fe80::1", r?.Host); // a missing bracket is forgiven
        else Assert.Null(r);
    }

    [Theory]
    [InlineData("10.0.0.6:8443")]
    [InlineData("https://10.0.0.6")]
    [InlineData("not a host")]
    [InlineData("fe80::1")]
    [InlineData("")]
    public void SwappingTheUrlHostNeverThrows(string host)
    {
        var url = WebEndpoints.UrlFor("https://nas.lab:5001/", new HostEndpoint(host, 443));
        if (url is not null) Assert.True(Uri.IsWellFormedUriString(url, UriKind.Absolute), url);
    }

    [Fact]
    public void WebEntryTriesTheUrlThenEachFallback()
    {
        var e = new Entry
        {
            Kind = EntryKind.Web, Web = new WebSettings { Url = "https://nas.lab:5001/ui" },
            Endpoints = { new("nas.lab", 5001), new("10.0.0.6:5002"), new("10.0.0.7"), new("bad host"), new("") },
        };
        var t = WebEndpoints.For(e);
        Assert.Equal(new HostEndpoint("nas.lab", 5001), t.Main);
        Assert.Equal(new[] { new HostEndpoint("10.0.0.6", 5002), new HostEndpoint("10.0.0.7", 5001) }, t.Fallbacks);
        Assert.Equal(1, t.Invalid);
        Assert.Equal("https://10.0.0.6:5002/ui", WebEndpoints.UrlFor(e.Web.Url, t.Fallbacks[0]));
        Assert.Equal("https://10.0.0.7:5001/ui", WebEndpoints.UrlFor(e.Web.Url, t.Fallbacks[1]));
    }

    [Fact]
    public void WebUrlDefaultPortIsDroppedFromTheSwappedUrl()
    {
        var e = new Entry { Kind = EntryKind.Web, Web = new WebSettings { Url = "pve.lab" }, Endpoints = { new("10.0.0.9") } };
        var t = WebEndpoints.For(e);
        Assert.Equal(new HostEndpoint("pve.lab", 443), t.Main);
        Assert.Equal("https://10.0.0.9/", WebEndpoints.UrlFor(e.Web.Url, t.Fallbacks[0]));
    }

    [Fact]
    public async Task ResolverSkipsAnInvalidHostAndUsesTheNext()
    {
        var tried = new List<(string, int)>();
        var resolver = new EndpointResolver((host, port, ct) =>
        {
            tried.Add((host, port));
            return host == "10.0.0.5" ? Task.FromException(new System.Net.Sockets.SocketException(10061)) : Task.CompletedTask;
        });
        var entry = new Entry { Kind = EntryKind.Rdp, Endpoints = { new("10.0.0.5"), new("not a host"), new("10.0.0.6:3390") } };
        var r = await resolver.ResolveAsync(entry);
        Assert.Equal(new HostEndpoint("10.0.0.6", 3390), r.Endpoint);
        Assert.True(r.UsedFallback);
        Assert.Equal(new[] { ("10.0.0.5", 3389), ("10.0.0.6", 3390) }, tried);
        Assert.Equal(3, r.Attempts.Count);
    }
}
