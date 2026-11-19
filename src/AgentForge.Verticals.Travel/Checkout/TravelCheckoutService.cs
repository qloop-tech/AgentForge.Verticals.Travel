using System.Text.Json;
using AgentForge.Verticals.Travel.Journey;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentForge.Verticals.Travel.Checkout;

public sealed record CheckoutView(string Reference, string HoldReference, string Status, long AmountMinor, string Currency,
    DateTimeOffset Deadline, string? PaymentUrl, string? BookingReference, string DeadlineDisplay, string Notice);

/// <summary>Durable payment creation claims precede HTTP. Scope and inventory locks match the hold service.</summary>
public sealed class TravelCheckoutService(IServiceScopeFactory scopes, TravelInvocationScope identity, ITravelPaymentProvider provider, IConfiguration? configuration = null)
{
    private readonly TimeZoneInfo timeZone = TimeZoneInfo.FindSystemTimeZoneById(configuration?["TravelCustomerProfile:BusinessHours:TimeZone"] ?? "UTC");
    private TravelCheckoutDbContext Db(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<TravelCheckoutDbContext>();
    internal static Task Gate(TravelCheckoutDbContext db, string key, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key},0))", ct);
    internal static Task<DateTimeOffset> Now(TravelCheckoutDbContext db, CancellationToken ct) =>
        db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);

    public async Task<CheckoutView> CreateAsync(string holdReference, decimal expectedTotal, string currency, CancellationToken ct)
    {
        identity.RequireApprovedCall("create_travel_checkout", new { holdReference, expectedTotal, currency });
        var key = identity.Key(); CheckoutAttempt attempt;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = Db(scope); await using var tx = await db.Database.BeginTransactionAsync(ct); await Gate(db, key, ct);
            var existing = await db.Attempts.SingleOrDefaultAsync(x => x.HoldReference == holdReference && x.ScopeKey == key, ct);
            if (existing is not null)
            {
                if (existing.Amount != Minor(expectedTotal) || existing.Currency != currency) throw new JourneyConflictException("Checkout arguments differ from the original request.");
                return await View(db, existing, ct);
            }
            var hold = await db.Holds.SingleOrDefaultAsync(x => x.Reference == holdReference && x.ScopeKey == key, ct)
                ?? throw new InvalidOperationException("Hold not found in this conversation.");
            await Gate(db, hold.InventoryKey, ct); var now = await Now(db, ct);
            if (hold.State != "active" || hold.ExpiresAt <= now) throw new JourneyConflictException("The hold expired or was released. Request a fresh hold and approval first.");
            var quote = JsonSerializer.Deserialize<TravelQuote>(hold.QuoteJson)!;
            var row = await db.Journeys.SingleAsync(x => x.ScopeKey == key, ct); var journey = JsonSerializer.Deserialize<TravelJourney>(row.Body)!;
            if (!journey.Quotes.Any(x => x.Reference == quote.Reference) || !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(quote.Requirements), JsonSerializer.SerializeToElement(journey.Requirements)))
                throw new JourneyConflictException("The held quote no longer matches the inquiry.");
            if (quote.Total != expectedTotal || quote.Currency != currency) throw new JourneyConflictException("Approved payment amount/currency does not match the held quote.");
            var address = identity.Address();
            // Round down to provider seconds: the link must never outlive the inventory allocation.
            hold.ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(now.AddMinutes(30).ToUnixTimeSeconds());
            attempt = new()
            {
                Id = "CO-" + Guid.NewGuid().ToString("N"),
                ScopeKey = key,
                PartitionKey = identity.PartitionKey(),
                HoldReference = hold.Reference,
                QuoteReference = quote.Reference,
                QuoteJson = hold.QuoteJson,
                AccountId = address.Account,
                ChatId = address.Chat,
                Amount = Minor(expectedTotal),
                Currency = currency,
                Deadline = hold.ExpiresAt,
                CreatedAt = now,
                ReconcileAfter = now.AddMinutes(1)
            };
            db.Attempts.Add(attempt); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
        // Never retry this POST automatically. A timeout may mean the provider already created the link.
        try { var link = await provider.CreateAsync(attempt, ct); await SaveLinkAsync(attempt.Id, attempt.PartitionKey, link, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PaymentProviderException or InvalidOperationException or JsonException)
        {
            await using var scope = scopes.CreateAsyncScope(); var db = Db(scope);
            var row = await db.Attempts.SingleAsync(x => x.Id == attempt.Id, CancellationToken.None);
            if (row.Status == "creating") row.Status = ex is PaymentProviderException { Status: >= 400 and < 500 and not 429 } ? "failed" : "needs_reconciliation";
            await db.SaveChangesAsync(CancellationToken.None);
        }
        await using (var scope = scopes.CreateAsyncScope()) { var db = Db(scope); return await View(db, await db.Attempts.SingleAsync(x => x.Id == attempt.Id, ct), ct); }
    }
    public static long Minor(decimal total)
    {
        if (total <= 0 || decimal.Truncate(total * 100) != total * 100) throw new ArgumentException("Payment amount must be positive with at most two decimal places.");
        return checked((long)(total * 100));
    }
    public async Task<CheckoutView[]> GetAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope(); var db = Db(scope); var key = identity.Key();
        var attempts = await db.Attempts.Where(x => x.ScopeKey == key).OrderByDescending(x => x.CreatedAt).Take(10).ToListAsync(ct);
        var views = new List<CheckoutView>(); foreach (var a in attempts) views.Add(await View(db, a, ct)); return [.. views];
    }
    private async Task<CheckoutView> View(TravelCheckoutDbContext db, CheckoutAttempt a, CancellationToken ct)
    {
        var booking = await db.Bookings.SingleOrDefaultAsync(x => x.CheckoutId == a.Id, ct); var now = await Now(db, ct);
        var state = booking is not null ? "paid" : a.Status == "ready" && a.Deadline <= now ? "expired" : a.Status;
        return new(a.Id, a.HoldReference, state, a.Amount, a.Currency, a.Deadline, state == "ready" ? a.Url : null, booking?.Reference,
            TimeZoneInfo.ConvertTime(a.Deadline, timeZone).ToString("dd MMM yyyy, HH:mm zzz", System.Globalization.CultureInfo.InvariantCulture) + $" ({timeZone.Id})",
            booking is not null ? "Confirmed DEMO booking; test payment only." : state == "ready" ? "Test payment link. Full quoted amount; no real money. Pay before the deadline." :
            state == "payment_review" ? "Payment needs human reconciliation; no booking is confirmed." : "No booking is confirmed. Never create another link for this hold; check its status or contact the operator.");
    }
    private static bool LinkMatches(CheckoutAttempt a, ProviderLink link) => link.Reference == a.Id && link.Amount == a.Amount && link.Currency == a.Currency && link.ExpiresAt == a.Deadline.ToUnixTimeSeconds();
    private async Task SaveLinkAsync(string id, string partition, ProviderLink link, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope(); var db = Db(scope); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var a = await db.Attempts.SingleAsync(x => x.Id == id && x.PartitionKey == partition, ct); await Gate(db, a.ScopeKey, ct);
        await db.Entry(a).ReloadAsync(ct);
        if (!LinkMatches(a, link)) throw new InvalidOperationException("Provider link does not match the approved checkout.");
        if (a.ProviderLinkId is not null && a.ProviderLinkId != link.Id) throw new InvalidOperationException("Conflicting payment links require operator review.");
        a.ProviderLinkId = link.Id; a.Url = link.Url; if (a.Status is "creating" or "needs_reconciliation") a.Status = "ready";
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    // Called by the plugin's durable webhook/polling processor, never with model-supplied payment identity.
    public async Task ReconcileAsync(string id, string partition, string? eventPaymentId, CancellationToken ct)
    {
        CheckoutAttempt a;
        await using (var scope = scopes.CreateAsyncScope()) a = await Db(scope).Attempts.AsNoTracking().SingleAsync(x => x.Id == id && x.PartitionKey == partition, ct);
        if (a.Status is "failed" or "paid" or "payment_review") return;
        var link = a.ProviderLinkId is null ? await provider.FindAsync(a.Id, ct) : await provider.GetLinkAsync(a.ProviderLinkId, ct);
        if (link is null) return;
        await SaveLinkAsync(id, partition, link, ct);
        if (link.Status is "cancelled" or "expired" && link.Paid == 0)
        {
            await using var terminalScope = scopes.CreateAsyncScope();
            var terminalDb = Db(terminalScope);
            await using var terminalTx = await terminalDb.Database.BeginTransactionAsync(ct);
            await Gate(terminalDb, a.ScopeKey, ct);
            var terminal = await terminalDb.Attempts.SingleAsync(x => x.Id == id && x.PartitionKey == partition, ct);
            if (terminal.Status is "ready" or "creating" or "needs_reconciliation") terminal.Status = link.Status;
            await terminalDb.SaveChangesAsync(ct);
            await terminalTx.CommitAsync(ct);
            return;
        }
        if (link.Status != "paid") return;
        var paymentId = eventPaymentId ?? link.PaymentId;
        if (paymentId is null) return; // provider may expose payment details after the paid status
        var payment = await provider.GetPaymentAsync(paymentId, ct);
        if (payment.Status != "captured") return;
        var bound = link.OrderId is not null ? payment.OrderId == link.OrderId : link.PaymentId == payment.Id;
        if (!LinkMatches(a, link) || link.Paid != a.Amount || payment.Id != paymentId || payment.Amount != a.Amount || payment.Currency != a.Currency || !bound)
            throw new InvalidOperationException("Captured payment does not match this checkout.");
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = Db(scope); await using var tx = await db.Database.BeginTransactionAsync(ct); await Gate(db, a.ScopeKey, ct);
            a = await db.Attempts.SingleAsync(x => x.Id == id && x.PartitionKey == partition, ct);
            if (await db.Bookings.AnyAsync(x => x.CheckoutId == id, ct)) return;
            var hold = await db.Holds.SingleAsync(x => x.Reference == a.HoldReference && x.ScopeKey == a.ScopeKey, ct);
            await Gate(db, hold.InventoryKey, ct); var now = await Now(db, ct);
            a.PaymentId = payment.Id;
            if (hold.State != "active" || hold.ExpiresAt <= now || a.Deadline <= now)
            { a.Status = "payment_review"; a.ReviewReason = "Payment verified after inventory deadline or release. Operator reconciliation required."; }
            else
            {
                var quote = JsonSerializer.Deserialize<TravelQuote>(a.QuoteJson)!;
                // Retain the allocation through the trip; existing capacity queries already count active allocations.
                hold.ExpiresAt = new DateTimeOffset(quote.StartDate.AddDays((quote.Requirements.Days ?? 5) + 1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
                a.Status = "paid"; db.Bookings.Add(new()
                {
                    Reference = "DEMO-" + Guid.NewGuid().ToString("N"),
                    CheckoutId = a.Id,
                    ScopeKey = a.ScopeKey,
                    HoldReference = a.HoldReference,
                    QuoteReference = a.QuoteReference,
                    PaidAmount = a.Amount,
                    Currency = a.Currency,
                    PaymentId = payment.Id,
                    ConfirmedAt = now
                });
            }
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
    }
}
