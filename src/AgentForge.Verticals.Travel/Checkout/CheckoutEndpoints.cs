using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using AgentForge.Verticals.Travel.Journey;

namespace AgentForge.Verticals.Travel.Checkout;

public static class CheckoutEndpoints
{
    public static void Map(IEndpointRouteBuilder routes) => routes.MapPost("/vertical-hooks/travel/razorpay", ReceiveAsync);
    private static async Task<IResult> ReceiveAsync(HttpRequest request, TravelCheckoutDbContext db, IConfiguration config, CancellationToken ct)
    {
        const int limit = 128 * 1024;
        if (request.ContentLength > limit) return Results.StatusCode(413);
        using var buffer = new MemoryStream(); var chunk = new byte[8192]; int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        { if (buffer.Length + read > limit) return Results.StatusCode(413); buffer.Write(chunk, 0, read); }
        var bytes = buffer.ToArray();
        if (!RazorpayWebhookSignature.Valid(bytes, request.Headers["X-Razorpay-Signature"].ToString(), config["VerticalPayments:WebhookSecret"] ?? "")) return Results.Unauthorized();
        try
        {
            using var document = JsonDocument.Parse(bytes); var root = document.RootElement;
            var kind = RazorpayClient.Text(root, "event") ?? "";
            if (kind != "payment_link.paid") return Results.Ok();
            var payload = root.GetProperty("payload"); var link = payload.GetProperty("payment_link").GetProperty("entity");
            var reference = RazorpayClient.Text(link, "reference_id"); var linkId = RazorpayClient.Text(link, "id");
            if (reference is null || linkId is null || reference.Length > 40 || linkId.Length > 100) return Results.BadRequest();
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes)); var eventId = request.Headers["X-Razorpay-Event-Id"].ToString();
            if (eventId.Length == 0) eventId = hash; if (eventId.Length > 128) return Results.BadRequest();
            var partition = TravelInvocationScope.ConfiguredPartition(config);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await TravelCheckoutService.Gate(db, "payment-event:" + eventId, ct);
            var existing = await db.Events.FindAsync([eventId], ct);
            if (existing is not null) return existing.BodyHash == hash ? Results.Ok() : Results.Conflict();
            var attempt = await db.Attempts.SingleOrDefaultAsync(x => x.Id == reference && x.PartitionKey == partition, ct);
            if (attempt is null) return Results.Ok(); // another deployment's event
            string? payment = null;
            if (payload.TryGetProperty("payment", out var p) && p.TryGetProperty("entity", out var entity)) payment = RazorpayClient.Text(entity, "id");
            db.Events.Add(new()
            {
                Id = eventId,
                PartitionKey = partition,
                LinkId = linkId,
                Reference = reference,
                PaymentId = payment,
                Kind = kind,
                BodyHash = hash,
                CreatedAt = await TravelCheckoutService.Now(db, ct)
            });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Results.Ok();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return Results.BadRequest(); }
    }
}
