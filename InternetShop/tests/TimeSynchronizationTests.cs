using System.Globalization;
using System.Net;
using InternetShop.Client.Clock;
using InternetShop.Client.Api;

namespace InternetShop.Api.Tests;

[Trait("Category", "Time")]
public sealed class TimeSynchronizationTests
{
    [Fact]
    public void Correction_RemovesClientOffsetAndExcludesServerProcessing()
    {
        var start = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        var time = new ManualTime(start);
        var clock = new ClientClock(TimeSpan.FromSeconds(120), time);
        clock.RecordRequestSent();
        time.UtcNow = start.AddMilliseconds(10200);
        clock.RecordResponseReceived();
        clock.UpdateCorrection(start.AddMilliseconds(100), start.AddMilliseconds(10100));
        Assert.Equal(TimeSpan.FromSeconds(-120), clock.ServerCorrection);
        Assert.Equal(TimeSpan.FromMilliseconds(10200), clock.RoundTripTime);
        Assert.Equal(TimeSpan.FromMilliseconds(100), clock.NetworkDelay);
        Assert.Equal(time.UtcNow, clock.CorrectedTime.ToUniversalTime());
        clock.RecordRequestSent();
        Assert.Null(clock.ServerTime);
        Assert.Null(clock.NetworkDelay);
        Assert.Equal(TimeSpan.FromSeconds(-120), clock.ServerCorrection);
    }

    [Theory]
    [InlineData("/api/time", HttpStatusCode.OK)]
    [InlineData("/api/not-a-route", HttpStatusCode.NotFound)]
    public async Task Responses_IncludeTimeHeadersWithoutDatabaseAccess(string path, HttpStatusCode status)
    {
        await using var factory = new InternetShopApiFactory(
            "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused");
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path);
        Assert.Equal(status, response.StatusCode);
        var received = DateTimeOffset.Parse(response.Headers.GetValues("X-Server-Received-Time").Single(),
            CultureInfo.InvariantCulture);
        var sent = DateTimeOffset.Parse(response.Headers.GetValues("X-Server-Time").Single(),
            CultureInfo.InvariantCulture);
        Assert.True(sent >= received);
        if (path == "/api/time")
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Cors_ExposesSynchronizationHeaders()
    {
        await using var factory = new InternetShopApiFactory(
            "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused");
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/time");
        request.Headers.Add("Origin", "http://127.0.0.1:5500");
        using var response = await client.SendAsync(request);
        var exposed = string.Join(",", response.Headers.GetValues("Access-Control-Expose-Headers"));
        Assert.Contains("X-Server-Time", exposed);
        Assert.Contains("X-Server-Received-Time", exposed);
    }

    [Theory]
    [InlineData(120)]
    [InlineData(-90)]
    [InlineData(0)]
    public void RepeatedSamples_ReplaceCorrectionAndHandleBothOffsetDirections(int offsetSeconds)
    {
        var start = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        var time = new ManualTime(start);
        var clock = new ClientClock(TimeSpan.FromSeconds(offsetSeconds), time);
        for (var i = 0; i < 2; i++)
        {
            clock.RecordRequestSent();
            time.UtcNow = time.UtcNow.AddMilliseconds(200);
            clock.RecordResponseReceived();
            var server = time.UtcNow.AddMilliseconds(-100).ToOffset(TimeSpan.FromHours(3));
            clock.UpdateCorrection(server, server);
            Assert.Equal(TimeSpan.FromSeconds(-offsetSeconds), clock.ServerCorrection);
            Assert.Equal(time.UtcNow, clock.CorrectedTime.ToUniversalTime());
        }
    }

    [Fact]
    public void InvalidSample_RetainsPreviousCorrection()
    {
        var start = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        var time = new ManualTime(start);
        var clock = new ClientClock(TimeSpan.FromSeconds(120), time);
        clock.RecordRequestSent();
        time.UtcNow = start.AddMilliseconds(200);
        clock.RecordResponseReceived();
        clock.UpdateCorrection(start.AddMilliseconds(100), start.AddMilliseconds(100));
        var saved = clock.ServerCorrection;
        clock.UpdateCorrection(start.AddSeconds(1), start);
        Assert.Equal(saved, clock.ServerCorrection);
        clock.RecordRequestSent();
        clock.UpdateCorrection(start, start);
        Assert.Equal(saved, clock.ServerCorrection);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    public async Task ConsoleApiClient_UpdatesCorrectionOnSuccessAndError(int status)
    {
        var start = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        var time = new ManualTime(start);
        var clock = new ClientClock(TimeSpan.FromSeconds(120), time);
        using var handler = new FakeHandler(request =>
        {
            Assert.Equal("7", request.Headers.GetValues("X-Client-Id").Single());
            time.UtcNow = start.AddMilliseconds(10200);
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("{}") };
            response.Headers.Add("X-Server-Received-Time", start.AddMilliseconds(100).ToString("O"));
            response.Headers.Add("X-Server-Time", start.AddMilliseconds(10100).ToString("O"));
            return response;
        });
        using var client = new InternetShopApiClient(7, new Uri("http://localhost"), clock, handler);
        var result = await client.SendAsync("GET", "/api/time", null, CancellationToken.None);
        Assert.Equal(status, result.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(-120), clock.ServerCorrection);
        Assert.Equal(TimeSpan.FromMilliseconds(100), clock.NetworkDelay);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConsoleApiClient_MissingOrMalformedHeaders_RetainsCorrection(bool malformed)
    {
        var start = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        var time = new ManualTime(start);
        var clock = new ClientClock(TimeSpan.FromSeconds(120), time);
        clock.RecordRequestSent();
        time.UtcNow = start.AddMilliseconds(200);
        clock.RecordResponseReceived();
        clock.UpdateCorrection(start.AddMilliseconds(100), start.AddMilliseconds(100));
        var saved = clock.ServerCorrection;
        using var handler = new FakeHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            if (malformed)
            {
                response.Headers.TryAddWithoutValidation("X-Server-Time", "not-a-date");
                response.Headers.TryAddWithoutValidation("X-Server-Received-Time", "not-a-date");
            }
            return response;
        });
        using var client = new InternetShopApiClient(1, new Uri("http://localhost"), clock, handler);
        await client.SendAsync("GET", "/api/time", null, CancellationToken.None);
        Assert.Equal(saved, clock.ServerCorrection);
        Assert.Null(clock.ServerTime);
        Assert.Null(clock.NetworkDelay);
    }

    [Fact]
    public async Task ServerDelay_IsExcludedFromNetworkEstimate_EvenOnErrorResponse()
    {
        await using var factory = new InternetShopApiFactory(
            "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused",
            requestDelaySeconds: 1);
        using var setup = factory.CreateClient();
        var clock = new ClientClock(TimeSpan.FromSeconds(120));
        using var client = new InternetShopApiClient(1, new Uri("http://localhost"), clock,
            factory.Server.CreateHandler());
        var result = await client.SendAsync("POST", "/api/operations/Unknown", "{}", CancellationToken.None);
        Assert.Equal(400, result.StatusCode);
        Assert.NotNull(clock.ServerTime);
        Assert.NotNull(clock.RoundTripTime);
        Assert.NotNull(clock.NetworkDelay);
        Assert.NotNull(clock.ServerCorrection);
        Assert.True(clock.RoundTripTime.Value >= TimeSpan.FromMilliseconds(900));
        Assert.True(clock.RoundTripTime.Value - clock.NetworkDelay.Value * 2 >= TimeSpan.FromMilliseconds(900));
        Assert.InRange(clock.ServerCorrection.Value.TotalSeconds, -121, -119);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private sealed class ManualTime(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
