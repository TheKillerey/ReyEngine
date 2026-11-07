using System.Net;
using System.Text;
using ReyEngine.App.Services;
using ReyEngine.Formats.Characters;

namespace ReyEngine.Formats.Tests;

/// <summary>M878: the champion JSON's ability video fields and the on-demand cache. No network: a fake handler answers.</summary>
public sealed class AbilityVideoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rey_abv_" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class Fake(HttpStatusCode code, byte[] body, bool hang = false) : HttpMessageHandler
    {
        public int Calls;
        public string? LastUrl;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Calls++; LastUrl = r.RequestUri!.ToString();
            if (hang) await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(code) { Content = new ByteArrayContent(body) };
        }
    }

    [Fact]
    public void TheChampionJsonVideoFieldsAreRead()
    {
        const string Json = """
        {"id":266,"alias":"Aatrox","name":"Aatrox","title":"t","passive":{"name":"P","description":"d","abilityVideoPath":"champion-abilities/0266/ability_0266_P1.webm","abilityVideoImagePath":"champion-abilities/0266/ability_0266_P1.jpg"},
         "spells":[{"spellKey":"q","name":"Q","description":"d","abilityVideoPath":"champion-abilities/0266/ability_0266_Q1.webm","abilityVideoImagePath":"champion-abilities/0266/ability_0266_Q1.jpg"},
                   {"spellKey":"w","name":"W","description":"d"}]}
        """;
        using var data = ClientChampionData.Unavailable();
        var d = data.Parse(266, Encoding.UTF8.GetBytes(Json));
        Assert.Equal("champion-abilities/0266/ability_0266_Q1.webm", d.Ability("Q")!.VideoPath);
        Assert.Equal("champion-abilities/0266/ability_0266_Q1.jpg", d.Ability("Q")!.VideoImagePath);
        Assert.Equal("champion-abilities/0266/ability_0266_P1.webm", d.Passive!.VideoPath);
        Assert.Null(d.Ability("W")!.VideoPath);
    }

    [Fact]
    public void TheCachePathIsPerChampionAndRejectsOddPaths()
    {
        var cache = new AbilityVideoCache(_dir, new HttpClient(new Fake(HttpStatusCode.OK, [1])));
        Assert.Equal(Path.Combine(_dir, "0266", "ability_0266_Q1.webm"), cache.CachePath("champion-abilities/0266/ability_0266_Q1.webm"));
        Assert.Null(cache.CachePath("champion-abilities/0266/../../x.webm"));
        Assert.Null(cache.CachePath("other/0266/a.webm"));
        Assert.Null(cache.CachePath(null));
        Assert.Equal("https://lol.dyn.riotcdn.net/x/videos/champion-abilities/0266/ability_0266_Q1.jpg", AbilityVideoCache.UrlOf("champion-abilities/0266/ability_0266_Q1.jpg"));
    }

    [Fact]
    public async Task ADownloadIsCachedAndNotRepeated()
    {
        var fake = new Fake(HttpStatusCode.OK, [1, 2, 3]);
        var cache = new AbilityVideoCache(_dir, new HttpClient(fake));
        var r = await cache.EnsureAsync("champion-abilities/0266/ability_0266_Q1.webm");
        Assert.Equal(3, new FileInfo(r.Path!).Length);
        await cache.EnsureAsync("champion-abilities/0266/ability_0266_Q1.webm");
        Assert.Equal(1, fake.Calls);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(r.Path!)!, "*.part"));
    }

    [Fact]
    public async Task AFailureLeavesNoFileAndDoesNotThrow()
    {
        var cache = new AbilityVideoCache(_dir, new HttpClient(new Fake(HttpStatusCode.NotFound, [])));
        var r = await cache.EnsureAsync("champion-abilities/0266/ability_0266_Q1.webm");
        Assert.Null(r.Path);
        Assert.Contains("404", r.Message);
        Assert.Null(cache.Cached("champion-abilities/0266/ability_0266_Q1.webm"));

        var slow = new AbilityVideoCache(_dir, new HttpClient(new Fake(HttpStatusCode.OK, [1], hang: true)), TimeSpan.FromMilliseconds(100));
        var t = await slow.EnsureAsync("champion-abilities/0266/ability_0266_W1.webm");
        Assert.Null(t.Path);
        Assert.Contains("timed out", t.Message);
    }
}
