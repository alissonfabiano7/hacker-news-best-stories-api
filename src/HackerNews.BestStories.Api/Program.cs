using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using HackerNews.BestStories.Api.Caching;
using HackerNews.BestStories.Api.HackerNews;
using HackerNews.BestStories.Api.Stories;
using Polly.CircuitBreaker;
using Polly.Timeout;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<HackerNewsOptions>(builder.Configuration.GetSection(HackerNewsOptions.SectionName));

var hackerNews = builder.Configuration.GetSection(HackerNewsOptions.SectionName).Get<HackerNewsOptions>() ?? new();
builder.Services.AddHttpClient<IHackerNewsClient, HackerNewsClient>(http =>
{
    http.BaseAddress = new Uri(hackerNews.BaseUrl);
})
.AddStandardResilienceHandler(options =>
{
    options.AttemptTimeout.Timeout = hackerNews.Timeout;
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SingleFlightCache>();
builder.Services.AddSingleton<BestStoriesService>();
builder.Services.AddHealthChecks();
builder.Services.AddProblemDetails();
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict);
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "Hacker News Best Stories API";
        document.Info.Version = "v1";
        document.Info.Description = "Returns the best stories from Hacker News, sorted by score descending.";
        return Task.CompletedTask;
    });
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromSeconds(1),
                QueueLimit = 0,
            }));
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRateLimiter();
app.MapOpenApi();
app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "Hacker News Best Stories API v1"));

app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
app.MapHealthChecks("/health");

app.MapGet("/stories", async (int? n, BestStoriesService stories, HttpContext context, CancellationToken ct) =>
{
    if (n is not (>= 1 and <= 500))
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid 'n'",
            detail: "Query parameter 'n' is required and must be an integer between 1 and 500.");
    }

    try
    {
        return Results.Ok(await stories.GetAsync(n.Value, ct));
    }
    catch (Exception ex) when (!ct.IsCancellationRequested && ex is
        HttpRequestException or TaskCanceledException or BrokenCircuitException or TimeoutRejectedException)
    {
        context.Response.Headers.RetryAfter = "5";
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Hacker News unavailable",
            detail: "Could not reach Hacker News and no cached data is available. Try again shortly.");
    }
})
.WithName("GetBestStories")
.WithTags("Stories")
.Produces<IReadOnlyList<StoryResponse>>(StatusCodes.Status200OK)
.ProducesProblem(StatusCodes.Status400BadRequest)
.ProducesProblem(StatusCodes.Status503ServiceUnavailable)
.WithSummary("First n best stories from Hacker News, sorted by score descending.")
.AddOpenApiOperationTransformer((operation, _, _) =>
{
    var parameter = operation.Parameters?.FirstOrDefault(p => p.Name == "n");
    if (parameter is Microsoft.OpenApi.OpenApiParameter n)
    {
        n.Required = true;
        n.Description = "How many stories to return (1 to 500).";
    }

    return Task.CompletedTask;
});

app.Run();
