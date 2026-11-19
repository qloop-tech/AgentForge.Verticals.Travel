using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace AgentForge.Verticals.Travel.Journey;

[McpServerToolType]
public sealed class TravelHoldTools(TravelHoldService holds)
{
    [McpServerTool(Name = "get_travel_holds", ReadOnly = true)]
    [Description("Retrieve this authenticated conversation's demo holds and current expiry status. A hold is not a paid or confirmed booking.")]
    public async Task<string> GetHolds(CancellationToken cancellationToken) => JsonSerializer.Serialize(await holds.GetAsync(cancellationToken));
    [McpServerTool(Name = "get_travel_availability", ReadOnly = true)]
    [Description("Get current remaining fixed-departure demo slots after unexpired holds across all conversations. Custom offers require supplier confirmation.")]
    public async Task<string> GetAvailability(CancellationToken cancellationToken) => JsonSerializer.Serialize(await holds.AvailabilityAsync(cancellationToken));
    [McpServerTool(Name = "hold_travel_quote", ReadOnly = false)]
    [Description("Request explicit traveller approval, then place a 15-minute DEMO fixed-departure hold for an exact saved quote/revision. No payment or booking. Native approval is mandatory; do not claim a hold exists until this tool executes successfully. A fresh operationId identifies the exact action.")]
    public async Task<string> HoldQuote(string quoteReference, int expectedRevision, string operationId, decimal expectedTotal, string currency, int partySize, string startDate, CancellationToken cancellationToken)
    {
        try { return JsonSerializer.Serialize(await holds.HoldAsync(quoteReference, expectedRevision, operationId, expectedTotal, currency, partySize, startDate, cancellationToken)); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException) { return JsonSerializer.Serialize(new { error = ex.Message }); }
    }
    [McpServerTool(Name = "release_travel_hold", ReadOnly = false)]
    [Description("Release THIS conversation's demo hold when the traveller asks to cancel it or change their held quote. Idempotent; no payment or booking is affected.")]
    public async Task<string> ReleaseHold(string holdReference, CancellationToken cancellationToken)
    {
        try { return JsonSerializer.Serialize(await holds.ReleaseAsync(holdReference, cancellationToken)); }
        catch (InvalidOperationException ex) { return JsonSerializer.Serialize(new { error = ex.Message }); }
    }
}
