using System.Security.Claims;
using System.Text.Json;
using AgentForge.Verticals.Travel.Journey;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace AgentForge.Verticals.Travel.Tests;

public sealed class TravelHoldTests
{
    public static bool Enabled => TravelJourneyPersistenceTests.Enabled;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact(SkipUnless = nameof(Enabled), Skip = "Requires isolated PostgreSQL test scope.")]
    public async Task Hold_requires_exact_approval_preserves_quotes_and_replays_without_extending_expiry()
    {
        await using var fixture = new Fixture(); await fixture.Store.InitializeAsync(Ct);
        var f = fixture.Services("one"); var quoted = await f.Journey.QuoteAsync("KERALA-FIXED", (await f.Journey.SaveAsync(Family(), 0, "save", Ct)).Revision, "quote", Ct);
        var quote = quoted.Quotes.Single();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Holds.HoldAsync(quote.Reference, quoted.Revision, "hold", quote.Total, "INR", 3, "2026-12-12", Ct));
        Approve(f.Http, quote, quoted.Revision, "hold");
        var held = await f.Holds.HoldAsync(quote.Reference, quoted.Revision, "hold", quote.Total, "INR", 3, "2026-12-12", Ct);
        var replay = await f.Holds.HoldAsync(quote.Reference, quoted.Revision, "hold", quote.Total, "INR", 3, "2026-12-12", Ct);
        Assert.Contains("+05:30", held.Hold.ExpiryDisplay);
        Assert.Equal(held.Hold.Reference, replay.Hold.Reference); Assert.Equal(held.Hold.ExpiresAt, replay.Hold.ExpiresAt);
        Assert.Equal(3, Assert.Single(await f.Holds.AvailabilityAsync(Ct)).AvailableSlots);
        await Assert.ThrowsAsync<JourneyConflictException>(() => f.Journey.SaveAsync(new(HotelStars: 4), quoted.Revision, "edit", Ct));
        Assert.Equal(47250m, (await f.Journey.GetAsync(Ct))!.Quotes.Single().Total);
        var other = fixture.Services("other"); await Assert.ThrowsAsync<InvalidOperationException>(() => other.Holds.ReleaseAsync(held.Hold.Reference, Ct));
        await f.Holds.ReleaseAsync(held.Hold.Reference, Ct); await f.Holds.ReleaseAsync(held.Hold.Reference, Ct);
        Assert.Equal(6, Assert.Single(await f.Holds.AvailabilityAsync(Ct)).AvailableSlots);
    }
    [Fact(SkipUnless = nameof(Enabled), Skip = "Requires isolated PostgreSQL test scope.")]
    public async Task Concurrent_parties_compete_for_shared_slots_and_expired_holds_release_capacity()
    {
        await using var fixture = new Fixture(); await fixture.Store.InitializeAsync(Ct);
        var parties = new[] { fixture.Services("a"), fixture.Services("b") };
        var quotes = new List<TravelJourney>();
        foreach (var p in parties)
        {
            var saved = await p.Journey.SaveAsync(Family() with { Adults = 3 }, 0, "save", Ct);
            var quoted = await p.Journey.QuoteAsync("KERALA-FIXED", saved.Revision, "quote", Ct); quotes.Add(quoted);
            Approve(p.Http, quoted.Quotes.Single(), quoted.Revision, "hold");
        }
        var results = await Task.WhenAll(parties.Select(async (p, i) =>
        {
            var q = quotes[i].Quotes.Single();
            try { return await p.Holds.HoldAsync(q.Reference, quotes[i].Revision, "hold", q.Total, "INR", 4, "2026-12-12", Ct); }
            catch (JourneyConflictException) { return null; }
        }));
        var winner = Assert.Single(results, r => r is not null)!;
        Assert.Equal(2, Assert.Single(await parties[0].Holds.AvailabilityAsync(Ct)).AvailableSlots);
        await using (var db = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("AGENTFORGE_TRAVEL_TEST_DB")!))
        await using (var expire = db.CreateCommand("UPDATE travel_holds SET expires_at=clock_timestamp()-interval '1 second' WHERE reference=$1"))
        { expire.Parameters.AddWithValue(winner.Hold.Reference); await expire.ExecuteNonQueryAsync(Ct); }
        Assert.Equal(6, Assert.Single(await parties[0].Holds.AvailabilityAsync(Ct)).AvailableSlots);
        var winningParty = results[0] is not null ? parties[0] : parties[1];
        Assert.Equal("expired", Assert.Single(await winningParty.Holds.GetAsync(Ct)).Status);
        await using var restarted = new TravelJourneyStore(fixture.Configuration);
        var recovered = new TravelHoldService(restarted, fixture.Catalog, winningParty.Scope);
        Assert.Equal("expired", Assert.Single(await recovered.GetAsync(Ct)).Status);
        var index = results[0] is not null ? 0 : 1;
        var original = quotes[index].Quotes.Single();
        var replay = await winningParty.Holds.HoldAsync(original.Reference, quotes[index].Revision, "hold", original.Total, "INR", 4, "2026-12-12", Ct);
        Assert.Equal("expired", replay.Hold.Status);
        Assert.Equal(6, Assert.Single(await winningParty.Holds.AvailabilityAsync(Ct)).AvailableSlots);
    }
    [Fact(SkipUnless = nameof(Enabled), Skip = "Requires isolated PostgreSQL test scope.")]
    public async Task Changed_approval_amount_stale_quotes_and_custom_inventory_do_not_create_holds()
    {
        await using var fixture = new Fixture(); await fixture.Store.InitializeAsync(Ct);
        var p = fixture.Services("one"); var saved = await p.Journey.SaveAsync(Family(), 0, "save", Ct);
        var fixedQuote = await p.Journey.QuoteAsync("KERALA-FIXED", saved.Revision, "fixed", Ct); var q = fixedQuote.Quotes.Single();
        Approve(p.Http, q, fixedQuote.Revision, "hold");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => p.Holds.HoldAsync(q.Reference, fixedQuote.Revision, "hold", 1, "INR", 3, "2026-12-12", Ct));
        Approve(p.Http, q, fixedQuote.Revision, "bad-price", 1);
        await Assert.ThrowsAsync<JourneyConflictException>(() => p.Holds.HoldAsync(q.Reference, fixedQuote.Revision, "bad-price", 1, "INR", 3, "2026-12-12", Ct));
        var changed = await p.Journey.SaveAsync(new(HotelStars: 4), fixedQuote.Revision, "change", Ct);
        Approve(p.Http, q, changed.Revision, "old-quote");
        await Assert.ThrowsAsync<JourneyConflictException>(() => p.Holds.HoldAsync(q.Reference, changed.Revision, "old-quote", q.Total, "INR", 3, "2026-12-12", Ct));
        var custom = await p.Journey.QuoteAsync("KERALA-CUSTOM", changed.Revision, "custom", Ct); q = custom.Quotes.Last(); Approve(p.Http, q, custom.Revision, "custom-hold");
        await Assert.ThrowsAsync<InvalidOperationException>(() => p.Holds.HoldAsync(q.Reference, custom.Revision, "custom-hold", q.Total, "INR", 3, "2026-12-12", Ct));
        Assert.Empty(await p.Holds.GetAsync(Ct));
    }
    private static TripRequirements Family() => new("Demo Family", "Kerala", new(2026, 12, 12), "Ahmedabad", 2, [8], 5, 70000, 3, false);
    private static void Approve(FixedAccessor http, TravelQuote quote, int revision, string operation, decimal? approvedTotal = null)
    {
        var identity = (ClaimsIdentity)http.HttpContext!.User.Identity!;
        foreach (var claim in identity.FindAll("agentforge:approved-action").ToArray()) identity.RemoveClaim(claim);
        identity.AddClaim(new("agentforge:approved-action", JsonSerializer.Serialize(new { name = "hold_travel_quote", arguments = new { quoteReference = quote.Reference, expectedRevision = revision, operationId = operation, expectedTotal = approvedTotal ?? quote.Total, currency = quote.Currency, partySize = quote.PartySize, startDate = "2026-12-12" } })));
    }
    private sealed class FixedAccessor : IHttpContextAccessor { public HttpContext? HttpContext { get; set; } }
    private sealed class Fixture : IAsyncDisposable
    {
        public IConfiguration Configuration { get; } = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {{"ConnectionStrings:vertical-db",Environment.GetEnvironmentVariable("AGENTFORGE_TRAVEL_TEST_DB")},{"AGENTFORGE_CONFIG_VERSION",Guid.NewGuid().ToString()},{"TravelCustomerProfile:BusinessHours:TimeZone","Asia/Kolkata"}}).Build();
        public TravelJourneyStore Store { get; }
        public TravelScenarioCatalog Catalog { get; }
        public Fixture() { Store = new(Configuration); Catalog = new(Configuration); }
        public (TravelJourneyService Journey, TravelHoldService Holds, FixedAccessor Http, TravelInvocationScope Scope) Services(string account)
        {
            var principal = TravelJourneyTests.Identity(account); var id = (ClaimsIdentity)principal.Identity!; id.RemoveClaim(id.FindFirst("agentforge:configuration")!); id.AddClaim(new("agentforge:configuration", Configuration["AGENTFORGE_CONFIG_VERSION"]!));
            var http = new FixedAccessor { HttpContext = new DefaultHttpContext { User = principal } }; var scope = new TravelInvocationScope(Configuration, http);
            return (new(Store, Catalog, scope, TimeProvider.System), new(Store, Catalog, scope, Configuration), http, scope);
        }
        public ValueTask DisposeAsync() => Store.DisposeAsync();
    }
}
