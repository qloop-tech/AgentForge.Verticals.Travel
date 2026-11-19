namespace AgentForge.Verticals.Travel.Journey;

public sealed record TravelHold(string Reference, string QuoteReference, int PartySize, DateTimeOffset ExpiresAt,
    string Status, TravelQuote Quote, string? ExpiryDisplay = null);
public sealed record TravelHoldResult(TravelHold Hold, string Notice = "Demo hold only — no payment or confirmed booking.");
public sealed record TravelAvailability(string OfferId, int SeededSlots, int HeldSlots, int AvailableSlots);
