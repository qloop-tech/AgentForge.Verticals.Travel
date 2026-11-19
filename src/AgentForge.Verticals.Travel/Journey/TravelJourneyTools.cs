using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace AgentForge.Verticals.Travel.Journey;

[McpServerToolType]
public sealed class TravelJourneyTools(TravelJourneyService service)
{
    [McpServerTool(Name = "get_travel_journey", ReadOnly = true)]
    [Description("Retrieve THIS authenticated conversation's persisted inquiry, revision, missing requirements and all quote versions. No phone or inquiry reference selects identity.")]
    public async Task<string> GetJourney(CancellationToken cancellationToken) => JsonSerializer.Serialize(new { demo = true, journey = await service.GetAsync(cancellationToken) });

    [McpServerTool(Name = "list_demo_travel_offers", ReadOnly = true)]
    [Description("Read labelled demo inventory: fixed departure with seeded slots and a customizable alternative from two synthetic experts. These are not live TravelTriangle suppliers or availability.")]
    public string ListOffers() => JsonSerializer.Serialize(service.Offers());

    [McpServerTool(Name = "save_travel_requirements", ReadOnly = false)]
    [Description("Persist known traveller requirements, merging supplied fields. Omit unknowns; never invent details. expectedRevision=0 for new inquiry, otherwise use get_travel_journey. operationId is a unique command identifier; reuse only to retry identical input. Empty childAges means no children. budget is TOTAL family INR budget.")]
    public async Task<string> SaveRequirements(int expectedRevision, string operationId,
        string? customerName = null, string? destination = null, string? startDate = null, string? departureCity = null,
        int? adults = null, int[]? childAges = null, int? days = null, decimal? budget = null, int? hotelStars = null,
        bool? houseboat = null, CancellationToken cancellationToken = default)
    {
        try
        {
            DateOnly? date = startDate is null ? null : DateOnly.ParseExact(startDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            return JsonSerializer.Serialize(await service.SaveAsync(new(customerName, destination, date, departureCity, adults, childAges, days, budget, hotelStars, houseboat), expectedRevision, operationId, cancellationToken));
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException) { return JsonSerializer.Serialize(new { error = ex.Message }); }
    }
    [McpServerTool(Name = "create_travel_quote", ReadOnly = false)]
    [Description("Calculate and SAVE a versioned demo family quote from a persisted complete inquiry and chosen demo offer. Uses code for child/infant pricing, upgrade, houseboat, tax and total. Does not reserve slots, charge or confirm a booking. For comparison, quote each offer using the latest revision returned by the prior operation.")]
    public async Task<string> CreateQuote(string offerId, int expectedRevision, string operationId, CancellationToken cancellationToken)
    {
        try { return JsonSerializer.Serialize(await service.QuoteAsync(offerId, expectedRevision, operationId, cancellationToken)); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return JsonSerializer.Serialize(new { error = ex.Message }); }
    }
}
