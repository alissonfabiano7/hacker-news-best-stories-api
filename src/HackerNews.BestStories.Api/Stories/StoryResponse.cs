namespace HackerNews.BestStories.Api.Stories;

public sealed record StoryResponse(
    string Title,
    string? Uri,
    string PostedBy,
    DateTimeOffset Time,
    int Score,
    int CommentCount);
