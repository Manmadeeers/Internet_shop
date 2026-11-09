using System.Text.Json;
using Npgsql;

namespace InternetShop.MessageBroker;

public sealed class BrokerStore(IConfiguration configuration)
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web);

    private readonly string connectionString =
        configuration.GetConnectionString("Broker")
        ?? throw new InvalidOperationException(
            "ConnectionStrings:Broker is missing.");

    private volatile bool ready;

    public bool Ready => ready;

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction =
            await connection.BeginTransactionAsync(ct);

        await using (var command = Command(
            connection,
            "SELECT pg_advisory_xact_lock(2026110901);"))
        {
            await command.ExecuteNonQueryAsync(ct);
        }

        var sql = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "schema.sql"),
            ct);

        await using (var command = Command(connection, sql))
        {
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        ready = true;
    }

    public async Task PingAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, "SELECT 1;");
        await command.ExecuteScalarAsync(ct);
    }

    public async Task<PublishResult> PublishAsync(
        PublishRequest request,
        CancellationToken ct)
    {
        var envelopeJson = JsonSerializer.Serialize(request.Envelope, Json);

        await using var connection = await OpenAsync(ct);
        await using var transaction =
            await connection.BeginTransactionAsync(ct);

        int inserted;

        await using (var command = Command(
            connection,
            """
            INSERT INTO internetshop_broker.messages
                (event_id, routing_key, envelope)
            VALUES ($1, $2, $3::jsonb)
            ON CONFLICT (event_id) DO NOTHING;
            """,
            request.Envelope.EventId,
            request.RoutingKey,
            envelopeJson))
        {
            inserted = await command.ExecuteNonQueryAsync(ct);
        }

        if (inserted == 0)
        {
            await using (var command = Command(
                connection,
                """
                SELECT routing_key = $2 AND envelope = $3::jsonb
                FROM internetshop_broker.messages
                WHERE event_id = $1;
                """,
                request.Envelope.EventId,
                request.RoutingKey,
                envelopeJson))
            {
                var matches =
                    (bool)(await command.ExecuteScalarAsync(ct) ?? false);

                if (!matches)
                {
                    throw new BrokerHttpException(
                        409,
                        "This EventId already exists with different content.");
                }
            }

            await using var countCommand = Command(
                connection,
                """
                SELECT count(*)
                FROM internetshop_broker.deliveries
                WHERE event_id = $1;
                """,
                request.Envelope.EventId);

            var count = Convert.ToInt32(
                await countCommand.ExecuteScalarAsync(ct));

            await transaction.CommitAsync(ct);

            return new PublishResult(
                request.Envelope.EventId,
                count,
                Duplicate: true);
        }

        var queues = new HashSet<string>();

        await using (var command = Command(
            connection,
            """
            SELECT queue_name, pattern
            FROM internetshop_broker.bindings;
            """))
        {
            await using var reader =
                await command.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                if (TopicMatcher.Matches(
                    reader.GetString(1),
                    request.RoutingKey))
                {
                    queues.Add(reader.GetString(0));
                }
            }
        }

        if (queues.Count == 0)
        {
            throw new BrokerHttpException(
                422,
                "No queue binding matches this routing key.");
        }

        foreach (var queue in queues)
        {
            await using var command = Command(
                connection,
                """
                INSERT INTO internetshop_broker.deliveries
                    (queue_name, event_id)
                VALUES ($1, $2);
                """,
                queue,
                request.Envelope.EventId);

            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);

        return new PublishResult(
            request.Envelope.EventId,
            queues.Count,
            Duplicate: false);
    }

    public async Task EnsureQueueAsync(
        string queue,
        CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(
            connection,
            """
            SELECT EXISTS(
                SELECT 1
                FROM internetshop_broker.queues
                WHERE name = $1
            );
            """,
            queue);

        if (!(bool)(await command.ExecuteScalarAsync(ct) ?? false))
        {
            throw new BrokerHttpException(
                404,
                $"Queue '{queue}' does not exist.");
        }
    }

    public async Task<DeliveryResponse?> ReceiveAsync(
        string queue,
        int leaseSeconds,
        CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(
            connection,
            """
            WITH candidate AS
            (
                SELECT queue_name, event_id
                FROM internetshop_broker.deliveries
                WHERE queue_name = $1
                  AND state = 'ready'
                  AND available_at <= clock_timestamp()
                ORDER BY position
                LIMIT 1
                FOR UPDATE SKIP LOCKED
            ),
            claimed AS
            (
                UPDATE internetshop_broker.deliveries d
                SET state = 'inflight',
                    attempts = d.attempts + 1,
                    lease_token = $2,
                    lease_until =
                        clock_timestamp() + $3 * interval '1 second'
                FROM candidate c
                WHERE d.queue_name = c.queue_name
                  AND d.event_id = c.event_id
                RETURNING
                    d.event_id,
                    d.lease_token,
                    d.lease_until,
                    d.attempts
            )
            SELECT
                c.lease_token,
                c.lease_until,
                c.attempts,
                m.envelope::text
            FROM claimed c
            JOIN internetshop_broker.messages m
              ON m.event_id = c.event_id;
            """,
            queue,
            Guid.NewGuid(),
            leaseSeconds);

        await using var reader =
            await command.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new DeliveryResponse(
            reader.GetGuid(0),
            reader.GetFieldValue<DateTimeOffset>(1),
            reader.GetInt32(2),
            Deserialize(reader.GetString(3)));
    }

    public async Task<bool> AckAsync(
        string queue,
        Guid receipt,
        CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(
            connection,
            """
            UPDATE internetshop_broker.deliveries
            SET state = 'done',
                lease_until = NULL,
                completed_at = clock_timestamp()
            WHERE queue_name = $1
              AND lease_token = $2
              AND (
                  (state = 'inflight'
                   AND lease_until > clock_timestamp())
                  OR state = 'done'
              );
            """,
            queue,
            receipt);

        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> RejectAsync(
        string queue,
        Guid receipt,
        string error,
        CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(
            connection,
            """
            UPDATE internetshop_broker.deliveries
            SET state = CASE
                    WHEN attempts >= 3 THEN 'dead'
                    ELSE 'ready'
                END,
                available_at = clock_timestamp()
                    + CASE WHEN attempts = 1 THEN 2 ELSE 5 END
                    * interval '1 second',
                lease_token = NULL,
                lease_until = NULL,
                last_error = $3
            WHERE queue_name = $1
              AND lease_token = $2
              AND state = 'inflight'
              AND lease_until > clock_timestamp();
            """,
            queue,
            receipt,
            error);

        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task RecoverExpiredAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(
            connection,
            """
            UPDATE internetshop_broker.deliveries
            SET state = CASE
                    WHEN attempts >= 3 THEN 'dead'
                    ELSE 'ready'
                END,
                available_at = clock_timestamp()
                    + CASE WHEN attempts = 1 THEN 2 ELSE 5 END
                    * interval '1 second',
                lease_token = NULL,
                lease_until = NULL,
                last_error = 'Delivery lease expired.'
            WHERE state = 'inflight'
              AND lease_until <= clock_timestamp();
            """);

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<QueueStatistics>> StatisticsAsync(
        CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(
            connection,
            """
            SELECT
                q.name,
                count(d.event_id) FILTER (
                    WHERE d.state = 'ready'
                      AND d.available_at <= clock_timestamp()),
                count(d.event_id) FILTER (
                    WHERE d.state = 'ready'
                      AND d.available_at > clock_timestamp()),
                count(d.event_id) FILTER (WHERE d.state = 'inflight'),
                count(d.event_id) FILTER (WHERE d.state = 'done'),
                count(d.event_id) FILTER (WHERE d.state = 'dead')
            FROM internetshop_broker.queues q
            LEFT JOIN internetshop_broker.deliveries d
              ON d.queue_name = q.name
            GROUP BY q.name
            ORDER BY q.name;
            """);

        var result = new List<QueueStatistics>();
        await using var reader =
            await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            result.Add(new QueueStatistics(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5)));
        }

        return result;
    }

    public async Task<List<DeadLetter>> DeadLettersAsync(
        string queue,
        CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(
            connection,
            """
            SELECT
                d.event_id,
                d.attempts,
                d.last_error,
                m.envelope::text
            FROM internetshop_broker.deliveries d
            JOIN internetshop_broker.messages m
              ON m.event_id = d.event_id
            WHERE d.queue_name = $1
              AND d.state = 'dead'
            ORDER BY d.position DESC
            LIMIT 100;
            """,
            queue);

        var result = new List<DeadLetter>();
        await using var reader =
            await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            result.Add(new DeadLetter(
                queue,
                reader.GetGuid(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                Deserialize(reader.GetString(3))));
        }

        return result;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);

        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static NpgsqlCommand Command(
        NpgsqlConnection connection,
        string sql,
        params object[] values)
    {
        var command = new NpgsqlCommand(sql, connection);

        foreach (var value in values)
        {
            command.Parameters.Add(
                new NpgsqlParameter { Value = value });
        }

        return command;
    }

    private static EventEnvelope Deserialize(string json) =>
        JsonSerializer.Deserialize<EventEnvelope>(json, Json)
        ?? throw new InvalidDataException(
            "Stored event envelope is invalid.");
}
