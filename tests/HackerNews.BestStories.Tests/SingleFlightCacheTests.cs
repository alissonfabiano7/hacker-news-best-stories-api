using HackerNews.BestStories.Api.Caching;
using Microsoft.Extensions.Time.Testing;

namespace HackerNews.BestStories.Tests;

public class SingleFlightCacheTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Concurrent_callers_on_the_same_key_share_one_factory_call()
    {
        var cache = new SingleFlightCache(new FakeTimeProvider());
        var calls = 0;
        var gate = new TaskCompletionSource();

        async Task<string> Factory()
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return "value";
        }

        var callers = Enumerable.Range(0, 50).Select(_ => cache.GetOrCreateAsync("k", Ttl, Factory)).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(callers);

        Assert.Equal(1, calls);
        Assert.All(results, r => Assert.Equal("value", r));
    }

    [Fact]
    public async Task Value_is_reused_until_the_ttl_elapses_and_refreshed_after()
    {
        var clock = new FakeTimeProvider();
        var cache = new SingleFlightCache(clock);
        var calls = 0;
        Task<int> Factory() => Task.FromResult(Interlocked.Increment(ref calls));

        Assert.Equal(1, await cache.GetOrCreateAsync("k", Ttl, Factory));
        clock.Advance(Ttl - TimeSpan.FromSeconds(1));
        Assert.Equal(1, await cache.GetOrCreateAsync("k", Ttl, Factory));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, await cache.GetOrCreateAsync("k", Ttl, Factory));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Failures_are_not_cached()
    {
        var cache = new SingleFlightCache(new FakeTimeProvider());
        var calls = 0;

        Task<string> Factory()
        {
            calls++;
            return calls == 1
                ? Task.FromException<string>(new HttpRequestException("upstream down"))
                : Task.FromResult("recovered");
        }

        await Assert.ThrowsAsync<HttpRequestException>(() => cache.GetOrCreateAsync("k", Ttl, Factory));
        Assert.Equal("recovered", await cache.GetOrCreateAsync("k", Ttl, Factory));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Keys_are_independent()
    {
        var cache = new SingleFlightCache(new FakeTimeProvider());

        var a = await cache.GetOrCreateAsync("a", Ttl, () => Task.FromResult("A"));
        var b = await cache.GetOrCreateAsync("b", Ttl, () => Task.FromResult("B"));

        Assert.Equal("A", a);
        Assert.Equal("B", b);
    }

    [Fact]
    public async Task A_caller_that_cancels_does_not_fail_the_others()
    {
        var cache = new SingleFlightCache(new FakeTimeProvider());
        var gate = new TaskCompletionSource();
        async Task<string> Factory() { await gate.Task; return "value"; }

        using var cts = new CancellationTokenSource();
        var cancelled = cache.GetOrCreateAsync("k", Ttl, Factory, cts.Token);
        var patient = cache.GetOrCreateAsync("k", Ttl, Factory);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        gate.SetResult();
        Assert.Equal("value", await patient);
    }
}
