using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;
using NpgsqlTypes;

namespace AgentForge.Verticals.Travel.Journey;

/// <summary>Travel-owned schema. An advisory transaction lock serializes commands for one scoped aggregate.</summary>
public sealed class TravelJourneyStore(IConfiguration configuration) : IAsyncDisposable, IDisposable
{
    private readonly NpgsqlDataSource _database = NpgsqlDataSource.Create(configuration.GetConnectionString("vertical-db")
        ?? throw new InvalidOperationException("ConnectionStrings:vertical-db is required for durable travel records."));
    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var connection = await _database.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock(hashtextextended('AgentForge.Travel.Schema.v1', 0));
            CREATE TABLE IF NOT EXISTS travel_journeys (
                scope_key text PRIMARY KEY, inquiry_id text NOT NULL, revision integer NOT NULL,
                body jsonb NOT NULL, updated_at timestamptz NOT NULL DEFAULT now());
            CREATE TABLE IF NOT EXISTS travel_operations (
                scope_key text NOT NULL, operation_key text NOT NULL, request_hash text NOT NULL,
                result jsonb NOT NULL, created_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY(scope_key, operation_key));
            CREATE TABLE IF NOT EXISTS travel_quote_versions (
                scope_key text NOT NULL, reference text NOT NULL, version integer NOT NULL,
                body jsonb NOT NULL, created_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY(scope_key, reference));
            """, connection, transaction);
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }
    public async Task<TravelJourney?> GetAsync(string scope, CancellationToken ct)
    {
        await using var command = _database.CreateCommand("SELECT body::text FROM travel_journeys WHERE scope_key=$1");
        command.Parameters.AddWithValue(scope);
        return await command.ExecuteScalarAsync(ct) is string json ? JsonSerializer.Deserialize<TravelJourney>(json) : null;
    }
    public async Task<TravelJourney> ExecuteAsync(string scope, string operation, string payload, int expectedRevision,
        Func<TravelJourney?, TravelJourney> change, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(operation) || operation.Length > 100) throw new ArgumentException("Operation ID must contain 1–100 characters.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        await using var connection = await _database.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", connection, transaction))
        { gate.Parameters.AddWithValue(scope); await gate.ExecuteNonQueryAsync(ct); }
        await using (var prior = new NpgsqlCommand("SELECT request_hash, result::text FROM travel_operations WHERE scope_key=$1 AND operation_key=$2", connection, transaction))
        {
            prior.Parameters.AddWithValue(scope); prior.Parameters.AddWithValue(operation);
            await using var reader = await prior.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                if (reader.GetString(0) != hash) throw new JourneyConflictException("Operation ID already used with different input.");
                return JsonSerializer.Deserialize<TravelJourney>(reader.GetString(1))!;
            }
        }
        TravelJourney? current;
        await using (var select = new NpgsqlCommand("SELECT body::text FROM travel_journeys WHERE scope_key=$1", connection, transaction))
        { select.Parameters.AddWithValue(scope); current = await select.ExecuteScalarAsync(ct) is string json ? JsonSerializer.Deserialize<TravelJourney>(json) : null; }
        if ((current?.Revision ?? 0) != expectedRevision) throw new JourneyConflictException("Stale inquiry revision. Retrieve the current journey and retry with a new operation ID.");
        var next = change(current);
        var serialized = JsonSerializer.Serialize(next);
        await using (var save = new NpgsqlCommand("""
            INSERT INTO travel_journeys(scope_key,inquiry_id,revision,body) VALUES($1,$2,$3,$4)
            ON CONFLICT(scope_key) DO UPDATE SET revision=excluded.revision, body=excluded.body, updated_at=now()
            """, connection, transaction))
        { save.Parameters.AddWithValue(scope); save.Parameters.AddWithValue(next.InquiryId); save.Parameters.AddWithValue(next.Revision); save.Parameters.AddWithValue(NpgsqlDbType.Jsonb, serialized); await save.ExecuteNonQueryAsync(ct); }
        foreach (var quote in next.Quotes.Skip(current?.Quotes.Length ?? 0))
        {
            await using var save = new NpgsqlCommand("INSERT INTO travel_quote_versions(scope_key,reference,version,body) VALUES($1,$2,$3,$4)", connection, transaction);
            save.Parameters.AddWithValue(scope); save.Parameters.AddWithValue(quote.Reference); save.Parameters.AddWithValue(quote.Version);
            save.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(quote)); await save.ExecuteNonQueryAsync(ct);
        }
        await using (var receipt = new NpgsqlCommand("INSERT INTO travel_operations(scope_key,operation_key,request_hash,result) VALUES($1,$2,$3,$4)", connection, transaction))
        { receipt.Parameters.AddWithValue(scope); receipt.Parameters.AddWithValue(operation); receipt.Parameters.AddWithValue(hash); receipt.Parameters.AddWithValue(NpgsqlDbType.Jsonb, serialized); await receipt.ExecuteNonQueryAsync(ct); }
        await transaction.CommitAsync(ct);
        return next;
    }
    public void Dispose() => _database.Dispose();
    public ValueTask DisposeAsync() => _database.DisposeAsync();
}
public sealed class TravelSchemaInitializer(TravelJourneyStore store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => store.InitializeAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
