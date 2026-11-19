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
    internal NpgsqlDataSource Database => _database;
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
            CREATE TABLE IF NOT EXISTS travel_holds (
                reference text PRIMARY KEY, scope_key text NOT NULL, inventory_key text NOT NULL,
                quote_reference text NOT NULL, party_size integer NOT NULL CHECK(party_size>0),
                expires_at timestamptz NOT NULL, state text NOT NULL CHECK(state IN ('active','released')),
                operation_id text NOT NULL, request_hash text NOT NULL, quote jsonb NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now(), UNIQUE(scope_key,operation_id));
            CREATE INDEX IF NOT EXISTS ix_travel_holds_inventory ON travel_holds(inventory_key,state,expires_at);
            CREATE INDEX IF NOT EXISTS ix_travel_holds_scope ON travel_holds(scope_key,state,expires_at);
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
        await using (var held = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM travel_holds WHERE scope_key=$1 AND state='active' AND expires_at>clock_timestamp())", connection, transaction))
        {
            held.Parameters.AddWithValue(scope);
            if (await held.ExecuteScalarAsync(ct) is true) throw new JourneyConflictException("Release the active hold before changing requirements or quotes.");
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
