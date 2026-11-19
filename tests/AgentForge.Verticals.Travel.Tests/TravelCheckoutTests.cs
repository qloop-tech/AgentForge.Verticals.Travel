using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentForge.Verticals.Travel.Checkout;
using AgentForge.Verticals.Travel.Journey;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentForge.Verticals.Travel.Tests;

public sealed class TravelCheckoutTests
{
    public static bool Enabled=>TravelJourneyPersistenceTests.Enabled;
    private static CancellationToken Ct=>TestContext.Current.CancellationToken;
    [Fact]
    public void Webhook_signature_binds_the_exact_raw_body_and_minor_amount_rejects_rounding()
    {
        var body=Encoding.UTF8.GetBytes("{\"event\":\"payment_link.paid\"}");
        var signature=Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("test-secret"),body));
        Assert.True(RazorpayWebhookSignature.Valid(body,signature,"test-secret"));
        Assert.False(RazorpayWebhookSignature.Valid([..body,32],signature,"test-secret"));
        Assert.False(RazorpayWebhookSignature.Valid(body,new string('z',64),"test-secret"));
        Assert.Equal(6300000,TravelCheckoutService.Minor(63000m));
        Assert.Throws<ArgumentException>(()=>TravelCheckoutService.Minor(1.001m));
    }
    [Fact(SkipUnless=nameof(Enabled),Skip="Requires isolated PostgreSQL scope.")]
    public async Task Approved_checkout_replays_one_link_and_duplicate_payment_confirms_one_booking_and_keeps_slots()
    {
        await using var f=new Fixture();var hold=await f.Hold();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Checkout.CreateAsync(hold.Reference,hold.Quote.Total,"INR",Ct));
        f.ApproveCheckout(hold);
        var checkout=await f.Checkout.CreateAsync(hold.Reference,hold.Quote.Total,"INR",Ct);
        var replay=await f.Checkout.CreateAsync(hold.Reference,hold.Quote.Total,"INR",Ct);
        Assert.Equal("ready",checkout.Status);Assert.Equal(checkout.Reference,replay.Reference);Assert.Equal(1,f.Provider.Creates);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Holds.ReleaseAsync(hold.Reference,Ct));
        f.Provider.Paid=true;
        await Task.WhenAll(Enumerable.Range(0,3).Select(_=>f.Checkout.ReconcileAsync(checkout.Reference,f.Identity.PartitionKey(),f.Provider.PaymentId,Ct)));
        await using var scope=f.Host.Services.CreateAsyncScope();var db=scope.ServiceProvider.GetRequiredService<TravelCheckoutDbContext>();
        Assert.Single(await db.Bookings.Where(x=>x.ScopeKey==f.Identity.Key()).ToListAsync(Ct));
        var allocated=await db.Holds.SingleAsync(x=>x.Reference==hold.Reference,Ct);
        Assert.True(allocated.ExpiresAt>checkout.Deadline.AddDays(1));
        Assert.Equal("booked",Assert.Single(await f.Holds.GetAsync(Ct)).Status);
        Assert.Equal(3,Assert.Single(await f.Holds.AvailabilityAsync(Ct)).AvailableSlots);
        Assert.NotNull(Assert.Single(await f.Checkout.GetAsync(Ct)).BookingReference);
    }
    [Fact(SkipUnless=nameof(Enabled),Skip="Requires isolated PostgreSQL scope.")]
    public async Task Ambiguous_creation_is_not_reposted_and_reconciliation_recovers_provider_link()
    {
        await using var f=new Fixture();var hold=await f.Hold();f.ApproveCheckout(hold);f.Provider.Timeout=true;
        var result=await f.Checkout.CreateAsync(hold.Reference,hold.Quote.Total,"INR",Ct);
        Assert.Equal("needs_reconciliation",result.Status);
        await f.Checkout.CreateAsync(hold.Reference,hold.Quote.Total,"INR",Ct);Assert.Equal(1,f.Provider.Creates);
        var restored=new TravelCheckoutService(f.Host.Services.GetRequiredService<IServiceScopeFactory>(),f.Identity,f.Provider);
        await restored.ReconcileAsync(result.Reference,f.Identity.PartitionKey(),null,Ct);
        Assert.Equal("ready",Assert.Single(await f.Checkout.GetAsync(Ct)).Status);
    }
    [Fact(SkipUnless=nameof(Enabled),Skip="Requires isolated PostgreSQL scope.")]
    public async Task Payment_after_deadline_requires_review_and_does_not_confirm_booking()
    {
        await using var f=new Fixture();var hold=await f.Hold();f.ApproveCheckout(hold);
        var result=await f.Checkout.CreateAsync(hold.Reference,hold.Quote.Total,"INR",Ct);
        await using(var scope=f.Host.Services.CreateAsyncScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<TravelCheckoutDbContext>();
            await db.Holds.Where(x=>x.Reference==hold.Reference).ExecuteUpdateAsync(x=>x.SetProperty(h=>h.ExpiresAt,DateTimeOffset.UtcNow.AddMinutes(-1)),Ct);
        }
        f.Provider.Paid=true;await f.Checkout.ReconcileAsync(result.Reference,f.Identity.PartitionKey(),null,Ct);
        var state=Assert.Single(await f.Checkout.GetAsync(Ct));Assert.Equal("payment_review",state.Status);Assert.Null(state.BookingReference);
    }
    [Fact(SkipUnless=nameof(Enabled),Skip="Requires isolated PostgreSQL scope.")]
    public async Task Wrong_captured_payment_amount_does_not_confirm_booking()
    {
        await using var f=new Fixture();var hold=await f.Hold();f.ApproveCheckout(hold);
        var result=await f.Checkout.CreateAsync(hold.Reference,hold.Quote.Total,"INR",Ct);f.Provider.Paid=true;f.Provider.WrongAmount=true;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Checkout.ReconcileAsync(result.Reference,f.Identity.PartitionKey(),null,Ct));
        Assert.Null(Assert.Single(await f.Checkout.GetAsync(Ct)).BookingReference);
    }
    [Fact(SkipUnless=nameof(Enabled),Skip="Requires isolated PostgreSQL scope.")]
    public async Task Verified_provider_cancellation_allows_release_and_does_not_create_a_booking()
    {
        await using var f=new Fixture();var hold=await f.Hold();f.ApproveCheckout(hold);
        var result=await f.Checkout.CreateAsync(hold.Reference,hold.Quote.Total,"INR",Ct);
        f.Provider.Cancelled=true;
        await f.Checkout.ReconcileAsync(result.Reference,f.Identity.PartitionKey(),null,Ct);
        Assert.Equal("cancelled",Assert.Single(await f.Checkout.GetAsync(Ct)).Status);
        await f.Holds.ReleaseAsync(hold.Reference,Ct);
        Assert.Equal(6,Assert.Single(await f.Holds.AvailabilityAsync(Ct)).AvailableSlots);
    }
    [Fact(SkipUnless=nameof(Enabled),Skip="Requires isolated PostgreSQL scope.")]
    public async Task Another_account_cannot_read_or_use_a_checkout_even_with_copied_hold_arguments()
    {
        await using var f=new Fixture();var hold=await f.Hold();f.ApproveCheckout(hold);
        await f.Checkout.CreateAsync(hold.Reference,hold.Quote.Total,"INR",Ct);
        f.SwitchAccount();f.ApproveCheckout(hold);
        Assert.Empty(await f.Checkout.GetAsync(Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Checkout.CreateAsync(hold.Reference,hold.Quote.Total,"INR",Ct));
        Assert.Equal(1,f.Provider.Creates);
    }
    [Fact(SkipUnless=nameof(Enabled),Skip="Requires isolated PostgreSQL scope.")]
    public async Task Expired_hold_cannot_be_extended_by_checkout_approval()
    {
        await using var f=new Fixture();var hold=await f.Hold();f.ApproveCheckout(hold);
        await using(var scope=f.Host.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<TravelCheckoutDbContext>().Holds.Where(x=>x.Reference==hold.Reference)
                .ExecuteUpdateAsync(x=>x.SetProperty(h=>h.ExpiresAt,DateTimeOffset.UtcNow.AddMinutes(-1)),Ct);
        await Assert.ThrowsAsync<JourneyConflictException>(()=>f.Checkout.CreateAsync(hold.Reference,hold.Quote.Total,"INR",Ct));
        Assert.Equal(0,f.Provider.Creates);
    }
    [Fact(SkipUnless=nameof(Enabled),Skip="Requires isolated PostgreSQL scope.")]
    public async Task Signed_webhook_is_durable_deduplicated_and_verified_before_one_booking()
    {
        await using var f=new Fixture();var hold=await f.Hold();f.ApproveCheckout(hold);
        var checkout=await f.Checkout.CreateAsync(hold.Reference,hold.Quote.Total,"INR",Ct);
        var builder=Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.Configuration.AddConfiguration(f.Host.Services.GetRequiredService<IConfiguration>());
        builder.Configuration["VerticalPayments:WebhookSecret"]="webhook-test-secret";
        builder.WebHost.UseUrls("http://127.0.0.1:0");CheckoutIntegration.Configure(builder);
        await using var app=builder.Build();CheckoutEndpoints.Map(app);await app.StartAsync(Ct);
        var address=app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.Single();
        using var client=new HttpClient{BaseAddress=new(address)};
        var body=JsonSerializer.SerializeToUtf8Bytes(new{ @event="payment_link.paid",payload=new{
            payment_link=new{entity=new{id="plink_fake",reference_id=checkout.Reference}},payment=new{entity=new{id=f.Provider.PaymentId}}}});
        async Task<System.Net.HttpStatusCode> Post(byte[] bytes,string signature)
        {
            using var request=new HttpRequestMessage(HttpMethod.Post,"/vertical-hooks/travel/razorpay"){Content=new ByteArrayContent(bytes)};
            request.Headers.Add("X-Razorpay-Signature",signature);request.Headers.Add("X-Razorpay-Event-Id","evt_"+checkout.Reference);
            using var response=await client.SendAsync(request,Ct);return response.StatusCode;
        }
        string Sign(byte[] bytes)=>Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("webhook-test-secret"),bytes));
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized,await Post(body,new string('0',64)));
        Assert.Equal(System.Net.HttpStatusCode.OK,await Post(body,Sign(body)));
        Assert.Equal(System.Net.HttpStatusCode.OK,await Post(body,Sign(body)));
        var changed=body.Concat(new byte[]{32}).ToArray();
        Assert.Equal(System.Net.HttpStatusCode.Conflict,await Post(changed,Sign(changed)));
        await using(var scope=f.Host.Services.CreateAsyncScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<TravelCheckoutDbContext>();
            Assert.Single(await db.Events.Where(x=>x.Reference==checkout.Reference).ToListAsync(Ct));
            Assert.Empty(await db.Bookings.Where(x=>x.CheckoutId==checkout.Reference).ToListAsync(Ct));
        }
        f.Provider.Paid=true;
        var processor=new CheckoutProcessor(f.Host.Services.GetRequiredService<IServiceScopeFactory>(),f.Checkout,
            f.Host.Services.GetRequiredService<IConfiguration>(),f.Host.Services,Microsoft.Extensions.Logging.Abstractions.NullLogger<CheckoutProcessor>.Instance);
        await processor.TickAsync(Ct);await processor.TickAsync(Ct);
        Assert.NotNull(Assert.Single(await f.Checkout.GetAsync(Ct)).BookingReference);
        await using(var scope=f.Host.Services.CreateAsyncScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<TravelCheckoutDbContext>();
            Assert.Equal("processed",(await db.Events.SingleAsync(x=>x.Reference==checkout.Reference,Ct)).Status);
            Assert.Single(await db.Bookings.Where(x=>x.CheckoutId==checkout.Reference).ToListAsync(Ct));
        }
        await app.StopAsync(Ct);
    }
    private sealed class FakeProvider:ITravelPaymentProvider
    {
        public string PaymentId{get;}="pay_"+Guid.NewGuid().ToString("N");
        public int Creates;public bool Timeout,Paid,WrongAmount,Cancelled;private CheckoutAttempt? attempt;
        private ProviderLink Link()=>new("plink_fake",attempt!.Id,attempt.Amount,Paid?attempt.Amount:0,attempt.Currency,Paid?"paid":Cancelled?"cancelled":"created",
            "https://rzp.io/i/test",attempt.Deadline.ToUnixTimeSeconds(),"order_fake",Paid?PaymentId:null);
        public Task<ProviderLink> CreateAsync(CheckoutAttempt a,CancellationToken ct){Creates++;attempt=a;if(Timeout)throw new HttpRequestException("Simulated response loss");return Task.FromResult(Link());}
        public Task<ProviderLink> GetLinkAsync(string id,CancellationToken ct)=>Task.FromResult(Link());
        public Task<ProviderLink?> FindAsync(string reference,CancellationToken ct)=>Task.FromResult<ProviderLink?>(Link());
        public Task<ProviderPayment> GetPaymentAsync(string id,CancellationToken ct)=>Task.FromResult(new ProviderPayment(PaymentId,WrongAmount?1:attempt!.Amount,"INR","captured","order_fake"));
    }
    private sealed class Fixture:IAsyncDisposable
    {
        public IHost Host{get;}
        public TravelJourneyStore Store{get;}
        public TravelInvocationScope Identity{get;}
        public TravelHoldService Holds{get;}
        public TravelCheckoutService Checkout{get;}
        public FakeProvider Provider{get;}=new();
        private HttpContextAccessor Http{get;}=new();
        private TravelJourneyService Journey{get;}
        public Fixture()
        {
            var builder=Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string,string?>{{"ConnectionStrings:vertical-db",Environment.GetEnvironmentVariable("AGENTFORGE_TRAVEL_TEST_DB")},
                {"AGENTFORGE_CONFIG_VERSION",Guid.NewGuid().ToString()}});
            CheckoutIntegration.Configure(builder);Host=builder.Build();Store=new(builder.Configuration);
            var principal=TravelJourneyTests.Identity(Guid.NewGuid().ToString());var id=(ClaimsIdentity)principal.Identity!;
            id.RemoveClaim(id.FindFirst("agentforge:configuration")!);id.AddClaim(new("agentforge:configuration",builder.Configuration["AGENTFORGE_CONFIG_VERSION"]!));
            Http.HttpContext=new DefaultHttpContext{User=principal};Identity=new(builder.Configuration,Http);
            var catalog=new TravelScenarioCatalog(builder.Configuration);Journey=new(Store,catalog,Identity,TimeProvider.System);
            Holds=new(Store,catalog,Identity,builder.Configuration,new(Host.Services.GetRequiredService<IServiceScopeFactory>()));
            Checkout=new(Host.Services.GetRequiredService<IServiceScopeFactory>(),Identity,Provider);
        }
        public async Task<TravelHold> Hold()
        {
            await Store.InitializeAsync(Ct);
            await using(var scope=Host.Services.CreateAsyncScope())await scope.ServiceProvider.GetRequiredService<TravelCheckoutDbContext>().Database.MigrateAsync(Ct);
            var saved=await Journey.SaveAsync(new("Demo Family","Kerala",new(2026,12,12),"Ahmedabad",2,[8],5,70000,3,false),0,"save",Ct);
            var quoted=await Journey.QuoteAsync("KERALA-FIXED",saved.Revision,"quote",Ct);var q=quoted.Quotes.Single();
            Approve("hold_travel_quote",new{quoteReference=q.Reference,expectedRevision=quoted.Revision,operationId="hold",expectedTotal=q.Total,currency="INR",partySize=3,startDate="2026-12-12"});
            return (await Holds.HoldAsync(q.Reference,quoted.Revision,"hold",q.Total,"INR",3,"2026-12-12",Ct)).Hold;
        }
        public void SwitchAccount(){var id=(ClaimsIdentity)Http.HttpContext!.User.Identity!;id.RemoveClaim(id.FindFirst("agentforge:account")!);id.AddClaim(new("agentforge:account",Guid.NewGuid().ToString()));}
        public void ApproveCheckout(TravelHold h)=>Approve("create_travel_checkout",new{holdReference=h.Reference,expectedTotal=h.Quote.Total,currency="INR"});
        private void Approve(string name,object arguments)
        {var id=(ClaimsIdentity)Http.HttpContext!.User.Identity!;foreach(var c in id.FindAll("agentforge:approved-action").ToArray())id.RemoveClaim(c);id.AddClaim(new("agentforge:approved-action",JsonSerializer.Serialize(new{name,arguments})));}
        public async ValueTask DisposeAsync(){await Store.DisposeAsync();Host.Dispose();}
    }
}
