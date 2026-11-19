using System.Net;
using System.Text;
using AgentForge.Verticals.Travel.Checkout;
using Microsoft.Extensions.Configuration;

namespace AgentForge.Verticals.Travel.Tests;

public sealed class RazorpayContractTests
{
    [Fact]
    public async Task Reconciliation_reads_the_provider_payment_links_collection_and_filters_exact_reference()
    {
        var handler=new ResponseHandler("""
            {"payment_links":[{"id":"plink_1","reference_id":"CO-ours","amount":6300000,"amount_paid":0,"currency":"INR","status":"created","short_url":"https://rzp.io/i/demo","expire_by":1800000000,"payments":[]}]}
            """);
        using var http=new HttpClient(handler){BaseAddress=new("https://api.razorpay.com/v1/")};
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"VerticalPayments:TestKeyId","rzp_test_contract"},{"VerticalPayments:TestKeySecret","contract-secret"}}).Build();
        var client=new RazorpayClient(http,config);
        Assert.Equal("plink_1",(await client.FindAsync("CO-ours",TestContext.Current.CancellationToken))!.Id);
        Assert.Equal(HttpMethod.Get,handler.Method);Assert.Contains("reference_id=CO-ours",handler.Path);
        Assert.Null(await client.FindAsync("CO-other",TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task Live_credentials_are_rejected_before_any_provider_request()
    {
        var handler=new ResponseHandler("{}");using var http=new HttpClient(handler){BaseAddress=new("https://api.razorpay.com/v1/")};
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"VerticalPayments:TestKeyId","rzp_live_forbidden"},{"VerticalPayments:TestKeySecret","contract-secret"}}).Build();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>new RazorpayClient(http,config).GetLinkAsync("plink_1",TestContext.Current.CancellationToken));
        Assert.Null(handler.Method);
    }
    private sealed class ResponseHandler(string body):HttpMessageHandler
    {
        public HttpMethod? Method;public string? Path;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {Method=request.Method;Path=request.RequestUri!.PathAndQuery;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")});}
    }
}
