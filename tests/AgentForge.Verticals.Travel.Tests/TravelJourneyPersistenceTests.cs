using AgentForge.Verticals.Travel.Journey;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace AgentForge.Verticals.Travel.Tests;

public sealed class TravelJourneyPersistenceTests
{
    public static bool Enabled => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGENTFORGE_TRAVEL_TEST_DB"));
    [Fact(SkipUnless = nameof(Enabled), Skip = "Opt into isolated PostgreSQL journey persistence tests.")]
    public async Task Saved_quote_revisions_survive_restart_and_retries_conflicts_and_other_scopes_cannot_change_them()
    {
        var ct = TestContext.Current.CancellationToken;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:vertical-db"] = Environment.GetEnvironmentVariable("AGENTFORGE_TRAVEL_TEST_DB") }).Build();
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = TravelJourneyTests.Identity(Guid.NewGuid().ToString()) } };
        var scope = new TravelInvocationScope(configuration, http);
        var catalog = new TravelScenarioCatalog(configuration);
        await using var firstStore = new TravelJourneyStore(configuration);
        await firstStore.InitializeAsync(ct);
        var service = new TravelJourneyService(firstStore, catalog, scope, TimeProvider.System);
        var family = new TripRequirements("Demo Family", "Kerala", new(2026, 12, 12), "Ahmedabad", 2, [8], 5, 70000, 3, false);
        var saved = await service.SaveAsync(family, 0, "capture", ct);
        var replay = await service.SaveAsync(family, 0, "capture", ct);
        Assert.Equal(saved.InquiryId, replay.InquiryId);
        Assert.Equal(saved.Revision, replay.Revision);
        await Assert.ThrowsAsync<JourneyConflictException>(() => service.SaveAsync(family with { Adults = 3 }, 0, "capture", ct));
        var quoted = await service.QuoteAsync("KERALA-FIXED", saved.Revision, "quote", ct);
        var changed = await service.SaveAsync(new(HotelStars: 4, Houseboat: true), quoted.Revision, "upgrade", ct);
        Assert.Null(changed.SelectedQuote);
        var revised = await service.QuoteAsync("KERALA-FIXED", changed.Revision, "revised", ct);
        Assert.Equal(new decimal[] { 47250, 63000 }, revised.Quotes.Select(q => q.Total));
        await Assert.ThrowsAsync<JourneyConflictException>(() => service.SaveAsync(new(HotelStars: 3), quoted.Revision, "stale", ct));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(new(ChildAges: [-1]), revised.Revision, "bad-age", ct));
        await using var recreated = new TravelJourneyStore(configuration);
        var restored = await new TravelJourneyService(recreated, catalog, scope, TimeProvider.System).GetAsync(ct);
        Assert.Equal(revised.SelectedQuote, restored!.SelectedQuote);
        Assert.Equal(2, restored.Quotes.Length);
        Assert.Equal(3, restored.Quotes[0].Requirements.HotelStars);
        Assert.Equal(4, restored.Quotes[1].Requirements.HotelStars);
        Assert.Equal(4, restored.Requirements.HotelStars);
        http.HttpContext.User = TravelJourneyTests.Identity(Guid.NewGuid().ToString());
        Assert.Null(await service.GetAsync(ct));
        // Both contenders use the same revision; exactly one is allowed to advance it.
        http.HttpContext.User = TravelJourneyTests.Identity(Guid.NewGuid().ToString());
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async i =>
        {
            try { await service.SaveAsync(family, 0, "racing-" + i, ct); return true; }
            catch (JourneyConflictException) { return false; }
        }));
        Assert.Single(results, x => x);
    }
}
