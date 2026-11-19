using System.Security.Claims;
using AgentForge.Verticals.Travel.Journey;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace AgentForge.Verticals.Travel.Tests;

public sealed class TravelJourneyTests
{
    private static IConfiguration Config() => new ConfigurationBuilder().Build();
    private static readonly TripRequirements Family = new("Demo Family", "Kerala", new(2026, 12, 12), "Ahmedabad", 2, [8], 5, 70000m, 3, false);
    [Theory]
    [InlineData(3, false, 47250)]
    [InlineData(4, false, 56700)]
    [InlineData(4, true, 63000)]
    public void Family_quotes_include_child_supplements_and_tax(int stars, bool houseboat, decimal expected)
    {
        var scenario = new TravelScenarioCatalog(Config()).Scenario;
        var journey = new TravelJourney("INQ-test", 1, Family with { HotelStars = stars, Houseboat = houseboat }, [], []);
        var quote = TravelJourneyService.Calculate(journey, scenario.Offers[0], scenario);
        Assert.Equal(expected, quote.Total);
        Assert.Equal(9000m, quote.ChildSubtotal);
        Assert.Equal(3, quote.PartySize);
        Assert.Equal(1, quote.InquiryRevision);
        Assert.Contains("no reservation", quote.Status);
    }
    [Fact]
    public void Fixed_date_and_capacity_are_validated_without_inventing_custom_availability()
    {
        var scenario = new TravelScenarioCatalog(Config()).Scenario;
        var journey = new TravelJourney("test", 1, Family with { StartDate = new(2026, 12, 13) }, [], []);
        Assert.Throws<InvalidOperationException>(() => TravelJourneyService.Calculate(journey, scenario.Offers[0], scenario));
        var custom = TravelJourneyService.Calculate(journey, scenario.Offers[1], scenario);
        Assert.Equal(52500m, custom.Total);
        Assert.Null(scenario.Offers[1].AvailableSlots);
        Assert.Throws<InvalidOperationException>(() => TravelJourneyService.Calculate(journey with { Requirements = Family with { Adults = 6 } }, scenario.Offers[0], scenario));
    }
    [Fact]
    public void Infants_and_missing_child_information_are_distinct()
    {
        var scenario = new TravelScenarioCatalog(Config()).Scenario;
        var journey = new TravelJourney("test", 1, Family with { ChildAges = [1] }, [], []);
        Assert.Equal(37800m, TravelJourneyService.Calculate(journey, scenario.Offers[0], scenario).Total);
        Assert.Contains("childAges (empty array if no children)", TravelJourneyService.Missing(Family with { ChildAges = null }));
    }
    [Fact]
    public void Scope_requires_authenticated_identity_and_separates_accounts_and_customers()
    {
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var scope = new TravelInvocationScope(Config(), http);
        Assert.Throws<UnauthorizedAccessException>(() => scope.Key());
        http.HttpContext.User = Identity("one");
        var first = scope.Key();
        http.HttpContext.User = Identity("two");
        Assert.NotEqual(first, scope.Key());
        http.HttpContext.User = Identity("one", "other-customer");
        Assert.Throws<UnauthorizedAccessException>(() => scope.Key());
    }
    internal static ClaimsPrincipal Identity(string account, string customer = "default") => new(new ClaimsIdentity(new[]
    {
        new Claim("agentforge:tenant", "default"), new Claim("agentforge:customer", customer),
        new Claim("agentforge:configuration", "1"), new Claim("agentforge:vertical", "travel"),
        new Claim("agentforge:account", account), new Claim("agentforge:conversation", "test@lid")
    }, "test"));
}
