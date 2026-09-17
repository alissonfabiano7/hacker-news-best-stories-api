using System.Collections.Concurrent;

namespace HackerNews.BestStories.Api.Caching;

public sealed class SingleFlightCache(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public async Task<T> GetOrCreateAsync<T>(string key, TimeSpan ttl, Func<Task<T>> factory, CancellationToken ct = default)
    {
        if (_entries.TryGetValue(key, out var current) && current.IsExpired(clock.GetUtcNow()))
        {
            Remove(key, current);
        }

        var entry = _entries.GetOrAdd(key, _ => new Entry(async self =>
        {
            var value = await factory().ConfigureAwait(false);
            self.ExpireAt(clock.GetUtcNow().Add(ttl));
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
            Remove(key, entry);
            throw;
        }
    }

    private void Remove(string key, Entry entry) =>
        _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));

    private sealed class Entry
    {
        private readonly Lazy<Task<object?>> _value;
        private long _expiresAtUtcTicks = long.MaxValue;

        public Entry(Func<Entry, Task<object?>> load) =>
            _value = new Lazy<Task<object?>>(() => load(this));

        public Task<object?> Value => _value.Value;

        public bool IsExpired(DateTimeOffset now) =>
            Volatile.Read(ref _expiresAtUtcTicks) <= now.UtcTicks;

        public void ExpireAt(DateTimeOffset instant) =>
            Volatile.Write(ref _expiresAtUtcTicks, instant.UtcTicks);
    }
}
