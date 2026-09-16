using HackerNews.BestStories.Api.Caching;
using HackerNews.BestStories.Api.HackerNews;
using Microsoft.Extensions.Options;

namespace HackerNews.BestStories.Api.Stories;

public sealed class BestStoriesService
{
    private const string IdsCacheKey = "best-ids";

    private readonly IHackerNewsClient _client;
    private readonly SingleFlightCache _cache;
    private readonly HackerNewsOptions _options;
    private readonly SemaphoreSlim _upstreamSlots;

    public BestStoriesService(IHackerNewsClient client, SingleFlightCache cache, IOptions<HackerNewsOptions> options)
    {
        _client = client;
        _cache = cache;
        _options = options.Value;
        _upstreamSlots = new SemaphoreSlim(_options.MaxConcurrency, _options.MaxConcurrency);
    }

    public async Task<IReadOnlyList<StoryResponse>> GetAsync(int n, CancellationToken ct)
    {
        var ids = await _cache.GetOrCreateAsync(
            IdsCacheKey,
            _options.IdsTtl,
            () => _client.GetBestStoryIdsAsync(CancellationToken.None),
            ct) ?? [];

        var itemTasks = ids.Take(n).Select(id => GetItemAsync(id, ct));
        var items = await Task.WhenAll(itemTasks);

        return items
            .Where(IsPublishedStory)
            .Select(ToResponse)
            .OrderByDescending(s => s.Score)
            .ToList();
    }

    private Task<HnItem?> GetItemAsync(int id, CancellationToken ct) =>
        _cache.GetOrCreateAsync($"item:{id}", _options.ItemTtl, async () =>
        {
            await _upstreamSlots.WaitAsync().ConfigureAwait(false);
            try
            {
                return await _client.GetItemAsync(id, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _upstreamSlots.Release();
            }
        }, ct);

    private static bool IsPublishedStory(HnItem? item) =>
        item is { Deleted: not true, Dead: not true, Title: not null };

    private static StoryResponse ToResponse(HnItem? item) => new(
        Title: item!.Title!,
        Uri: item.Url,
        PostedBy: item.By ?? string.Empty,
        Time: DateTimeOffset.FromUnixTimeSeconds(item.Time),
        Score: item.Score ?? 0,
        CommentCount: item.Descendants ?? 0);
}
