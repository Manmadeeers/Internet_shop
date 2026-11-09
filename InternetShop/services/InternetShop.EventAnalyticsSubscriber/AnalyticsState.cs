namespace InternetShop.EventAnalyticsSubscriber;

public sealed class AnalyticsState
{
    public long TotalEvents { get; set; }
    public Dictionary<string, long> EventsByType { get; set; } = [];
    public Dictionary<string, long> EventsByUtcDate { get; set; } = [];
    public DateTimeOffset? LastObservedAtUtc { get; set; }
    public HashSet<Guid> ProcessedEventIds { get; set; } = [];
}
