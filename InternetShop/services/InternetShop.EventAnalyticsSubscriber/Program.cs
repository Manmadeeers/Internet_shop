using InternetShop.EventAnalyticsSubscriber;
using InternetShop.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});
builder.Services.Configure<BrokerOptions>(
    builder.Configuration.GetSection("Broker"));
builder.Services.Configure<AnalyticsOptions>(
    builder.Configuration.GetSection("Analytics"));
builder.Services.AddHostedService<Worker>();
await builder.Build().RunAsync();
