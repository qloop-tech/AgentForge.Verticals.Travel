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
    public string Key()
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
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values))));
    }
}
