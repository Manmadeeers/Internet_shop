using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace InternetShop.ConsensusNode;

public sealed class ConsensusCoordinator(
    IHttpClientFactory clientFactory,
    IOptions<ClusterOptions> options,
    ClusterState state,
    ILogger<ConsensusCoordinator> logger)
{
    private readonly ClusterOptions _options = options.Value;
    private readonly SemaphoreSlim _proposalLock = new(1, 1);

    public async Task<ConsensusResult> ProposeAsync(
        long configurationVersion,
        CancellationToken ct)
    {
        await _proposalLock.WaitAsync(ct);

        try
        {
            var leaderId = state.GetLeaderId();
            var requiredVotes = _options.Nodes.Length / 2 + 1;

            if (leaderId != _options.NodeId)
            {
                return new ConsensusResult(
                    configurationVersion,
                    leaderId ?? 0,
                    0,
                    requiredVotes,
                    _options.Nodes.Length,
                    Achieved: false,
                    $"Node {_options.NodeId} is not the leader. Current leader: {leaderId?.ToString() ?? "none"}.",
                    []);
            }

            var votes = await Task.WhenAll(_options.Nodes.Select(
                node => RequestVoteAsync(node, configurationVersion, ct)));
            var positiveVotes = votes.Count(vote => vote.Approved);
            var achieved = positiveVotes >= requiredVotes;

            if (achieved)
            {
                var respondingNodeIds = votes
                    .Where(vote => vote.Responded)
                    .Select(vote => vote.NodeId)
                    .ToHashSet();

                await Task.WhenAll(_options.Nodes
                    .Where(node => respondingNodeIds.Contains(node.Id))
                    .Select(node => CommitAsync(node, configurationVersion, ct)));
            }

            logger.LogInformation(
                "Consensus for version {Version}: {Result}; votes={Votes}/{Required}",
                configurationVersion,
                achieved ? "ACHIEVED" : "NOT ACHIEVED",
                positiveVotes,
                requiredVotes);

            return new ConsensusResult(
                configurationVersion,
                _options.NodeId,
                positiveVotes,
                requiredVotes,
                _options.Nodes.Length,
                achieved,
                achieved ? "CONSENSUS ACHIEVED" : "CONSENSUS NOT ACHIEVED",
                votes.OrderBy(vote => vote.NodeId).ToArray());
        }
        finally
        {
            _proposalLock.Release();
        }
    }

    private async Task<VoteResponse> RequestVoteAsync(
        ClusterMember node,
        long configurationVersion,
        CancellationToken ct)
    {
        if (node.Id == _options.NodeId)
        {
            return state.Vote(configurationVersion);
        }

        try
        {
            var client = clientFactory.CreateClient("cluster");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.HeartbeatTimeoutMilliseconds);
            using var response = await client.PostAsJsonAsync(
                $"{node.BaseUrl.TrimEnd('/')}/internal/vote",
                new ProposalRequest(configurationVersion),
                timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                return NoResponse(node.Id);
            }

            return await response.Content.ReadFromJsonAsync<VoteResponse>(
                       cancellationToken: timeout.Token)
                   ?? NoResponse(node.Id);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException &&
            !ct.IsCancellationRequested)
        {
            return NoResponse(node.Id);
        }
    }

    private async Task CommitAsync(
        ClusterMember node,
        long configurationVersion,
        CancellationToken ct)
    {
        if (node.Id == _options.NodeId)
        {
            state.Commit(configurationVersion);
            return;
        }

        try
        {
            var client = clientFactory.CreateClient("cluster");
            using var response = await client.PostAsJsonAsync(
                $"{node.BaseUrl.TrimEnd('/')}/internal/commit",
                new ProposalRequest(configurationVersion),
                ct);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException &&
            !ct.IsCancellationRequested)
        {
            logger.LogWarning(
                "Node {NodeId} did not receive commit for version {Version}",
                node.Id,
                configurationVersion);
        }
    }

    private static VoteResponse NoResponse(int nodeId) =>
        new(nodeId, Responded: false, Approved: false, "NO RESPONSE");
}
