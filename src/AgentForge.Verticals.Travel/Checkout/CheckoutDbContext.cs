using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using System.Text.Json;

namespace AgentForge.Verticals.Travel.Checkout;

public sealed class CheckoutAttempt
{
    public string Id { get; set; } = "";

    public string ScopeKey { get; set; } = "";
    public string PartitionKey { get; set; } = "";
    public string HoldReference { get; set; } = "";
    public string QuoteReference { get; set; } = "";
    public string QuoteJson { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string ChatId { get; set; } = "";
    public long Amount { get; set; }
    public string Currency { get; set; } = "INR";
    public string Status { get; set; } = "creating";
    public string? ProviderLinkId { get; set; }
    public string? Url { get; set; }
    public string? PaymentId { get; set; }
    public string NotificationStatus { get; set; } = "pending";
    public string? ReviewReason { get; set; }
    public DateTimeOffset ReconcileAfter { get; set; }
    public DateTimeOffset Deadline { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
public sealed class CheckoutPaymentEvent
{
    public string Id { get; set; } = "";
    public string PartitionKey { get; set; } = "";
    public string LinkId { get; set; } = "";
    public string Reference { get; set; } = "";
    public string? PaymentId { get; set; }
    public string Kind { get; set; } = "";
    public string Status { get; set; } = "pending";
    public string BodyHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
public sealed class TravelBooking
{
    public string Reference { get; set; } = "";
    public string CheckoutId { get; set; } = "";
    public string ScopeKey { get; set; } = "";
    public string HoldReference { get; set; } = "";
    public string QuoteReference { get; set; } = "";
    public long PaidAmount { get; set; }
    public string Currency { get; set; } = "INR";
    public string PaymentId { get; set; } = "";
    public DateTimeOffset ConfirmedAt { get; set; }
    public string NotificationStatus { get; set; } = "pending";
}
// These existing tables are mapped only for checkout integration. Their schema/stores are not migrated here.
public sealed class CheckoutHold
{
    public string Reference { get; set; } = "";
    public string ScopeKey { get; set; } = "";
    public string InventoryKey { get; set; } = "";
    public string QuoteReference { get; set; } = "";
    public int PartySize { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string State { get; set; } = "";
    public string QuoteJson { get; set; } = "";
}
public sealed class CheckoutJourney
{
    public string ScopeKey { get; set; } = "";
    public string Body { get; set; } = "";
}
public sealed class TravelCheckoutDbContext(DbContextOptions<TravelCheckoutDbContext> options) : DbContext(options)
{
    public DbSet<CheckoutAttempt> Attempts => Set<CheckoutAttempt>();
    public DbSet<CheckoutPaymentEvent> Events => Set<CheckoutPaymentEvent>();
    public DbSet<TravelBooking> Bookings => Set<TravelBooking>();
    public DbSet<CheckoutHold> Holds => Set<CheckoutHold>();
    public DbSet<CheckoutJourney> Journeys => Set<CheckoutJourney>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<CheckoutAttempt>(e => { e.ToTable("travel_checkout_attempts"); e.HasKey(x => x.Id); e.HasIndex(x => x.HoldReference).IsUnique(); e.Property(x => x.QuoteJson).HasColumnType("jsonb"); e.HasIndex(x => new { x.PartitionKey, x.Status }); });
        model.Entity<CheckoutPaymentEvent>(e => { e.ToTable("travel_payment_events"); e.HasKey(x => x.Id); e.HasIndex(x => new { x.PartitionKey, x.Status }); });
        model.Entity<TravelBooking>(e => { e.ToTable("travel_bookings"); e.HasKey(x => x.Reference); e.HasIndex(x => x.PaymentId).IsUnique(); e.HasIndex(x => x.CheckoutId).IsUnique(); e.HasIndex(x => x.HoldReference).IsUnique(); });
        model.Entity<CheckoutHold>(e => { e.ToTable("travel_holds", t => t.ExcludeFromMigrations()); e.HasKey(x => x.Reference); e.Property(x => x.QuoteJson).HasColumnType("jsonb"); });
        model.Entity<CheckoutJourney>(e => { e.ToTable("travel_journeys", t => t.ExcludeFromMigrations()); e.HasKey(x => x.ScopeKey); e.Property(x => x.Body).HasColumnType("jsonb"); });
        foreach (var entity in model.Model.GetEntityTypes()) foreach (var property in entity.GetProperties()) property.SetColumnName(JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name));
        model.Entity<CheckoutHold>().Property(x => x.QuoteJson).HasColumnName("quote");
    }
}
public sealed class TravelCheckoutDesignFactory : IDesignTimeDbContextFactory<TravelCheckoutDbContext>
{
    public TravelCheckoutDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<TravelCheckoutDbContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable("AGENTFORGE_EF_DESIGN_CONNECTION") ?? "Host=localhost;Database=verticals;Username=postgres", o => o.MigrationsHistoryTable("__travel_checkout_migrations")).Options);
}
