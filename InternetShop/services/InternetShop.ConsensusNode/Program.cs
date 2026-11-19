using InternetShop.ConsensusNode;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<ClusterOptions>()
    .Bind(builder.Configuration.GetSection("Cluster"))
    .Validate(
        options => options.NodeId > 0 &&
                   options.Nodes.Length > 0 &&
                   options.Nodes.Select(node => node.Id).Distinct().Count() == options.Nodes.Length &&
                   options.Nodes.Any(node => node.Id == options.NodeId) &&
                   options.Nodes.All(node => node.Id > 0 && Uri.IsWellFormedUriString(node.BaseUrl, UriKind.Absolute)),
        "Cluster must contain this node and unique positive IDs with absolute URLs.")
    .Validate(
        options => options.HeartbeatIntervalMilliseconds >= 100 &&
                   options.HeartbeatTimeoutMilliseconds >= 100,
        "Heartbeat interval and timeout must be at least 100 ms.")
    .ValidateOnStart();

builder.Services.AddHttpClient("cluster", client =>
    client.Timeout = TimeSpan.FromSeconds(2));
builder.Services.AddSingleton<ClusterState>();
builder.Services.AddSingleton<ConsensusCoordinator>();
builder.Services.AddHostedService<HeartbeatWorker>();

var app = builder.Build();

app.MapGet("/heartbeat", (ClusterState state) =>
    state.Available
        ? Results.Ok(new { message = "ALIVE" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.MapGet("/state", (ClusterState state) => state.GetSnapshot());

app.MapPost("/consensus", async (
    ProposalRequest request,
    ClusterState state,
    ConsensusCoordinator coordinator,
    CancellationToken ct) =>
{
    if (!state.Available)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    var result = await coordinator.ProposeAsync(request.ConfigurationVersion, ct);
    return result.LeaderId == state.GetSnapshot().NodeId
        ? Results.Ok(result)
        : Results.Conflict(result);
});

app.MapPost("/internal/vote", (ProposalRequest request, ClusterState state) =>
{
    var vote = state.Vote(request.ConfigurationVersion);
    return vote.Responded
        ? Results.Ok(vote)
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

app.MapPost("/internal/commit", (ProposalRequest request, ClusterState state) =>
    state.Commit(request.ConfigurationVersion)
        ? Results.Ok(state.GetSnapshot())
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.MapPut("/admin/state", (SimulationRequest request, ClusterState state) =>
{
    state.ConfigureSimulation(request);
    return Results.Ok(state.GetSnapshot());
});

app.Run();

public partial class Program;
