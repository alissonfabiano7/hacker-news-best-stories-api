using HackerNews.BestStories.Api.Caching;
using HackerNews.BestStories.Api.HackerNews;
using HackerNews.BestStories.Api.Stories;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace HackerNews.BestStories.Tests;

public class BestStoriesServiceTests
{
    private static HnItem Story(int id, int score, string? url = "https://example.com", int? descendants = 3,
        bool? deleted = null, bool? dead = null, string? title = null) =>
        new(id, title ?? $"Story {id}", url, $"user{id}", 1570887781, score, descendants, "story", deleted, dead);

    private static BestStoriesService CreateService(FakeHackerNewsClient client, int maxConcurrency = 10) =>
        new(client, new SingleFlightCache(new FakeTimeProvider()),
            Options.Create(new HackerNewsOptions { MaxConcurrency = maxConcurrency }));

    [Fact]
    public async Task Returns_stories_sorted_by_score_descending()
    {
        var client = new FakeHackerNewsClient([1, 2, 3], Story(1, 10), Story(2, 30), Story(3, 20));

        var result = await CreateService(client).GetAsync(3, CancellationToken.None);

        Assert.Equal([30, 20, 10], result.Select(s => s.Score));
    }

    [Fact]
    public async Task Takes_the_first_n_ids_in_hacker_news_order_then_sorts()
    {
        var client = new FakeHackerNewsClient([1, 2, 3], Story(1, 10), Story(2, 30), Story(3, 99));

        var result = await CreateService(client).GetAsync(2, CancellationToken.None);

        Assert.Equal(["Story 2", "Story 1"], result.Select(s => s.Title));
    }

    [Fact]
    public async Task Skips_missing_deleted_and_dead_items()
    {
        var client = new FakeHackerNewsClient([1, 2, 3, 4],
            Story(1, 10), Story(2, 20, deleted: true), Story(3, 30, dead: true));

        var result = await CreateService(client).GetAsync(4, CancellationToken.None);

        var story = Assert.Single(result);
        Assert.Equal("Story 1", story.Title);
    }

    [Fact]
    public async Task Maps_item_fields_to_the_response_contract()
    {
        var client = new FakeHackerNewsClient([1], Story(1, 42, url: null, descendants: null));

        var story = Assert.Single(await CreateService(client).GetAsync(1, CancellationToken.None));

        Assert.Equal("Story 1", story.Title);
        Assert.Null(story.Uri);
        Assert.Equal("user1", story.PostedBy);
        Assert.Equal(DateTimeOffset.Parse("2019-10-12T13:43:01+00:00"), story.Time);
        Assert.Equal(42, story.Score);
        Assert.Equal(0, story.CommentCount);
    }

    [Fact]
    public async Task Second_call_is_served_from_cache()
    {
        var client = new FakeHackerNewsClient([1, 2], Story(1, 10), Story(2, 20));
        var service = CreateService(client);

        await service.GetAsync(2, CancellationToken.None);
        await service.GetAsync(2, CancellationToken.None);

        Assert.Equal(1, client.IdsCalls);
        Assert.Equal(2, client.ItemCalls);
    }

    [Fact]
    public async Task Upstream_item_requests_are_bounded_by_max_concurrency()
    {
        var ids = Enumerable.Range(1, 20).ToArray();
        var client = new FakeHackerNewsClient(ids, ids.Select(id => Story(id, id)).ToArray())
        {
            ItemDelay = TimeSpan.FromMilliseconds(20),
        };

        var result = await CreateService(client, maxConcurrency: 3).GetAsync(20, CancellationToken.None);

        Assert.Equal(20, result.Count);
        Assert.InRange(client.MaxObservedConcurrency, 1, 3);
    }

    [Fact]
    public async Task Upstream_failure_propagates_so_the_endpoint_can_answer_503()
    {
        var client = new FakeHackerNewsClient([1]) { Fail = true };

        await Assert.ThrowsAsync<HttpRequestException>(() => CreateService(client).GetAsync(1, CancellationToken.None));
    }

    private sealed class FakeHackerNewsClient(int[] ids, params HnItem[] items) : IHackerNewsClient
    {
        private readonly Dictionary<int, HnItem> _items = items.ToDictionary(i => i.Id);
        private int _idsCalls, _itemCalls, _inFlight, _maxInFlight;

        public TimeSpan ItemDelay { get; init; }
        public bool Fail { get; init; }
        public int IdsCalls => _idsCalls;
        public int ItemCalls => _itemCalls;
        public int MaxObservedConcurrency => _maxInFlight;

        public Task<int[]?> GetBestStoryIdsAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _idsCalls);
            if (Fail) throw new HttpRequestException("upstream down");
            return Task.FromResult<int[]?>(ids);
        }

        public async Task<HnItem?> GetItemAsync(int id, CancellationToken ct)
        {
            Interlocked.Increment(ref _itemCalls);
            var now = Interlocked.Increment(ref _inFlight);
            InterlockedMax(ref _maxInFlight, now);
            try
            {
                if (ItemDelay > TimeSpan.Zero) await Task.Delay(ItemDelay, ct);
                return _items.GetValueOrDefault(id);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = target) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
        }
    }
}
