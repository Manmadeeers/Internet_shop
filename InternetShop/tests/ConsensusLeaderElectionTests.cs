extern alias consensus;

using Microsoft.Extensions.Options;
using ClusterMember = consensus::InternetShop.ConsensusNode.ClusterMember;
using ClusterOptions = consensus::InternetShop.ConsensusNode.ClusterOptions;
using ClusterState = consensus::InternetShop.ConsensusNode.ClusterState;
using PeerHeartbeat = consensus::InternetShop.ConsensusNode.PeerHeartbeat;

namespace InternetShop.Tests;

public sealed class ConsensusLeaderElectionTests
{
    [Fact]
    public void RecoveredHigherIdNodeDoesNotReplaceCurrentLeader()
    {
        var state = CreateState(nodeId: 4);

        state.UpdatePeers(
        [
            Heartbeat(1, leaderId: 5, leaderTerm: 0),
            Heartbeat(2, leaderId: 5, leaderTerm: 0),
            Heartbeat(3, leaderId: 5, leaderTerm: 0),
            Unavailable(5)
        ]);

        Assert.Equal(4, state.GetLeaderId());
        Assert.Equal(1, state.GetSnapshot().LeaderTerm);

        state.UpdatePeers(
        [
            Heartbeat(1, leaderId: 4, leaderTerm: 1),
            Heartbeat(2, leaderId: 4, leaderTerm: 1),
            Heartbeat(3, leaderId: 4, leaderTerm: 1),
            Heartbeat(5, leaderId: 5, leaderTerm: 0)
        ]);

        var snapshot = state.GetSnapshot();
        Assert.Equal(4, snapshot.LeaderId);
        Assert.Equal("LEADER", snapshot.Role);
        Assert.Equal(1, snapshot.LeaderTerm);
    }

    [Fact]
    public void RestartedHigherIdNodeLearnsNewerLeadershipTerm()
    {
        var restartedNode = CreateState(nodeId: 5);

        restartedNode.UpdatePeers(
        [
            Heartbeat(1, leaderId: 4, leaderTerm: 1),
            Heartbeat(2, leaderId: 4, leaderTerm: 1),
            Heartbeat(3, leaderId: 4, leaderTerm: 1),
            Heartbeat(4, leaderId: 4, leaderTerm: 1)
        ]);

        var snapshot = restartedNode.GetSnapshot();
        Assert.Equal(4, snapshot.LeaderId);
        Assert.Equal("FOLLOWER", snapshot.Role);
        Assert.Equal(1, snapshot.LeaderTerm);
    }

    private static ClusterState CreateState(int nodeId)
    {
        var options = new ClusterOptions
        {
            NodeId = nodeId,
            Nodes = Enumerable.Range(1, 5)
                .Select(id => new ClusterMember
                {
                    Id = id,
                    BaseUrl = $"http://node-{id}"
                })
                .ToArray()
        };

        return new ClusterState(Options.Create(options));
    }

    private static PeerHeartbeat Heartbeat(
        int nodeId,
        int leaderId,
        long leaderTerm) =>
        new(nodeId, Available: true, leaderId, leaderTerm);

    private static PeerHeartbeat Unavailable(int nodeId) =>
        new(nodeId, Available: false, LeaderId: null, LeaderTerm: 0);
}
