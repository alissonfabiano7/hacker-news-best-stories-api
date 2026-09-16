namespace HackerNews.BestStories.Api.HackerNews;

public sealed class HackerNewsOptions
{
    public const string SectionName = "HackerNews";

    public string BaseUrl { get; set; } = "https://hacker-news.firebaseio.com/v0/";

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan IdsTtl { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan ItemTtl { get; set; } = TimeSpan.FromMinutes(5);

    public int MaxConcurrency { get; set; } = 10;
}
