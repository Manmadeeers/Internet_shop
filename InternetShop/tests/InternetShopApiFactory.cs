using InternetShop.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace InternetShop.Api.Tests;

public sealed class InternetShopApiFactory(string connectionString,
    int requestDelaySeconds = 0, int raceDelaySeconds = 0,
    ILoggerProvider? logs = null, bool enableFailureInjection = false, IInterceptor? interceptor = null)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        if (interceptor is not null)
            builder.ConfigureServices(services => services.AddDbContext<ShopDbContext>(
                options => options.AddInterceptors(interceptor)));
        if (logs is not null) builder.ConfigureLogging(logging => logging.AddProvider(logs));
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:InternetShopDb"] = connectionString,
                ["Lab:Enabled"] = "true",
                ["Lab:RequestDelaySeconds"] = requestDelaySeconds.ToString(),
                ["Lab:RaceDelaySeconds"] = raceDelaySeconds.ToString(),
                ["Lab:EnableFailureInjection"] = enableFailureInjection.ToString()
            }));
    }
}
