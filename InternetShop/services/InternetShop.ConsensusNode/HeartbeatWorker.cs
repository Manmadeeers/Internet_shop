using Microsoft.Extensions.Options;

namespace InternetShop.ConsensusNode;

public sealed class HeartbeatWorker(
    IHttpClientFactory clientFactory,
    IOptions<ClusterOptions> options,
    ClusterState state,
    ILogger<HeartbeatWorker> logger) : BackgroundService
{
    private readonly ClusterOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromMilliseconds(_options.HeartbeatIntervalMilliseconds));

        await CheckPeersAsync(stoppingToken);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await CheckPeersAsync(stoppingToken);
        }
    }

    private async Task CheckPeersAsync(CancellationToken ct)
    {
        await Task.WhenAll(_options.Nodes
            .Where(node => node.Id != _options.NodeId)
            .Select(node => CheckPeerAsync(node, ct)));
    }

    private async Task CheckPeerAsync(ClusterMember node, CancellationToken ct)
    {
        var client = clientFactory.CreateClient("cluster");
        var previousLeader = state.GetLeaderId();
        var available = false;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.HeartbeatTimeoutMilliseconds);
            using var response = await client.GetAsync(
                $"{node.BaseUrl.TrimEnd('/')}/heartbeat",
                timeout.Token);
            available = response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException &&
            !ct.IsCancellationRequested)
        {
            available = false;
        }

        state.SetPeerAvailability(node.Id, available);

        var currentLeader = state.GetLeaderId();
        if (previousLeader != currentLeader)
        {
            logger.LogInformation(
                "Leader changed from {PreviousLeader} to {CurrentLeader}",
                previousLeader,
                currentLeader);
        }
    }
}
