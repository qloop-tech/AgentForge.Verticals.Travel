using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace AgentForge.Verticals.Travel.Journey;

/// <summary>Consumes the host's generic verified claims or framework run metadata. Never accepts model identity.</summary>
public sealed class TravelInvocationScope(IConfiguration configuration, IHttpContextAccessor http)
{
    private string[] Identity()
    {
        string Read(string field)
        {
            var key = "agentforge:" + field;
            if (http.HttpContext?.User is { Identity.IsAuthenticated: true } principal)
                return principal.FindFirstValue(key) ?? throw new UnauthorizedAccessException("Missing invocation identity.");
            if (AIAgent.CurrentRunContext?.RunOptions?.AdditionalProperties?.TryGetValue(key, out var value) == true && value is string text)
                return text;
            throw new UnauthorizedAccessException("An authenticated conversation scope is required.");
        }
        var values = new[] { Read("tenant"), Read("customer"), Read("configuration"), Read("vertical"), Read("account"), Read("conversation") };
        var expected = new[] { configuration["AGENTFORGE_TENANT_ID"] ?? "default", configuration["AGENTFORGE_CUSTOMER_ID"] ?? "default", configuration["AGENTFORGE_CONFIG_VERSION"] ?? "1", "travel" };
        if (!values.Take(4).SequenceEqual(expected) || values.Any(string.IsNullOrWhiteSpace))
            throw new UnauthorizedAccessException("Invocation identity does not match this plugin configuration.");
        return values;
    }

    public string Key() => Hash(Identity());
    public string PartitionKey() => Hash(Identity().Take(4).ToArray());
    public (string Account, string Chat) Address() { var values = Identity(); return (values[4], values[5]); }
    public static string ConfiguredPartition(IConfiguration configuration) => Hash([configuration["AGENTFORGE_TENANT_ID"] ?? "default", configuration["AGENTFORGE_CUSTOMER_ID"] ?? "default", configuration["AGENTFORGE_CONFIG_VERSION"] ?? "1", "travel"]);
    public string InventoryKey(string inventoryVersion, string offerId) => Hash([.. Identity().Take(4), inventoryVersion, offerId]);
    private static string Hash(string[] values) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values))));

    public void RequireApprovedCall(string functionName, object arguments)
    {
        const string key = "agentforge:approved-action";
        var raw = http.HttpContext?.User.Identity?.IsAuthenticated == true
            ? http.HttpContext.User.FindFirstValue(key)
            : AIAgent.CurrentRunContext?.RunOptions?.AdditionalProperties?.TryGetValue(key, out var value) == true ? value as string : null;
        if (string.IsNullOrWhiteSpace(raw)) throw new UnauthorizedAccessException("This action requires an explicit authenticated approval.");
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.GetProperty("name").GetString() == functionName &&
                JsonElement.DeepEquals(document.RootElement.GetProperty("arguments"), JsonSerializer.SerializeToElement(arguments))) return;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
        throw new UnauthorizedAccessException("Approval does not match this exact action and arguments.");
    }
}
