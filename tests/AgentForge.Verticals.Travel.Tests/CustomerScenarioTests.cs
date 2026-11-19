using AgentForge.Verticals.Travel.Journey;
using AgentForge.Verticals.Travel.Services;
using Microsoft.Extensions.Configuration;

namespace AgentForge.Verticals.Travel.Tests;

public sealed class CustomerScenarioTests
{
    [Fact]
    public void External_demo_overrides_catalog_policies_and_media_without_changing_bundled_defaults()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["CUSTOMER_CONFIG_PATH"] = Path.Combine(AppContext.BaseDirectory, "DemoCustomer") }).Build();
        var catalog = new TourCatalogService(configuration);
        Assert.Equal("KERALA-DEMO", Assert.Single(catalog.GetAll()).Id);
        Assert.Null(catalog.GetById("KERALA-BACKWATERS"));
        var agency = new PolicyService(configuration).GetAgencyInfo();
        Assert.DoesNotContain("Royal", agency.Name);
        Assert.Contains("no payments", agency.Payment.AccountNumber);
        Assert.Empty(new HotelService(configuration).GetByDestination("Kerala"));
        Assert.NotNull(new TourCatalogService().GetById("KERALA-BACKWATERS"));
        var scenario = new TravelScenarioCatalog(configuration).Scenario;
        Assert.True(scenario.IsDemo);
        Assert.Equal(2, scenario.Offers.Length);
    }
}
