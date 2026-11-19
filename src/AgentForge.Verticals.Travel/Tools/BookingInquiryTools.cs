using System.Text.Json;
using ModelContextProtocol.Server;
using AgentForge.Verticals.Travel.Journey;

namespace AgentForge.Verticals.Travel.Tools;

[McpServerToolType]
public sealed class BookingInquiryTools(TravelJourneyService service)
{
    [McpServerTool(Name = "create_booking_inquiry", ReadOnly = false)]
    [Description("Compatibility guidance: use save_travel_requirements to persist a scoped inquiry, then create_travel_quote. This legacy operation cannot create a booking or promise deposits.")]
    public string CreateBookingInquiry(string phone, string customerName, string tourId, string travelMonth, int paxCount, string? specialRequests = null) =>
        "Use save_travel_requirements with verified party details, exact date, total family budget and preferences. Phone is not an authorization key. No booking or deposit is confirmed.";

    [McpServerTool(Name = "get_customer_inquiries", ReadOnly = true)]
    [Description("Retrieve the current authenticated conversation's persisted inquiry. The legacy phone argument is ignored for authorization.")]
    public async Task<string> GetCustomerInquiries(string phone, CancellationToken cancellationToken) =>
        JsonSerializer.Serialize(await service.GetAsync(cancellationToken));
}
