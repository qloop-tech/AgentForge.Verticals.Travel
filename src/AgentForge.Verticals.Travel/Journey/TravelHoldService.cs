using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Microsoft.Extensions.Configuration;
using NpgsqlTypes;

namespace AgentForge.Verticals.Travel.Journey;

/// <summary>Database-clock expiry and inventory-level locks apply across all scoped conversations.</summary>
public sealed class TravelHoldService(TravelJourneyStore store, TravelScenarioCatalog catalog, TravelInvocationScope scope, IConfiguration? configuration = null)
{
    private const int HoldMinutes = 15;
    private readonly TimeZoneInfo _timeZone = TimeZoneInfo.FindSystemTimeZoneById(configuration?["TravelCustomerProfile:BusinessHours:TimeZone"] ?? "UTC");
    public async Task<TravelHold[]> GetAsync(CancellationToken ct)
    {
        await using var command = store.Database.CreateCommand("""
            SELECT reference,quote_reference,party_size,expires_at,
                CASE WHEN state='released' THEN 'released' WHEN expires_at<=clock_timestamp() THEN 'expired' ELSE 'active' END,
                quote::text FROM travel_holds WHERE scope_key=$1 ORDER BY created_at DESC LIMIT 20
            """);
        command.Parameters.AddWithValue(scope.Key());
        List<TravelHold> results = [];
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) results.Add(Read(reader));
        return [.. results];
    }
    public async Task<TravelAvailability[]> AvailabilityAsync(CancellationToken ct)
    {
        List<TravelAvailability> result = [];
        foreach (var offer in catalog.Scenario.Offers.Where(o => o.Kind == "fixed"))
        {
            await using var command = store.Database.CreateCommand("SELECT COALESCE(SUM(party_size),0)::integer FROM travel_holds WHERE inventory_key=$1 AND state='active' AND expires_at>clock_timestamp()");
            command.Parameters.AddWithValue(scope.InventoryKey(catalog.Scenario.Version, offer.Id));
            var held = (int)(await command.ExecuteScalarAsync(ct))!;
            result.Add(new(offer.Id, offer.AvailableSlots!.Value, held, Math.Max(0, offer.AvailableSlots.Value - held)));
        }
        return [.. result];
    }
    public async Task<TravelHoldResult> HoldAsync(string quoteReference, int expectedRevision, string operationId, decimal expectedTotal, string currency, int partySize, string startDate, CancellationToken ct)
    {
        scope.RequireApprovedCall("hold_travel_quote", new { quoteReference, expectedRevision, operationId, expectedTotal, currency, partySize, startDate });
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 100) throw new ArgumentException("Operation ID must contain 1–100 characters.");
        var scopeKey = scope.Key();
        var requestHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { quoteReference, expectedRevision, expectedTotal, currency, partySize, startDate }))));
        await using var connection = await store.Database.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await GateAsync(connection, transaction, scopeKey, ct);
        await using (var replay = new NpgsqlCommand("""
            SELECT reference,quote_reference,party_size,expires_at,
                CASE WHEN state='released' THEN 'released' WHEN expires_at<=clock_timestamp() THEN 'expired' ELSE 'active' END,
                quote::text,request_hash FROM travel_holds WHERE scope_key=$1 AND operation_id=$2
            """, connection, transaction))
        {
            replay.Parameters.AddWithValue(scopeKey); replay.Parameters.AddWithValue(operationId);
            await using var reader = await replay.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                if (reader.GetString(6) != requestHash) throw new JourneyConflictException("Hold operation ID reused with different arguments.");
                return new(Read(reader)); // never renew an expired/released hold under an old operation ID
            }
        }
        TravelJourney journey;
        await using (var get = new NpgsqlCommand("SELECT body::text FROM travel_journeys WHERE scope_key=$1", connection, transaction))
        {
            get.Parameters.AddWithValue(scopeKey);
            journey = await get.ExecuteScalarAsync(ct) is string json ? JsonSerializer.Deserialize<TravelJourney>(json)! : throw new InvalidOperationException("No saved inquiry.");
        }
        if (journey.Revision != expectedRevision) throw new JourneyConflictException("The inquiry changed. Retrieve it and request fresh approval.");
        var quote = journey.Quotes.SingleOrDefault(q => q.Reference == quoteReference) ?? throw new InvalidOperationException("Quote not found in this conversation.");
        if (quote.Requirements is null || !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(quote.Requirements), JsonSerializer.SerializeToElement(journey.Requirements)))
            throw new JourneyConflictException("This quote is stale. Create a revised quote before requesting a hold.");
        if (quote.Total != expectedTotal || quote.Currency != currency || quote.PartySize != partySize || quote.StartDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) != startDate)
            throw new JourneyConflictException("Approved amount, currency, party or date does not match the saved quote.");
        var scenario = catalog.Scenario;
        if (quote.InventoryVersion != scenario.Version) throw new JourneyConflictException("Inventory version changed. Requote before approval.");
        var offer = scenario.Offers.Single(o => o.Id == quote.OfferId);
        if (offer.Kind != "fixed") throw new InvalidOperationException("Custom offers need supplier confirmation; no fixed slots can be held for them.");
        var recalculated = TravelJourneyService.Calculate(journey, offer, scenario) with { Reference = quote.Reference, Version = quote.Version, InquiryRevision = quote.InquiryRevision };
        if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(quote), JsonSerializer.SerializeToElement(recalculated)))
            throw new JourneyConflictException("The saved price or offer changed. Requote before approval.");
        var inventoryKey = scope.InventoryKey(scenario.Version, offer.Id);
        await GateAsync(connection, transaction, inventoryKey, ct);
        await using (var check = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM travel_holds WHERE scope_key=$1 AND state='active' AND expires_at>clock_timestamp()),
                COALESCE((SELECT SUM(party_size) FROM travel_holds WHERE inventory_key=$2 AND state='active' AND expires_at>clock_timestamp()),0)::integer,
                clock_timestamp()
            """, connection, transaction))
        {
            check.Parameters.AddWithValue(scopeKey); check.Parameters.AddWithValue(inventoryKey);
            await using var reader = await check.ExecuteReaderAsync(ct); await reader.ReadAsync(ct);
            if (reader.GetBoolean(0)) throw new JourneyConflictException("This conversation already has an active hold.");
            if (quote.PartySize > offer.AvailableSlots!.Value - reader.GetInt32(1)) throw new JourneyConflictException("Insufficient demo slots remain for this party.");
            if (quote.StartDate < DateOnly.FromDateTime(reader.GetDateTime(2))) throw new JourneyConflictException("Departure date has passed.");
        }
        TravelHold hold;
        await using (var save = new NpgsqlCommand("""
            INSERT INTO travel_holds(reference,scope_key,inventory_key,quote_reference,party_size,expires_at,state,operation_id,request_hash,quote)
            VALUES($1,$2,$3,$4,$5,clock_timestamp()+make_interval(mins=>$6),'active',$7,$8,$9)
            RETURNING reference,quote_reference,party_size,expires_at,state,quote::text
            """, connection, transaction))
        {
            save.Parameters.AddWithValue($"HOLD-{Guid.NewGuid():N}"); save.Parameters.AddWithValue(scopeKey); save.Parameters.AddWithValue(inventoryKey);
            save.Parameters.AddWithValue(quote.Reference); save.Parameters.AddWithValue(quote.PartySize); save.Parameters.AddWithValue(HoldMinutes);
            save.Parameters.AddWithValue(operationId); save.Parameters.AddWithValue(requestHash); save.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(quote));
            await using var reader = await save.ExecuteReaderAsync(ct); await reader.ReadAsync(ct); hold = Read(reader);
        }
        await transaction.CommitAsync(ct);
        return new(hold);
    }
    public async Task<TravelHoldResult> ReleaseAsync(string holdReference, CancellationToken ct)
    {
        await using var connection = await store.Database.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var scopeKey = scope.Key(); await GateAsync(connection, transaction, scopeKey, ct);
        await using var update = new NpgsqlCommand("""
            UPDATE travel_holds SET state='released' WHERE reference=$1 AND scope_key=$2
            RETURNING reference,quote_reference,party_size,expires_at,state,quote::text
            """, connection, transaction);
        update.Parameters.AddWithValue(holdReference); update.Parameters.AddWithValue(scopeKey);
        TravelHold hold;
        await using (var reader = await update.ExecuteReaderAsync(ct))
        { if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Hold not found in this conversation."); hold = Read(reader); }
        await transaction.CommitAsync(ct); return new(hold);
    }
    private static async Task GateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string key, CancellationToken ct)
    { await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1,0))", connection, transaction); command.Parameters.AddWithValue(key); await command.ExecuteNonQueryAsync(ct); }
    private TravelHold Read(NpgsqlDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetFieldValue<DateTimeOffset>(3), reader.GetString(4), JsonSerializer.Deserialize<TravelQuote>(reader.GetString(5))!,
            TimeZoneInfo.ConvertTime(reader.GetFieldValue<DateTimeOffset>(3), _timeZone).ToString("dd MMM yyyy, HH:mm zzz", System.Globalization.CultureInfo.InvariantCulture) + $" ({_timeZone.Id})");
}
