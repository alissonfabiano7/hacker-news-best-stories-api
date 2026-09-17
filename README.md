# Hacker News Best Stories API

ASP.NET Core minimal API on .NET 10 that returns the best stories from Hacker News, sorted by score, without hammering the public Hacker News API.

## Live demo

Deployed on Render (free tier) from this repository's `Dockerfile`:

- Swagger UI: https://hn-best-stories.onrender.com (the root redirects to `/swagger`)
- Example: https://hn-best-stories.onrender.com/stories?n=10

**Heads-up on timing:** the free tier puts the service to sleep after 15 minutes without traffic. The first request after that takes around a minute while the container starts, and it also finds an empty in-memory cache, so it pays the round trips to Hacker News. Open the link, wait for it, and the following requests answer in milliseconds. This is a property of the free hosting plan, not of the API; run it locally to see the real numbers.

## Run

Requires the .NET 10 SDK. The projects target `net10.0`, so an older SDK cannot build them. If you do not have it, use Docker below.

```bash
dotnet run --project src/HackerNews.BestStories.Api
```

Then open http://localhost:5000, which redirects to Swagger UI at `/swagger`, to try the endpoint from the browser, or call it directly:

```
GET http://localhost:5000/stories?n=10
GET http://localhost:5000/health
GET http://localhost:5000/openapi/v1.json
```

Or with Docker, no SDK needed:

```bash
docker build -t hn-best-stories .
docker run --rm -p 5000:8080 hn-best-stories
```

Same URLs as above, Swagger UI included. The container listens on 8080 and runs as a non-root user; if port 5000 is taken on your machine, map another one (`-p 8085:8080`) and use it in the URLs. Settings can be overridden with environment variables, for example `-e HackerNews__MaxConcurrency=5`.

On Windows with Docker installed inside WSL rather than Docker Desktop, run the same two commands from the WSL shell, or prefix them with `wsl --` from PowerShell. Ports published by the container are reachable from Windows on `localhost`.

Run the tests:

```bash
dotnet test
```

## API

`GET /stories?n={count}`

- `n` is required, an integer between 1 and 500 (500 is the maximum size of the Hacker News best stories list). Anything else returns `400` with a `application/problem+json` body.
- `200` returns an array sorted by `score` descending:

```json
[
  {
    "title": "A uBlock Origin update was rejected from the Chrome Web Store",
    "uri": "https://github.com/uBlockOrigin/uBlock-issues/issues/745",
    "postedBy": "ismaildonmez",
    "time": "2019-10-12T13:43:01+00:00",
    "score": 1716,
    "commentCount": 572
  }
]
```

- `503` with `Retry-After: 5` when Hacker News cannot be reached and there is nothing cached to answer from.
- `429` when a single client exceeds 100 requests per second.

## Design

```
Program.cs                    endpoint, DI, resilience, rate limiting, OpenAPI document and Swagger UI
Stories/BestStoriesService    ids -> items -> filter -> sort -> take
Caching/SingleFlightCache     TTL cache where concurrent callers share one upstream call per key
HackerNews/HackerNewsClient   typed HttpClient for the Firebase API
```

How the Hacker News API is protected:

- **Two-level cache.** The list of best story ids is fetched at most once per minute; each story at most once every 5 minutes. A thousand clients asking for `n=500` in that window cost Hacker News one ids call and up to 500 item calls, not a million.
- **Single flight.** When the cache is cold, N concurrent callers for the same key wait on the same in-flight `Task` instead of each starting their own request. Implemented with `ConcurrentDictionary<string, Lazy<Task<T>>>`: the dictionary's value factory can run more than once under contention, but it only allocates a `Lazy`; the HTTP call lives inside `Lazy.Value`, which runs exactly once per instance.
- **Expiration lives inside the entry.** Each entry carries its own expiration instant, set when the upstream call completes and checked lazily on read; there is no timer. An expired entry is removed by key *and* instance, so a slow caller that saw the old entry expired cannot evict the fresh one another caller has just created. Without that, a narrow check-then-act race would let two upstream calls through at expiration time.
- **Failures are not cached.** A failed factory removes its own entry (and only its own, via `TryRemove(KeyValuePair)`), so a transient error is retried by the next caller instead of poisoning the key for the whole TTL.
- **Bounded concurrency.** A `SemaphoreSlim` caps in-flight item requests to Hacker News at 10, regardless of how many clients are waiting. The semaphore wraps only the upstream call, so cache hits never wait for a slot.
- **Resilience.** The standard resilience handler from `Microsoft.Extensions.Http.Resilience` adds a per-attempt timeout (10 s), retry with exponential backoff, a circuit breaker and a total request timeout. When the circuit is open the endpoint fails fast with `503`.
- **Cancellation.** The shared upstream task is not tied to any single caller's token. A client that disconnects stops waiting; the others still get the result, and the cache still gets filled.
- **Rate limiting.** A fixed window of 100 requests per second per client IP protects this API itself. It is a cheap first line of defense, not a substitute for the cache. Behind a reverse proxy, as in the live demo, every client shares the proxy address, so the limit becomes global; trusting `X-Forwarded-For` from a known proxy would fix that.

Everything is configurable in `appsettings.json` under `HackerNews` (`BaseUrl`, `Timeout`, `IdsTtl`, `ItemTtl`, `MaxConcurrency`), or by environment variables such as `HackerNews__MaxConcurrency=5`.

## Assumptions

- "The first n best stories, sorted by score" is read as: take the first `n` ids in the order Hacker News returns them, then sort those by score descending. If the intent is "the n highest scores across the whole list", it is a one-line change (sort before take). The two orderings are usually the same, since Hacker News ranks the best list by score, but they can drift while scores change.
- `n` is capped at 500, the documented maximum of `beststories.json`. In practice the list often holds around 200 ids, so the response can have fewer than `n` entries.
- Items that are missing, `deleted`, `dead` or have no title are skipped instead of failing the whole response. This is another reason the response can be shorter than `n`.
- `commentCount` maps to `descendants` (total comment count, including nested replies), which is what Hacker News itself shows. It defaults to 0 when absent.
- `postedBy` is an empty string in the unlikely case Hacker News omits the author, rather than dropping the story.
- `uri` is `null` for posts without a URL (Ask HN, Show HN text posts), matching what Hacker News returns.
- The cache is in-memory and per process. That is the right trade-off for one instance; see below for several.
- The OpenAPI document (`/openapi/v1.json`, generated by the built-in ASP.NET Core OpenAPI support) and Swagger UI are enabled in every environment, because this is a coding exercise meant to be poked at. In production they would sit behind an environment check or authentication.
- Serialization is camelCase and `time` is an ISO 8601 offset string, as in the example from the brief.

## Tests

xUnit, 16 tests, no network:

- `SingleFlightCacheTests`: 50 concurrent callers produce one factory call; values expire exactly at the TTL (using `FakeTimeProvider`, no sleeps); failures are not cached; keys are independent; a cancelling caller does not fail the others; a slow caller racing on expiration does not evict the refreshed entry (made deterministic with a `TimeProvider` that pauses mid-read).
- `BestStoriesServiceTests`: sort order; take-then-sort semantics; skipping of missing, deleted and dead items; field mapping (`null` url, `null` descendants, unix time); second call served from cache; concurrency bounded by `MaxConcurrency`; upstream failures propagate to the endpoint.
- `HackerNewsClientTests`: deserialization of a real item payload and of a sparse deleted item, request path for the ids list. Uses a stub `HttpMessageHandler`.

## With more time

- **Stale-while-revalidate.** Refresh the id list in the background just before it expires so no caller ever pays the upstream latency, and serve the stale copy when Hacker News is down instead of answering `503`.
- **Distributed cache.** With several instances, each one keeps its own cache, so upstream traffic grows with the instance count. Redis (or another shared store) would bring it back to one call per key.
- **Observability.** OpenTelemetry metrics for cache hit ratio, upstream latency and circuit state; structured logging with correlation ids.
- **Conditional responses.** `ETag` / `If-None-Match` and `Cache-Control` on `/stories` so clients and proxies can skip the body when nothing changed.
- **Integration tests.** `WebApplicationFactory` against a recorded Hacker News payload (WireMock.Net), covering the `400` / `503` paths end to end.
- **Authentication and per-client quotas** if the API were exposed beyond a trusted network.
