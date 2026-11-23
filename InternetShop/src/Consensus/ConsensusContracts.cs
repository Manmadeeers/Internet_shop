namespace InternetShop.Api.Consensus;

public sealed class ConsensusOptions
{
    public ConsensusNodeEndpoint[] Nodes { get; set; } = [];
}

public sealed class ConsensusNodeEndpoint
{
    public int Id { get; set; }

    public string BaseUrl { get; set; } = string.Empty;
}

public sealed record ConsensusNodeState(
    int NodeId,
    string Status,
    string Role,
    int? LeaderId,
    long LeaderTerm,
    long ConfigurationVersion,
    bool AcceptVotes,
    DateTimeOffset CheckedAt,
    string? Error = null);

public sealed record ClusterStatusResponse(
    int TotalNodes,
    int AvailableNodes,
    int RequiredMajority,
    bool ConsensusPossible,
    int? LeaderId,
    IReadOnlyList<ConsensusNodeState> Nodes);

public sealed record ConsensusProposalRequest(long ConfigurationVersion);

public sealed record ConsensusVote(
    int NodeId,
    bool Responded,
    bool Approved,
    string Reason);

public sealed record ConsensusProposalResponse(
    long ConfigurationVersion,
    int LeaderId,
    int PositiveVotes,
    int RequiredVotes,
    int TotalNodes,
    bool Achieved,
    string Message,
    IReadOnlyList<ConsensusVote> Votes);

public sealed record NodeSimulationRequest(bool Available, bool AcceptVotes = true);
