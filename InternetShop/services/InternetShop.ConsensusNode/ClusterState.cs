using Microsoft.Extensions.Options;

namespace InternetShop.ConsensusNode;

public sealed class ClusterState
{
    private readonly ClusterOptions _options;
    private readonly Dictionary<int, bool> _reachable = new();
    private readonly object _sync = new();
    private bool _available = true;
    private bool _acceptVotes = true;
    private int? _leaderId;
    private long _leaderTerm;
    private long _configurationVersion;

    public ClusterState(IOptions<ClusterOptions> options)
    {
        _options = options.Value;

        foreach (var node in _options.Nodes.Where(node => node.Id != _options.NodeId))
        {
            _reachable[node.Id] = false;
        }

        _leaderId = _options.Nodes.Max(node => node.Id);
    }

    public bool Available
    {
        get
        {
            lock (_sync)
            {
                return _available;
            }
        }
    }

    public int? GetLeaderId()
    {
        lock (_sync)
        {
            return _leaderId;
        }
    }

    public HeartbeatResponse GetHeartbeat()
    {
        lock (_sync)
        {
            return new HeartbeatResponse(
                _options.NodeId,
                _leaderId,
                _leaderTerm);
        }
    }

    public void UpdatePeers(IEnumerable<PeerHeartbeat> heartbeats)
    {
        lock (_sync)
        {
            var results = heartbeats.ToArray();

            foreach (var heartbeat in results)
            {
                _reachable[heartbeat.NodeId] = heartbeat.Available;
            }

            var newestLeadership = results
                .Where(heartbeat => heartbeat.Available &&
                                    heartbeat.LeaderId is not null &&
                                    heartbeat.LeaderTerm > _leaderTerm)
                .OrderByDescending(heartbeat => heartbeat.LeaderTerm)
                .ThenByDescending(heartbeat => heartbeat.NodeId)
                .FirstOrDefault();

            if (newestLeadership is not null)
            {
                _leaderId = newestLeadership.LeaderId;
                _leaderTerm = newestLeadership.LeaderTerm;
            }

            ElectLeaderIfNeeded();
        }
    }

    public NodeSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return new NodeSnapshot(
                _options.NodeId,
                _available ? "AVAILABLE" : "FAILED",
                _available && _leaderId == _options.NodeId ? "LEADER" : "FOLLOWER",
                _leaderId,
                _leaderTerm,
                _configurationVersion,
                _acceptVotes,
                DateTimeOffset.UtcNow);
        }
    }

    public VoteResponse Vote(long proposedVersion)
    {
        lock (_sync)
        {
            if (!_available)
            {
                return new VoteResponse(
                    _options.NodeId,
                    Responded: false,
                    Approved: false,
                    "Node is unavailable.");
            }

            if (!_acceptVotes)
            {
                return new VoteResponse(
                    _options.NodeId,
                    Responded: true,
                    Approved: false,
                    "Node is configured to reject proposals.");
            }

            var approved = proposedVersion > _configurationVersion;

            return new VoteResponse(
                _options.NodeId,
                Responded: true,
                Approved: approved,
                approved
                    ? "YES"
                    : $"NO: version must be greater than {_configurationVersion}.");
        }
    }

    public bool Commit(long version)
    {
        lock (_sync)
        {
            if (!_available || version <= _configurationVersion)
            {
                return false;
            }

            _configurationVersion = version;
            return true;
        }
    }

    public void ConfigureSimulation(SimulationRequest request)
    {
        lock (_sync)
        {
            _available = request.Available;
            _acceptVotes = request.AcceptVotes;
            ElectLeaderIfNeeded();
        }
    }

    private void ElectLeaderIfNeeded()
    {
        if (_leaderId is not null && IsAvailable(_leaderId.Value))
        {
            return;
        }

        var availableNodes = _reachable
            .Where(node => node.Value)
            .Select(node => node.Key)
            .ToList();

        if (_available)
        {
            availableNodes.Add(_options.NodeId);
        }

        var nextLeader = availableNodes.Count == 0
            ? (int?)null
            : availableNodes.Max();

        if (nextLeader != _leaderId)
        {
            _leaderId = nextLeader;
            _leaderTerm++;
        }
    }

    private bool IsAvailable(int nodeId) =>
        nodeId == _options.NodeId
            ? _available
            : _reachable.GetValueOrDefault(nodeId);
}
