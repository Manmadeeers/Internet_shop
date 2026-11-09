using System.Globalization;
using System.Text.Json;
using InternetShop.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InternetShop.EventAnalyticsSubscriber;

public sealed class Worker : BrokerSubscriber
{
    private static readonly JsonSerializerOptions StateJson = new()
    {
        WriteIndented = true
    };

    private readonly ILogger<Worker> logger;
    private readonly string stateFile;
    private readonly object gate = new();
    private AnalyticsState state;

    public Worker(
        IOptions<BrokerOptions> brokerOptions,
        IOptions<AnalyticsOptions> analyticsOptions,
        ILogger<Worker> logger) : base(brokerOptions, logger)
    {
        this.logger = logger;
        stateFile = Path.GetFullPath(analyticsOptions.Value.StateFile);
        Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);

        state = File.Exists(stateFile)
            ? JsonSerializer.Deserialize<AnalyticsState>(
                  File.ReadAllText(stateFile),
                  StateJson)
              ?? throw new InvalidDataException(
                  "Analytics state file contains no state.")
            : new AnalyticsState();

        logger.LogInformation(
            "Analytics loaded. TotalEvents={TotalEvents}",
            state.TotalEvents);
    }

    protected override string QueueName => ShopTopology.AnalyticsQueue;

    protected override Task HandleAsync(
        EventEnvelope message,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (gate)
        {
            if (state.ProcessedEventIds.Contains(message.EventId))
            {
                logger.LogInformation(
                    "ANALYTICS duplicate ignored EventId={EventId}",
                    message.EventId);
                return Task.CompletedTask;
            }

            var eventDate = message.OccurredAtUtc.UtcDateTime
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            var next = new AnalyticsState
            {
                TotalEvents = state.TotalEvents + 1,
                EventsByType = new Dictionary<string, long>(
                    state.EventsByType),
                EventsByUtcDate = new Dictionary<string, long>(
                    state.EventsByUtcDate),
                LastObservedAtUtc = DateTimeOffset.UtcNow,
                ProcessedEventIds = new HashSet<Guid>(
                    state.ProcessedEventIds)
            };

            next.EventsByType[message.EventType] =
                next.EventsByType.GetValueOrDefault(message.EventType) + 1;
            next.EventsByUtcDate[eventDate] =
                next.EventsByUtcDate.GetValueOrDefault(eventDate) + 1;
            next.ProcessedEventIds.Add(message.EventId);

            SaveState(next);
            state = next;

            logger.LogInformation(
                "ANALYTICS EventType={EventType} TypeCount={TypeCount} TotalEvents={TotalEvents}",
                message.EventType,
                next.EventsByType[message.EventType],
                next.TotalEvents);

            return Task.CompletedTask;
        }
    }

    private void SaveState(AnalyticsState value)
    {
        var temporaryFile = stateFile + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, StateJson);

        using (var stream = new FileStream(
            temporaryFile,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporaryFile, stateFile, overwrite: true);
    }
}
