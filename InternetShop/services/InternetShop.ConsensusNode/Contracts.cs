namespace InternetShop.ConsensusNode;

public sealed class ClusterOptions
{
    public int NodeId { get; set; }

    public int HeartbeatIntervalMilliseconds { get; set; } = 1000;

    public int HeartbeatTimeoutMilliseconds { get; set; } = 750;

    public ClusterMember[] Nodes { get; set; } = [];
}

public sealed class ClusterMember
{
    public int Id { get; set; }

    public string BaseUrl { get; set; } = string.Empty;
}

public sealed record NodeSnapshot(
    int NodeId,
    string Status,
    string Role,
    int? LeaderId,
    long ConfigurationVersion,
    bool AcceptVotes,
    DateTimeOffset CheckedAt);

public sealed record ProposalRequest(long ConfigurationVersion);

public sealed record VoteResponse(
    int NodeId,
    bool Responded,
    bool Approved,
    string Reason);

public sealed record ConsensusResult(
    long ConfigurationVersion,
    int LeaderId,
    int PositiveVotes,
    int RequiredVotes,
    int TotalNodes,
    bool Achieved,
    string Message,
    IReadOnlyList<VoteResponse> Votes);

public sealed record SimulationRequest(bool Available, bool AcceptVotes = true);
