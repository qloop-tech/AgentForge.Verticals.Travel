using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace AgentForge.Verticals.Travel.Checkout;

public sealed record ProviderLink(string Id, string Reference, long Amount, long Paid, string Currency, string Status, string Url, long ExpiresAt, string? OrderId, string? PaymentId);
public sealed record ProviderPayment(string Id, long Amount, string Currency, string Status, string? OrderId);
public sealed class PaymentProviderException(int status) : Exception($"Payment provider returned HTTP {status}.") { public int Status { get; } = status; }
public interface ITravelPaymentProvider
{
    Task<ProviderLink> CreateAsync(CheckoutAttempt attempt, CancellationToken ct);
    Task<ProviderLink> GetLinkAsync(string id, CancellationToken ct);
    Task<ProviderLink?> FindAsync(string reference, CancellationToken ct);
    Task<ProviderPayment> GetPaymentAsync(string id, CancellationToken ct);
}
public sealed class RazorpayClient(HttpClient http, IConfiguration configuration) : ITravelPaymentProvider
{
    private void Authenticate(HttpRequestMessage request)
    {
        var id = configuration["VerticalPayments:TestKeyId"];
        var secret = configuration["VerticalPayments:TestKeySecret"];
        if (id?.StartsWith("rzp_test_", StringComparison.Ordinal) != true || string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("Razorpay test credentials are required. Live-mode credentials are not accepted by this demo.");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(id + ":" + secret)));
    }
    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path); Authenticate(request);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new PaymentProviderException((int)response.StatusCode);
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }
    public async Task<ProviderLink> CreateAsync(CheckoutAttempt a, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, "payment_links", new
        {
            amount = a.Amount,
            currency = a.Currency,
            accept_partial = false,
            upi_link = false,
            expire_by = a.Deadline.ToUnixTimeSeconds(),
            reference_id = a.Id,
            description = "DEMO Kerala holiday — full quote payment. Test mode only.",
            notify = new { sms = false, email = false },
            reminder_enable = false,
            notes = new { checkout_reference = a.Id, hold_reference = a.HoldReference }
        }, ct);
        return ReadLink(response.RootElement);
    }
    public async Task<ProviderLink> GetLinkAsync(string id, CancellationToken ct)
    { using var response = await SendAsync(HttpMethod.Get, "payment_links/" + Uri.EscapeDataString(id), null, ct); return ReadLink(response.RootElement); }
    public async Task<ProviderLink?> FindAsync(string reference, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, "payment_links?reference_id=" + Uri.EscapeDataString(reference), null, ct);
        return response.RootElement.GetProperty("payment_links").EnumerateArray().Where(x => Text(x, "reference_id") == reference).Select(ReadLink).SingleOrDefault();
    }
    public async Task<ProviderPayment> GetPaymentAsync(string id, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, "payments/" + Uri.EscapeDataString(id), null, ct); var p = response.RootElement;
        return new(Text(p, "id")!, Number(p, "amount"), Text(p, "currency")!, Text(p, "status")!, Text(p, "order_id"));
    }
    private static ProviderLink ReadLink(JsonElement p)
    {
        var url = Text(p, "short_url") ?? "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "rzp.io")
            throw new InvalidOperationException("Unexpected payment URL from provider.");
        string? payment = null;
        if (p.TryGetProperty("payments", out var list) && list.ValueKind == JsonValueKind.Array)
            payment = list.EnumerateArray().Select(x => Text(x, "payment_id")).FirstOrDefault(x => x is not null);
        return new(Text(p, "id")!, Text(p, "reference_id")!, Number(p, "amount"), Number(p, "amount_paid"), Text(p, "currency")!, Text(p, "status")!, url, Number(p, "expire_by"), Text(p, "order_id"), payment);
    }
    internal static string? Text(JsonElement p, string key) => p.TryGetProperty(key, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
    private static long Number(JsonElement p, string key) => p.TryGetProperty(key, out var x) && x.TryGetInt64(out var n) ? n : 0;
}
public static class RazorpayWebhookSignature
{
    public static bool Valid(byte[] body, string signature, string secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || signature.Length != 64) return false;
        try { return CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body), Convert.FromHexString(signature)); }
        catch (FormatException) { return false; }
    }
}
