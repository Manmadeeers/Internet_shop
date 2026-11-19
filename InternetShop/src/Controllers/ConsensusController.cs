using InternetShop.Api.Consensus;
using Microsoft.AspNetCore.Mvc;

namespace InternetShop.Api.Controllers;

[ApiController]
[Route("api/consensus")]
public sealed class ConsensusController(ConsensusClusterClient cluster) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ClusterStatusResponse>> GetStatus(
        CancellationToken ct) =>
        Ok(await cluster.GetStatusAsync(ct));

    [HttpPost("proposals")]
    public async Task<ActionResult<ConsensusProposalResponse>> Propose(
        ConsensusProposalRequest request,
        CancellationToken ct)
    {
        if (request.ConfigurationVersion <= 0)
        {
            return BadRequest(
                new ValidationProblemDetails(
                    new Dictionary<string, string[]>
                    {
                        [nameof(request.ConfigurationVersion)] =
                            ["ConfigurationVersion must be greater than zero."]
                    }));
        }

        return Ok(await cluster.ProposeAsync(request, ct));
    }

    [HttpPut("nodes/{nodeId:int}/state")]
    public async Task<ActionResult<ClusterStatusResponse>> ConfigureNode(
        int nodeId,
        NodeSimulationRequest request,
        CancellationToken ct)
    {
        try
        {
            var result = await cluster.ConfigureNodeAsync(nodeId, request, ct);
            return result is null ? NotFound() : Ok(result);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException &&
            !ct.IsCancellationRequested)
        {
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: $"Node {nodeId} is unreachable.");
        }
    }
}
