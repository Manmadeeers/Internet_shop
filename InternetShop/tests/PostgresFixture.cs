using Npgsql;
using Testcontainers.PostgreSql;

namespace InternetShop.Api.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    public PostgreSqlContainer Container { get; } = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("internet_shop_tests")
        .WithUsername("test_user")
        .WithPassword("test_password")
        .Build();

    public async Task InitializeAsync()
    {
        await Container.StartAsync();
        await using var connection = new NpgsqlConnection(Container.GetConnectionString());
        await connection.OpenAsync();
        foreach (var file in new[] { "schema.sql", "seed.sql" })
        {
            await using var command = new NpgsqlCommand(
                await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, file)), connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    public Task DisposeAsync()
    {
        return Container.DisposeAsync().AsTask();
    }
}
