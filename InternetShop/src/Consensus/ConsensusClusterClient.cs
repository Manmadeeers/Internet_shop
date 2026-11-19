using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace InternetShop.Api.Consensus;

public sealed class ConsensusClusterClient(
    IHttpClientFactory clientFactory,
    IOptions<ConsensusOptions> options)
{
    private readonly ConsensusOptions _options = options.Value;

    public async Task<ClusterStatusResponse> GetStatusAsync(CancellationToken ct)
    {
        var nodes = await Task.WhenAll(_options.Nodes.Select(
            node => GetNodeStateAsync(node, ct)));
        var available = nodes
            .Where(node => node.Status == "AVAILABLE")
            .ToArray();
        var leaderId = available.Length == 0
            ? (int?)null
            : available.Max(node => node.NodeId);

        var normalized = nodes
            .Select(node => node with
            {
                Role = node.Status == "AVAILABLE" && node.NodeId == leaderId
                    ? "LEADER"
                    : "FOLLOWER",
                LeaderId = leaderId
            })
            .OrderBy(node => node.NodeId)
            .ToArray();
        var majority = _options.Nodes.Length / 2 + 1;

        return new ClusterStatusResponse(
            _options.Nodes.Length,
            available.Length,
            majority,
            available.Length >= majority,
            leaderId,
            normalized);
    }

    public async Task<ConsensusProposalResponse> ProposeAsync(
        ConsensusProposalRequest proposal,
        CancellationToken ct)
    {
        var status = await GetStatusAsync(ct);
        if (status.LeaderId is null)
        {
            return UnavailableResult(proposal.ConfigurationVersion, status);
        }

        var leader = _options.Nodes.Single(node => node.Id == status.LeaderId);

        try
        {
            var client = clientFactory.CreateClient("consensus-cluster");
            using var response = await client.PostAsJsonAsync(
                $"{leader.BaseUrl.TrimEnd('/')}/consensus",
                proposal,
                ct);

            var result = await response.Content
                .ReadFromJsonAsync<ConsensusProposalResponse>(cancellationToken: ct);

            return result ?? UnavailableResult(proposal.ConfigurationVersion, status);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException &&
            !ct.IsCancellationRequested)
        {
            return UnavailableResult(proposal.ConfigurationVersion, status);
        }
    }

    public async Task<ClusterStatusResponse?> ConfigureNodeAsync(
        int nodeId,
        NodeSimulationRequest request,
        CancellationToken ct)
    {
        var node = _options.Nodes.SingleOrDefault(node => node.Id == nodeId);
        if (node is null)
        {
            return null;
        }

        var client = clientFactory.CreateClient("consensus-cluster");
        using var response = await client.PutAsJsonAsync(
            $"{node.BaseUrl.TrimEnd('/')}/admin/state",
            request,
            ct);
        response.EnsureSuccessStatusCode();

        return await GetStatusAsync(ct);
    }

    private async Task<ConsensusNodeState> GetNodeStateAsync(
        ConsensusNodeEndpoint node,
        CancellationToken ct)
    {
        try
        {
            var client = clientFactory.CreateClient("consensus-cluster");
            var state = await client.GetFromJsonAsync<ConsensusNodeState>(
                $"{node.BaseUrl.TrimEnd('/')}/state",
                ct);

            return state ?? FailedNode(node.Id, "Node returned an empty response.");
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException &&
            !ct.IsCancellationRequested)
        {
            return FailedNode(node.Id, exception.GetType().Name);
        }
    }

    private static ConsensusNodeState FailedNode(int nodeId, string error) =>
        new(
            nodeId,
            "FAILED",
            "FOLLOWER",
            null,
            0,
            AcceptVotes: false,
            DateTimeOffset.UtcNow,
            error);

    private static ConsensusProposalResponse UnavailableResult(
        long version,
        ClusterStatusResponse status) =>
        new(
            version,
            status.LeaderId ?? 0,
            0,
            status.RequiredMajority,
            status.TotalNodes,
            Achieved: false,
            "CONSENSUS NOT ACHIEVED: leader is unavailable.",
            []);
}
