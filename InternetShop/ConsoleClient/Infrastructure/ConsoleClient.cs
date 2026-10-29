using InternetShop.Client.Api;
using InternetShop.Client.Clock;

namespace InternetShop.Client.ConsoleApp;

public sealed class ConsoleApplication(int clientId, InternetShopApiClient client, ClientClock clock)
{
    public async Task RunAsync()
    {
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            Console.WriteLine($"Client {clientId}. Enter: GET /api/path or POST /api/path.");
            Console.WriteLine("For POST, paste JSON; an empty line ends the JSON body.");
            Console.WriteLine("An empty command exits. Ctrl+C cancels a request and exits.");
            while (!stop.IsCancellationRequested)
            {
                Console.Write("> ");
                var line = await Console.In.ReadLineAsync(stop.Token);
                if (string.IsNullOrWhiteSpace(line)) break;
                if (!TryParseCommand(line, out var method, out var path))
                {
                    Console.WriteLine("Enter GET or POST and a path starting with /api/ (or /health).");
                    continue;
                }

                string? json = null;
                if (method == "POST")
                {
                    Console.WriteLine("JSON (finish with an empty line):");
                    var lines = new List<string>();
                    while (true)
                    {
                        var bodyLine = await Console.In.ReadLineAsync(stop.Token);
                        if (string.IsNullOrWhiteSpace(bodyLine)) break;
                        lines.Add(bodyLine);
                    }
                    json = string.Join(Environment.NewLine, lines);
                }

                try
                {
                    Console.WriteLine($"Client {clientId} -> SEND {method} {path}");
                    var response = await client.SendAsync(method, path, json, stop.Token);
                    Console.WriteLine($"Client {clientId} <- HTTP {response.StatusCode}");
                    Console.WriteLine(response.Body);
                    PrintTimeStamps();
                }
                catch (HttpRequestException ex)
                {
                    Console.WriteLine($"Server unavailable or connection lost: {ex.Message}");
                }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                {
                    Console.WriteLine("Server response timed out.");
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            Console.WriteLine("Client cancelled.");
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private void PrintTimeStamps()
    {
        Console.WriteLine($"Client local time: {clock.LocalTime:O}");
        Console.WriteLine($"Request sent time: {clock.RequestTime:O}");
        Console.WriteLine($"Response received time: {clock.ResponseTime:O}");
        Console.WriteLine($"Request RTT (including server work): {clock.RoundTripTime?.TotalMilliseconds:F3} ms");
        if (clock.ServerTime is { } serverTime)
        {
            Console.WriteLine($"Server time at response: {serverTime:O}");
            Console.WriteLine($"Estimated one-way network delay: {clock.NetworkDelay?.TotalMilliseconds:F3} ms");
            Console.WriteLine($"Saved correction: {clock.ServerCorrection?.TotalSeconds:F3} seconds");
            Console.WriteLine($"Corrected client time: {clock.CorrectedTime:O}");
        }
        else Console.WriteLine("No valid synchronization timestamps in this response; saved correction retained.");
    }

    private static bool TryParseCommand(string line, out string method, out string path)
    {
        method = "";
        path = "";
        var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;
        method = parts[0].ToUpperInvariant();
        path = parts[1].Trim();
        return (method is "GET" or "POST") &&
            !path.Any(char.IsWhiteSpace) && !path.Contains('\\') &&
            (path.StartsWith("/api/", StringComparison.Ordinal) || path == "/health");
    }
}
