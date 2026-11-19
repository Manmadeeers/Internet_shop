using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace InternetShop.ConsensusNode;

public sealed class ClusterState
{
    private readonly ClusterOptions _options;
    private readonly ConcurrentDictionary<int, bool> _reachable = new();
    private readonly object _sync = new();
    private bool _available = true;
    private bool _acceptVotes = true;
    private long _configurationVersion;

    public ClusterState(IOptions<ClusterOptions> options)
    {
        _options = options.Value;

        foreach (var node in _options.Nodes.Where(node => node.Id != _options.NodeId))
        {
            _reachable[node.Id] = false;
        }
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

    public void SetPeerAvailability(int nodeId, bool available) =>
        _reachable[nodeId] = available;

    public int? GetLeaderId()
    {
        var availableNodes = _reachable
            .Where(node => node.Value)
            .Select(node => node.Key)
            .ToList();

        if (Available)
        {
            availableNodes.Add(_options.NodeId);
        }

        return availableNodes.Count == 0 ? null : availableNodes.Max();
    }

    public NodeSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            var leaderId = GetLeaderId();

            return new NodeSnapshot(
                _options.NodeId,
                _available ? "AVAILABLE" : "FAILED",
                _available && leaderId == _options.NodeId ? "LEADER" : "FOLLOWER",
                leaderId,
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
        }
    }
}
