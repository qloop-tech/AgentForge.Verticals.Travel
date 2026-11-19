using System.Text.Json;
using AgentForge.Verticals.Travel.Journey;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentForge.Verticals.Travel.Checkout;

/// <summary>Durable webhook inbox plus read-only provider reconciliation; notification delivery observes Core ownership.</summary>
public sealed class CheckoutProcessor(IServiceScopeFactory scopes, TravelCheckoutService checkout, IConfiguration config,
    IServiceProvider services, ILogger<CheckoutProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Checkout reconciliation deferred ({ErrorType}).", ex.GetType().Name); }
        }
    }
    public async Task TickAsync(CancellationToken ct)
    {
        var partition = TravelInvocationScope.ConfiguredPartition(config);
        await using var scope = scopes.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<TravelCheckoutDbContext>();
        var events = await db.Events.Where(x => x.PartitionKey == partition && x.Status == "pending").OrderBy(x => x.CreatedAt).Take(20).ToListAsync(ct);
        foreach (var evt in events)
        {
            var attempt = await db.Attempts.AsNoTracking().SingleAsync(x => x.Id == evt.Reference && x.PartitionKey == partition, ct);
            if (attempt.ProviderLinkId is not null && attempt.ProviderLinkId != evt.LinkId) { evt.Status = "rejected"; continue; }
            try { await checkout.ReconcileAsync(attempt.Id, partition, evt.PaymentId, ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PaymentProviderException or InvalidOperationException)
            { logger.LogWarning("Payment event {EventReference} deferred ({ErrorType}).", evt.Id, ex.GetType().Name); continue; }
            var status = await db.Attempts.AsNoTracking().Where(x => x.Id == attempt.Id).Select(x => x.Status).SingleAsync(ct);
            if (status is "paid" or "payment_review" or "failed") evt.Status = "processed";
        }
        await db.SaveChangesAsync(ct);
        var now = await TravelCheckoutService.Now(db, ct);
        var pending = await db.Attempts.AsNoTracking().Where(x => x.PartitionKey == partition &&
            (x.Status == "ready" || x.Status == "needs_reconciliation" || x.Status == "creating") && x.ReconcileAfter <= now)
            .OrderBy(x => x.ReconcileAfter).Take(20).ToListAsync(ct);
        foreach (var a in pending)
        {
            try { await checkout.ReconcileAsync(a.Id, partition, null, ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or PaymentProviderException or InvalidOperationException)
            { logger.LogWarning("Checkout {CheckoutReference} awaits provider reconciliation ({ErrorType}).", a.Id, ex.GetType().Name); }
            await db.Attempts.Where(x => x.Id == a.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.ReconcileAfter, now.AddSeconds(a.Deadline < now ? 300 : 30)), ct);
        }
        var sender = services.GetKeyedService<Func<string, string, string, string, CancellationToken, Task<string>>>("agentforge.send-notification");
        if (sender is null) return;
        var notices = await db.Attempts.Where(x => x.PartitionKey == partition && (x.Status == "paid" || x.Status == "payment_review") &&
            (x.NotificationStatus == "pending" || x.NotificationStatus == "paused")).Take(20).ToListAsync(ct);
        foreach (var a in notices)
        {
            var booking = await db.Bookings.SingleOrDefaultAsync(x => x.CheckoutId == a.Id, ct);
            var quote = JsonSerializer.Deserialize<TravelQuote>(a.QuoteJson)!;
            var text = booking is null ? "Your checkout needs human reconciliation. No booking is confirmed. Please do not pay again; a human operator must review the payment and availability." :
                $"*DEMO booking confirmed*\nReference: {booking.Reference}\nKerala departure: {quote.StartDate:dd MMM yyyy}\nTravellers: {quote.PartySize}\nHotel: {quote.Requirements.HotelStars}-star; houseboat: {(quote.Requirements.Houseboat == true ? "included" : "not included")}\nPaid: {a.Currency} {a.Amount / 100m:N2} (Razorpay TEST mode)\nThis is a demonstration booking using synthetic inventory; no real travel reservation was made.";
            a.NotificationStatus = await sender(a.AccountId, a.ChatId, text, "checkout:" + a.Id, ct);
            if (booking is not null) booking.NotificationStatus = a.NotificationStatus;
            await db.SaveChangesAsync(ct);
        }
    }
}
