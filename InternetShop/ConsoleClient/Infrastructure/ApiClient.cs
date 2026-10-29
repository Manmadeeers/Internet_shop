using System.Globalization;
using System.Text;
using InternetShop.Client.Clock;

namespace InternetShop.Client.Api;

public sealed class InternetShopApiClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ClientClock _clock;

    public InternetShopApiClient(int clientId, Uri server, ClientClock clock, HttpMessageHandler? handler = null)
    {
        _clock = clock;
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.BaseAddress = server;
        _httpClient.Timeout = TimeSpan.FromMinutes(3);
        _httpClient.DefaultRequestHeaders.Add("X-Client-Id",
            clientId.ToString(CultureInfo.InvariantCulture));
    }

    public async Task<(int StatusCode, string Body)> SendAsync(
        string method, string path, string? json, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (request.Method == HttpMethod.Post)
            request.Content = new StringContent(json ?? "", Encoding.UTF8, "application/json");

        _clock.RecordRequestSent();
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        _clock.RecordResponseReceived();
        if (ReadTimestamp(response, "X-Server-Received-Time") is { } received &&
            ReadTimestamp(response, "X-Server-Time") is { } sent)
            _clock.UpdateCorrection(received, sent);
        var body = await response.Content.ReadAsStringAsync(ct);
        return ((int)response.StatusCode, body);
    }

    private static DateTimeOffset? ReadTimestamp(HttpResponseMessage response, string header)
    {
        if (response.Headers.TryGetValues(header, out var values) &&
            DateTimeOffset.TryParse(values.FirstOrDefault(), CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var timestamp)) return timestamp;
        return null;
    }

    public void Dispose() => _httpClient.Dispose();
}
