using System.Text.Json;

namespace AgentForge.Verticals.Travel.Journey;

public sealed class TravelJourneyService(TravelJourneyStore store, TravelScenarioCatalog catalog, TravelInvocationScope scope, TimeProvider clock)
{
    public Task<TravelJourney?> GetAsync(CancellationToken ct) => store.GetAsync(scope.Key(), ct);
    public TravelScenario Offers() => catalog.Scenario;
    public Task<TravelJourney> SaveAsync(TripRequirements patch, int expectedRevision, string operationId, CancellationToken ct) =>
        store.ExecuteAsync(scope.Key(), operationId, JsonSerializer.Serialize(new { command = "requirements", patch, expectedRevision }), expectedRevision, current =>
        {
            var old = current?.Requirements ?? new();
            var merged = new TripRequirements(patch.CustomerName ?? old.CustomerName, patch.Destination ?? old.Destination,
                patch.StartDate ?? old.StartDate, patch.DepartureCity ?? old.DepartureCity, patch.Adults ?? old.Adults,
                patch.ChildAges ?? old.ChildAges, patch.Days ?? old.Days, patch.Budget ?? old.Budget, patch.HotelStars ?? old.HotelStars,
                patch.Houseboat ?? old.Houseboat);
            Validate(merged);
            return new TravelJourney(current?.InquiryId ?? $"INQ-{Guid.NewGuid():N}", (current?.Revision ?? 0) + 1,
                merged, Missing(merged), current?.Quotes ?? []); // a changed requirement clears the selected proposal
        }, ct);
    public Task<TravelJourney> QuoteAsync(string offerId, int expectedRevision, string operationId, CancellationToken ct) =>
        store.ExecuteAsync(scope.Key(), operationId, JsonSerializer.Serialize(new { command = "quote", offerId, expectedRevision }), expectedRevision, current =>
        {
            if (current is null || current.MissingFields.Length > 0) throw new InvalidOperationException("Capture missing traveller requirements before quoting.");
            var offer = catalog.Scenario.Offers.SingleOrDefault(o => o.Id == offerId) ?? throw new ArgumentException("Unknown demo offer.");
            Validate(current.Requirements);
            var quote = Calculate(current, offer, catalog.Scenario);
            return current with { Revision = current.Revision + 1, Quotes = [.. current.Quotes, quote], SelectedQuote = quote.Reference };
        }, ct);
    public static TravelQuote Calculate(TravelJourney journey, DemoOffer offer, TravelScenario scenario)
    {
        var r = journey.Requirements;
        if (Missing(r).Length > 0) throw new InvalidOperationException("Incomplete requirements.");
        if (!string.Equals(r.Destination, scenario.Destination, StringComparison.OrdinalIgnoreCase) || r.Days != scenario.Days)
            throw new InvalidOperationException("This demo inventory only covers the configured destination and duration.");
        var size = r.Adults!.Value + r.ChildAges!.Length;
        if (offer.Kind == "fixed" && (r.StartDate != offer.DepartureDate || size > offer.AvailableSlots))
            throw new InvalidOperationException("Fixed departure date or seeded remaining slots do not match this party. Consider the custom offer.");
        var adults = r.Adults.Value * offer.AdultPrice;
        var children = r.ChildAges.Sum(age => age < 2 ? offer.InfantPrice : offer.ChildPrice);
        var upgrades = r.HotelStars == 4 ? size * offer.FourStarSupplement : 0;
        var boat = r.Houseboat == true ? size * offer.HouseboatSupplement : 0;
        var tax = decimal.Round((adults + children + upgrades + boat) * offer.TaxRate, 2, MidpointRounding.AwayFromZero);
        var version = journey.Quotes.Length + 1;
        return new TravelQuote($"Q-{journey.InquiryId}-{version}", version, journey.Revision, offer.Id, offer.Expert, scenario.Version,
            offer.Kind, r.StartDate!.Value, size, adults, children, upgrades, boat, tax, adults + children + upgrades + boat + tax,
            scenario.Currency, offer.Inclusions, offer.Exclusions, r);
    }
    private void Validate(TripRequirements r)
    {
        if (r.Adults is < 1 or > 8 || r.ChildAges is { Length: > 6 } || r.ChildAges?.Any(a => a is < 0 or > 17) == true ||
            r.Days is < 1 or > 30 || r.Budget is <= 0 or > 10000000 || r.HotelStars is not (null or 3 or 4) ||
            r.StartDate < DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime) ||
            new[] { r.CustomerName, r.Destination, r.DepartureCity }.Any(s => s is not null && (string.IsNullOrWhiteSpace(s) || s.Length > 100)))
            throw new ArgumentException("Invalid traveller requirements: check dates, party size/ages, budget and hotel category.");
    }
    public static string[] Missing(TripRequirements r)
    {
        List<string> missing = [];
        if (string.IsNullOrWhiteSpace(r.CustomerName)) missing.Add("customerName");
        if (string.IsNullOrWhiteSpace(r.Destination)) missing.Add("destination");
        if (r.StartDate is null) missing.Add("startDate");
        if (string.IsNullOrWhiteSpace(r.DepartureCity)) missing.Add("departureCity");
        if (r.Adults is null) missing.Add("adults");
        if (r.ChildAges is null) missing.Add("childAges (empty array if no children)");
        if (r.Days is null) missing.Add("days");
        if (r.Budget is null) missing.Add("total family budget in INR");
        if (r.HotelStars is null) missing.Add("hotelStars");
        if (r.Houseboat is null) missing.Add("houseboat preference");
        return [.. missing];
    }
}
