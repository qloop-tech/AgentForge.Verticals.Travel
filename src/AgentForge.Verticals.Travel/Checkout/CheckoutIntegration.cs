using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentForge.Verticals.Travel.Checkout;

public static class CheckoutIntegration
{
    public static void Configure(IHostApplicationBuilder builder) => builder.AddNpgsqlDbContext<TravelCheckoutDbContext>("vertical-db",
        settings => { settings.DisableRetry = true; settings.DisableTracing = false; settings.DisableMetrics = false; settings.DisableHealthChecks = false; },
        options => { options.UseNpgsql(o => o.MigrationsHistoryTable("__travel_checkout_migrations")); options.EnableSensitiveDataLogging(false); });
}
public sealed class CheckoutSchemaInitializer(IServiceScopeFactory scopes) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TravelCheckoutDbContext>().Database.MigrateAsync(ct);
    }
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
public sealed class TravelCheckoutGuard(IServiceScopeFactory scopes)
{
    public async Task EnsureReleasableAsync(string scopeKey, string reference, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<TravelCheckoutDbContext>();
        if (await db.Attempts.AnyAsync(x => x.ScopeKey == scopeKey && x.HoldReference == reference && x.Status != "failed" && x.Status != "cancelled" && x.Status != "expired", ct))
            throw new InvalidOperationException("This hold is bound to checkout/payment. Reconcile or cancel checkout before releasing inventory.");
    }
    public async Task<HashSet<string>> BookedAsync(string scopeKey, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<TravelCheckoutDbContext>();
        return (await db.Bookings.Where(x => x.ScopeKey == scopeKey).Select(x => x.HoldReference).ToListAsync(ct)).ToHashSet();
    }
}
