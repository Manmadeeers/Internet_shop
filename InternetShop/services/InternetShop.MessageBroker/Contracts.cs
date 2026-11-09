using System.Text.Json;

namespace InternetShop.MessageBroker;

public sealed record EventEnvelope(
    Guid EventId,
    string EventType,
    int Version,
    DateTimeOffset OccurredAtUtc,
    JsonElement Data);

public sealed record PublishRequest(
    string RoutingKey,
    EventEnvelope Envelope);

public sealed record PublishResult(
    Guid EventId,
    int MatchedQueues,
    bool Duplicate);

public sealed record ReceiveRequest(
    int WaitSeconds = 20,
    int LeaseSeconds = 60);

public sealed record DeliveryResponse(
    Guid Receipt,
    DateTimeOffset LeaseExpiresAtUtc,
    int Attempt,
    EventEnvelope Envelope);

public sealed record ReceiptRequest(
    Guid Receipt,
    string? Error = null);

public sealed record QueueStatistics(
    string Name,
    long Ready,
    long Delayed,
    long InFlight,
    long Completed,
    long Dead);

public sealed record DeadLetter(
    string Queue,
    Guid EventId,
    int Attempts,
    string? Error,
    EventEnvelope Envelope);

public sealed class BrokerHttpException(
    int statusCode,
    string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
