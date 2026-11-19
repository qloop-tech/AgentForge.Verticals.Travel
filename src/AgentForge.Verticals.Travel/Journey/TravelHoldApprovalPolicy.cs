using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AgentForge.Verticals.Travel.Journey;

/// <summary>Plugin-owned native approval policy and traveller-facing description; Core knows no travel tool names.</summary>
public static class TravelHoldApprovalPolicy
{
    public static AIFunction Apply(AIFunction function) => function.Name == "hold_travel_quote" ? new ApprovalRequiredAIFunction(function) : function;
    public static string? Describe(ToolApprovalRequestContent request)
    {
        if (request.ToolCall is not FunctionCallContent { Name: "hold_travel_quote", Arguments: { } args }) return null;
        var data = JsonSerializer.SerializeToElement(args);
        try
        {
            return $"*Confirm a demo hold*\nQuote: {data.GetProperty("quoteReference").GetString()}\n{data.GetProperty("partySize").GetInt32()} travellers; departure {data.GetProperty("startDate").GetString()}\nTotal quote: {data.GetProperty("currency").GetString()} {data.GetProperty("expectedTotal").GetDecimal():N2}\nHold duration: 15 minutes. No payment is taken and no booking is confirmed.";
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException) { return null; }
    }
}
