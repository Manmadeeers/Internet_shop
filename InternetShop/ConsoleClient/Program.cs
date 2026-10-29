using System.Globalization;
using InternetShop.Client.Api;
using InternetShop.Client.Clock;
using InternetShop.Client.ConsoleApp;

if (args.Length is < 2 or > 3 ||
    !int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0 ||
    !Uri.TryCreate(args[1], UriKind.Absolute, out var server) ||
    (server.Scheme != Uri.UriSchemeHttp && server.Scheme != Uri.UriSchemeHttps) ||
    !string.IsNullOrEmpty(server.UserInfo) || server.AbsolutePath != "/" ||
    !string.IsNullOrEmpty(server.Query) || !string.IsNullOrEmpty(server.Fragment) ||
    (args.Length == 3 && (!double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var offset) ||
                         !double.IsFinite(offset) || Math.Abs(offset) > 86400)))
{
    Console.Error.WriteLine(
        "Usage: dotnet run --project ConsoleClient/ConsoleClient.csproj -- <client-id> <server-url> [clock-offset-seconds]\n" +
        "Example: dotnet run --project ConsoleClient/ConsoleClient.csproj -- 1 http://localhost:5000");
    return 1;
}

var clock = new ClientClock(TimeSpan.FromSeconds(args.Length == 3
    ? double.Parse(args[2], CultureInfo.InvariantCulture) : 0));
using var client = new InternetShopApiClient(id, server, clock);
var application = new ConsoleApplication(id, client, clock);
await application.RunAsync();
return 0;
