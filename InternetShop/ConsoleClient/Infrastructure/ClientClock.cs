namespace InternetShop.Client.Clock;

public sealed class ClientClock(TimeSpan configuredOffset, TimeProvider? timeProvider = null)
{
    public DateTimeOffset LocalTime => (timeProvider ?? TimeProvider.System).GetLocalNow() + configuredOffset;
    public DateTimeOffset CorrectedTime => LocalTime + (ServerCorrection ?? TimeSpan.Zero);
    public DateTimeOffset? RequestTime { get; private set; }
    public DateTimeOffset? ResponseTime { get; private set; }
    public DateTimeOffset? ServerTime { get; private set; }
    public TimeSpan? ServerCorrection { get; private set; }
    public TimeSpan? RoundTripTime { get; private set; }
    public TimeSpan? NetworkDelay { get; private set; }

    public void RecordRequestSent()
    {
        RequestTime = LocalTime;
        ResponseTime = null;
        ServerTime = null;
        RoundTripTime = null;
        NetworkDelay = null;
    }

    public DateTimeOffset RecordResponseReceived()
    {
        ResponseTime = LocalTime;
        RoundTripTime = ResponseTime - RequestTime;
        return ResponseTime.Value;
    }

    public void UpdateCorrection(DateTimeOffset serverReceived, DateTimeOffset serverSent)
    {
        if (RequestTime is not { } sent || ResponseTime is not { } received ||
            serverSent < serverReceived || received < sent) return;
        var networkRoundTrip = (received - sent) - (serverSent - serverReceived);
        NetworkDelay = TimeSpan.FromTicks(Math.Max(0, networkRoundTrip.Ticks) / 2);
        ServerTime = serverSent;
        ServerCorrection = TimeSpan.FromTicks(
            ((serverReceived - sent).Ticks + (serverSent - received).Ticks) / 2);
    }
}
