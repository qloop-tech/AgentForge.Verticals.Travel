namespace AgentForge.Verticals.Travel.Journey;

public sealed record TripRequirements(string? CustomerName = null, string? Destination = null, DateOnly? StartDate = null,
    string? DepartureCity = null, int? Adults = null, int[]? ChildAges = null, int? Days = null,
    decimal? Budget = null, int? HotelStars = null, bool? Houseboat = null);
public sealed record TravelQuote(string Reference, int Version, int InquiryRevision, string OfferId, string Expert,
    string InventoryVersion, string Kind, DateOnly StartDate, int PartySize, decimal AdultSubtotal,
    decimal ChildSubtotal, decimal UpgradeSubtotal, decimal HouseboatSubtotal, decimal Tax, decimal Total,
    string Currency, string[] Inclusions, string[] Exclusions, TripRequirements Requirements, string Status = "Demo proposal — no reservation or payment");
public sealed record TravelJourney(string InquiryId, int Revision, TripRequirements Requirements,
    string[] MissingFields, TravelQuote[] Quotes, string? SelectedQuote = null);
public sealed record DemoOffer(string Id, string Expert, string Kind, DateOnly? DepartureDate, int? AvailableSlots,
    decimal AdultPrice, decimal ChildPrice, decimal InfantPrice, decimal FourStarSupplement,
    decimal HouseboatSupplement, decimal TaxRate, string[] Inclusions, string[] Exclusions);
public sealed record TravelScenario(string Version, bool IsDemo, string Destination, int Days, string Currency, DemoOffer[] Offers);
public sealed class JourneyConflictException(string message) : InvalidOperationException(message);
