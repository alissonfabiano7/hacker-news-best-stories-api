using System.Net.Http.Json;

namespace HackerNews.BestStories.Api.HackerNews;

public interface IHackerNewsClient
{
    Task<int[]?> GetBestStoryIdsAsync(CancellationToken ct);
    Task<HnItem?> GetItemAsync(int id, CancellationToken ct);
}

public sealed class HackerNewsClient(HttpClient http) : IHackerNewsClient
{
    public Task<int[]?> GetBestStoryIdsAsync(CancellationToken ct) =>
        http.GetFromJsonAsync<int[]>("beststories.json", ct);

    public Task<HnItem?> GetItemAsync(int id, CancellationToken ct) =>
        http.GetFromJsonAsync<HnItem>($"item/{id}.json", ct);
}

public sealed record HnItem(
    int Id,
    string? Title,
    string? Url,
    string? By,
    long Time,
    int? Score,
    int? Descendants,
    string? Type,
    bool? Deleted,
    bool? Dead);
