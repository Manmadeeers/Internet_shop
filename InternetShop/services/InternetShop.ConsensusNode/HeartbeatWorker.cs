using System.Net.Http.Json;
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
        var previousLeader = state.GetLeaderId();
        var heartbeats = await Task.WhenAll(_options.Nodes
            .Where(node => node.Id != _options.NodeId)
            .Select(node => CheckPeerAsync(node, ct)));

        state.UpdatePeers(heartbeats);

        var currentLeader = state.GetLeaderId();
        if (previousLeader != currentLeader)
        {
            logger.LogInformation(
                "Leader changed from {PreviousLeader} to {CurrentLeader}",
                previousLeader,
                currentLeader);
        }
    }

    private async Task<PeerHeartbeat> CheckPeerAsync(
        ClusterMember node,
        CancellationToken ct)
    {
        var client = clientFactory.CreateClient("cluster");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.HeartbeatTimeoutMilliseconds);
            using var response = await client.GetAsync(
                $"{node.BaseUrl.TrimEnd('/')}/heartbeat",
                timeout.Token);

            if (response.IsSuccessStatusCode)
            {
                var heartbeat = await response.Content
                    .ReadFromJsonAsync<HeartbeatResponse>(cancellationToken: timeout.Token);

                if (heartbeat is not null)
                {
                    return new PeerHeartbeat(
                        node.Id,
                        Available: true,
                        heartbeat.LeaderId,
                        heartbeat.LeaderTerm);
                }
            }
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException &&
            !ct.IsCancellationRequested)
        {
        }

        return new PeerHeartbeat(
            node.Id,
            Available: false,
            LeaderId: null,
            LeaderTerm: 0);
    }
}
