using System.Data;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace InternetShop.Api.Infrastructure.Replication;

public sealed class DatabaseSnapshotCopier(
    IOptions<DemoReplicationOptions> options)
{
    private static readonly TableDefinition[] Tables =
    [
        new("customer", true),
        new("product", true),
        new("orders", true),
        new("orderitem", false),
        new("payment", true)
    ];

    public async Task CopyAsync(
        string sourceConnectionString,
        string destinationConnectionString,
        CancellationToken ct)
    {
        var sourceSettings =
            new NpgsqlConnectionStringBuilder(sourceConnectionString);

        var destinationSettings =
            new NpgsqlConnectionStringBuilder(destinationConnectionString);

        if (sourceSettings.Host == destinationSettings.Host &&
            sourceSettings.Port == destinationSettings.Port &&
            sourceSettings.Database == destinationSettings.Database)
        {
            throw new InvalidOperationException(
                "Source and destination must be different databases.");
        }

        await using var source =
            new NpgsqlConnection(sourceConnectionString);

        await using var destination =
            new NpgsqlConnection(destinationConnectionString);

        await source.OpenAsync(ct);
        await destination.OpenAsync(ct);

        await using var sourceTransaction =
            await source.BeginTransactionAsync(
                IsolationLevel.RepeatableRead, ct);

        var snapshots = new List<TableSnapshot>();

        foreach (var table in Tables)
        {
            var sql = $"""
                SELECT COALESCE(
                    jsonb_agg(to_jsonb(t)),
                    '[]'::jsonb
                )::text
                FROM public.{table.Name} AS t;
                """;

            await using var command =
                new NpgsqlCommand(sql, source, sourceTransaction)
                {
                    CommandTimeout = options.Value.SyncTimeoutSeconds
                };

            var rows = (string)(await command.ExecuteScalarAsync(ct))!;

            long sequenceValue = 1;
            bool sequenceCalled = false;

            if (table.HasIdentity)
            {
                await using var sequenceNameCommand = new NpgsqlCommand(
                    "SELECT pg_get_serial_sequence(@table, 'id');",
                    source,
                    sourceTransaction);

                sequenceNameCommand.Parameters.AddWithValue(
                    "table", $"public.{table.Name}");

                var sequenceName =
                    (string)(await sequenceNameCommand.ExecuteScalarAsync(ct))!;

                await using var sequenceCommand = new NpgsqlCommand(
                    $"SELECT last_value, is_called FROM {sequenceName};",
                    source,
                    sourceTransaction);

                await using var reader =
                    await sequenceCommand.ExecuteReaderAsync(ct);

                await reader.ReadAsync(ct);

                sequenceValue = reader.GetInt64(0);
                sequenceCalled = reader.GetBoolean(1);
            }

            snapshots.Add(new TableSnapshot(
                table,
                rows,
                sequenceValue,
                sequenceCalled));
        }

        await sourceTransaction.CommitAsync(ct);

        await using var destinationTransaction =
            await destination.BeginTransactionAsync(ct);

        await using (var truncate = new NpgsqlCommand(
            """
            TRUNCATE TABLE
                public.payment,
                public.orderitem,
                public.orders,
                public.product,
                public.customer;
            """,
            destination,
            destinationTransaction))
        {
            truncate.CommandTimeout = options.Value.SyncTimeoutSeconds;
            await truncate.ExecuteNonQueryAsync(ct);
        }

        foreach (var snapshot in snapshots)
        {
            var table = snapshot.Table.Name;

            var sql = $"""
                INSERT INTO public.{table}
                SELECT *
                FROM jsonb_populate_recordset(
                    NULL::public.{table},
                    @rows
                );
                """;

            await using var insert = new NpgsqlCommand(
                sql, destination, destinationTransaction)
            {
                CommandTimeout = options.Value.SyncTimeoutSeconds
            };

            insert.Parameters.Add(
                new NpgsqlParameter("rows", NpgsqlDbType.Jsonb)
                {
                    Value = snapshot.Rows
                });

            await insert.ExecuteNonQueryAsync(ct);

            if (!snapshot.Table.HasIdentity)
            {
                continue;
            }

            var sequenceSql = $"""
                SELECT setval(
                    pg_get_serial_sequence(@table, 'id'),
                    GREATEST(
                        @value,
                        COALESCE(
                            (SELECT MAX(id)::bigint FROM public.{table}),
                            1
                        )
                    ),
                    @called OR EXISTS(
                        SELECT 1 FROM public.{table}
                    )
                );
                """;

            await using var sequenceCommand = new NpgsqlCommand(
                sequenceSql,
                destination,
                destinationTransaction);

            sequenceCommand.Parameters.AddWithValue(
                "table", $"public.{table}");

            sequenceCommand.Parameters.AddWithValue(
                "value", snapshot.SequenceValue);

            sequenceCommand.Parameters.AddWithValue(
                "called", snapshot.SequenceCalled);

            await sequenceCommand.ExecuteScalarAsync(ct);
        }

        await destinationTransaction.CommitAsync(ct);
    }

    private sealed record TableDefinition(
        string Name,
        bool HasIdentity);

    private sealed record TableSnapshot(
        TableDefinition Table,
        string Rows,
        long SequenceValue,
        bool SequenceCalled);
}