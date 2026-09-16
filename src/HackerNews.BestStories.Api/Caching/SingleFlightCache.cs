using System.Collections.Concurrent;

namespace HackerNews.BestStories.Api.Caching;

public sealed class SingleFlightCache(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> _entries = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _expirations = new();

    public async Task<T> GetOrCreateAsync<T>(string key, TimeSpan ttl, Func<Task<T>> factory, CancellationToken ct = default)
    {
        if (_expirations.TryGetValue(key, out var expiresAt) && expiresAt <= clock.GetUtcNow())
        {
            _expirations.TryRemove(key, out _);
            _entries.TryRemove(key, out _);
        }

        var entry = _entries.GetOrAdd(key, _ => new Lazy<Task<object?>>(async () =>
        {
            var value = await factory().ConfigureAwait(false);
            _expirations[key] = clock.GetUtcNow().Add(ttl);
            return value;
        }));

        try
        {
            var value = await entry.Value.WaitAsync(ct).ConfigureAwait(false);
            return (T)value!;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _entries.TryRemove(new KeyValuePair<string, Lazy<Task<object?>>>(key, entry));
            throw;
        }
    }
}
