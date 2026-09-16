using System.Net;
using HackerNews.BestStories.Api.HackerNews;

namespace HackerNews.BestStories.Tests;

public class HackerNewsClientTests
{
    [Fact]
    public async Task Deserializes_a_real_item_payload()
    {
        const string json = """
            {"by":"ismaildonmez","descendants":572,"id":21233041,"score":1757,"time":1570887781,
             "title":"A uBlock Origin update was rejected from the Chrome Web Store","type":"story",
             "url":"https://github.com/uBlockOrigin/uBlock-issues/issues/745"}
            """;
        var client = CreateClient(json);

        var item = await client.GetItemAsync(21233041, CancellationToken.None);

        Assert.NotNull(item);
        Assert.Equal("A uBlock Origin update was rejected from the Chrome Web Store", item.Title);
        Assert.Equal("ismaildonmez", item.By);
        Assert.Equal(1757, item.Score);
        Assert.Equal(572, item.Descendants);
        Assert.Equal(1570887781, item.Time);
        Assert.Null(item.Deleted);
    }

    [Fact]
    public async Task Missing_fields_become_null()
    {
        var client = CreateClient("""{"id":1,"deleted":true,"time":1570887781}""");

        var item = await client.GetItemAsync(1, CancellationToken.None);

        Assert.NotNull(item);
        Assert.True(item.Deleted);
        Assert.Null(item.Title);
        Assert.Null(item.Url);
        Assert.Null(item.Score);
    }

    [Fact]
    public async Task Best_story_ids_request_hits_the_expected_path()
    {
        var handler = new StubHandler("[1,2,3]");
        var client = new HackerNewsClient(new HttpClient(handler) { BaseAddress = new Uri("https://hn.test/v0/") });

        var ids = await client.GetBestStoryIdsAsync(CancellationToken.None);

        Assert.NotNull(ids);
        Assert.Equal([1, 2, 3], ids);
        Assert.Equal("https://hn.test/v0/beststories.json", handler.LastRequestUri);
    }

    private static HackerNewsClient CreateClient(string json) =>
        new(new HttpClient(new StubHandler(json)) { BaseAddress = new Uri("https://hn.test/v0/") });

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        public string? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequestUri = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
