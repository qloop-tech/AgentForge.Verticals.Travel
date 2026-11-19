using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AgentForge.Verticals.Travel.Journey;

/// <summary>Plugin-owned native approval policy and traveller-facing description; Core knows no travel tool names.</summary>
public static class TravelHoldApprovalPolicy
{
    public static AIFunction Apply(AIFunction function) => function.Name is "hold_travel_quote" or "create_travel_checkout" ? new ApprovalRequiredAIFunction(function) : function;
    public static string? Describe(ToolApprovalRequestContent request)
    {
        if (request.ToolCall is not FunctionCallContent { Arguments: { } args } call) return null;
        if (call.Name is not ("hold_travel_quote" or "create_travel_checkout")) return null;
        var data = JsonSerializer.SerializeToElement(args);
        try
        {
            if (call.Name == "create_travel_checkout") return $"*Confirm demo checkout*\nHold: {data.GetProperty("holdReference").GetString()}\nFull amount: {data.GetProperty("currency").GetString()} {data.GetProperty("expectedTotal").GetDecimal():N2}\nExtend this active hold to a 30-minute payment window and create one Razorpay TEST payment link. Creating the link does not charge you. Booking is confirmed only after verified test payment.";
            return $"*Confirm a demo hold*\nQuote: {data.GetProperty("quoteReference").GetString()}\n{data.GetProperty("partySize").GetInt32()} travellers; departure {data.GetProperty("startDate").GetString()}\nTotal quote: {data.GetProperty("currency").GetString()} {data.GetProperty("expectedTotal").GetDecimal():N2}\nHold duration: 15 minutes. No payment is taken and no booking is confirmed.";
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException) { return null; }
    }
}
