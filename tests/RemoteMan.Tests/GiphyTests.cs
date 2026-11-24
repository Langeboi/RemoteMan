using RemoteMan.Core.Chat;
using Xunit;

namespace RemoteMan.Tests;

public class GiphyTests
{
    private const string Sample = """
    {"data":[
      {"id":"abc","title":"Happy Dance GIF by Cats",
       "images":{
         "fixed_width_small":{"url":"https://media1.giphy.com/media/abc/100w.gif","size":"90000"},
         "downsized":{"url":"https://media1.giphy.com/media/abc/giphy-downsized.gif","size":"1500000"},
         "original":{"url":"https://media1.giphy.com/media/abc/giphy.gif","size":"30000000"}}},
      {"id":"big","title":"",
       "images":{
         "fixed_width_small":{"url":"https://media2.giphy.com/media/big/100w.gif","size":"1"},
         "downsized":{"url":"https://media2.giphy.com/media/big/d.gif","size":"20000000"},
         "fixed_height":{"url":"https://media2.giphy.com/media/big/200.gif","size":"800000"}}},
      {"id":"nothing","title":"x","images":{}}
    ],"pagination":{"total_count":2}}
    """;

    [Fact]
    public void PicksAPreviewAndASendableSize()
    {
        var gifs = GiphyClient.Parse(Sample);
        Assert.Equal(2, gifs.Count);
        Assert.Equal("https://media1.giphy.com/media/abc/100w.gif", gifs[0].PreviewUrl);
        Assert.Equal("https://media1.giphy.com/media/abc/giphy-downsized.gif", gifs[0].SendUrl);
        // downsized is over the chat limit there, so the smaller fixed_height is sent
        Assert.Equal("https://media2.giphy.com/media/big/200.gif", gifs[1].SendUrl);
    }

    [Fact]
    public void FileNamesComeFromTheTitle()
    {
        var gifs = GiphyClient.Parse(Sample);
        Assert.Equal("happy-dance-gif-by-cats.gif", gifs[0].FileName);
        Assert.Equal("giphy.gif", gifs[1].FileName);
        Assert.Equal("party.gif", new GiphyGif("1", "Party GIF", "", "", 0).FileName);
    }

    [Fact]
    public async Task OnlyDownloadsFromGiphy()
    {
        var c = new GiphyClient("k");
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.DownloadAsync("https://evil.example/giphy.com.gif"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.DownloadAsync("http://media.giphy.com/x.gif"));
    }
}
