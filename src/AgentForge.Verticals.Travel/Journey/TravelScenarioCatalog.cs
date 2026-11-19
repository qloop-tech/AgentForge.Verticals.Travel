using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace AgentForge.Verticals.Travel.Journey;

public sealed class TravelScenarioCatalog
{
    public TravelScenario Scenario { get; }
    public TravelScenarioCatalog(IConfiguration configuration)
    {
        var external = configuration["CUSTOMER_CONFIG_PATH"];
        var path = string.IsNullOrWhiteSpace(external) ? null : Path.Combine(Path.GetFullPath(external), "travel-scenario.json");
        var json = path is not null && File.Exists(path) ? File.ReadAllText(path) : TravelDataFiles.ReadAllText("TravelScenario.json");
        Scenario = JsonSerializer.Deserialize<TravelScenario>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Missing travel scenario.");
        if (!Scenario.IsDemo || Scenario.Currency != "INR" || Scenario.Days < 1 || string.IsNullOrWhiteSpace(Scenario.Version) ||
            Scenario.Offers.Length != 2 || Scenario.Offers.Select(o => o.Id).Distinct().Count() != Scenario.Offers.Length ||
            Scenario.Offers.Any(o => o.Kind is not ("fixed" or "custom") || o.AdultPrice < 0 || o.ChildPrice < 0 || o.InfantPrice < 0 ||
                o.FourStarSupplement < 0 || o.HouseboatSupplement < 0 || o.TaxRate is < 0 or > 1 ||
                (o.Kind == "fixed" && (o.DepartureDate is null || o.AvailableSlots is null or < 0))))
            throw new InvalidOperationException("Invalid labelled demo inventory.");
    }
}
