using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace InternetShop.Messaging;

public sealed class BrokerOptions
{
    public string BaseUrl { get; set; } =
        "http://localhost:5090/";

    public string[] Endpoints { get; set; } = [];

    public Uri[] GetEndpoints()
    {
        var configured = Endpoints.Length == 0
            ? [BaseUrl]
            : Endpoints;

        return configured
            .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(endpoint =>
            {
                if (!Uri.TryCreate(
                        endpoint.TrimEnd('/') + "/",
                        UriKind.Absolute,
                        out var uri)
                    || uri.Scheme is not ("http" or "https"))
                {
                    throw new InvalidOperationException(
                        $"Broker endpoint '{endpoint}' is not a valid HTTP URL.");
                }

                return uri;
            })
            .ToArray() is { Length: > 0 } endpoints
            ? endpoints
            : throw new InvalidOperationException(
                "At least one broker endpoint must be configured.");
    }
}

public sealed record BrokerDelivery(
    Guid Receipt,
    DateTimeOffset LeaseExpiresAtUtc,
    int Attempt,
    EventEnvelope Envelope);

public sealed class BrokerClient : IDisposable
{
    private readonly HttpClient[] clients;
    private int nextEndpoint = -1;

    public BrokerClient(IOptions<BrokerOptions> options)
    {
        clients = options.Value.GetEndpoints()
            .Select(endpoint => new HttpClient(
                new SocketsHttpHandler
                {
                    ConnectTimeout = TimeSpan.FromSeconds(3)
                })
            {
                BaseAddress = endpoint,
                Timeout = TimeSpan.FromSeconds(30)
            })
            .ToArray();
    }

    public async Task PublishAsync(
        string routingKey,
        EventEnvelope envelope,
        CancellationToken ct)
    {
        using var response = await SendAsync(
            (client, token) => client.PostAsJsonAsync(
                "api/events",
                new { routingKey, envelope },
                token),
            ct);

        response.EnsureSuccessStatusCode();
    }

    public async Task<BrokerDelivery?> ReceiveAsync(
        string queue,
        CancellationToken ct)
    {
        using var response = await SendAsync(
            (client, token) => client.PostAsJsonAsync(
                $"api/queues/{Uri.EscapeDataString(queue)}/receive",
                new { waitSeconds = 20, leaseSeconds = 60 },
                token),
            ct);

        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        return await response.Content
            .ReadFromJsonAsync<BrokerDelivery>(cancellationToken: ct)
            ?? throw new InvalidDataException(
                "Broker returned an empty delivery.");
    }

    public Task<bool> AckAsync(
        string queue,
        Guid receipt,
        CancellationToken ct) =>
        SettleAsync(queue, "ack", receipt, null, ct);

    public Task<bool> RejectAsync(
        string queue,
        Guid receipt,
        string error,
        CancellationToken ct) =>
        SettleAsync(queue, "reject", receipt, error, ct);

    private async Task<bool> SettleAsync(
        string queue,
        string operation,
        Guid receipt,
        string? error,
        CancellationToken ct)
    {
        using var response = await SendAsync(
            (client, token) => client.PostAsJsonAsync(
                $"api/queues/{Uri.EscapeDataString(queue)}/{operation}",
                new { receipt, error },
                token),
            ct);

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpClient, CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken ct)
    {
        var start =
            (Interlocked.Increment(ref nextEndpoint) & int.MaxValue)
            % clients.Length;

        for (var attempt = 0; attempt < clients.Length; attempt++)
        {
            var client = clients[(start + attempt) % clients.Length];

            try
            {
                var response = await send(client, ct);

                if ((int)response.StatusCode < 500
                    || attempt == clients.Length - 1)
                {
                    return response;
                }

                response.Dispose();
            }
            catch (HttpRequestException)
                when (attempt < clients.Length - 1)
            {
                // EventId makes an uncertain publish safe to repeat.
            }
            catch (TaskCanceledException)
                when (!ct.IsCancellationRequested
                      && attempt < clients.Length - 1)
            {
                // Try the next broker after an endpoint timeout.
            }
        }

        throw new InvalidOperationException(
            "No broker endpoint handled the request.");
    }

    public void Dispose()
    {
        foreach (var client in clients)
        {
            client.Dispose();
        }
    }
}
