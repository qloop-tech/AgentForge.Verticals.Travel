using AgentForge.Verticals.Travel.Checkout;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Routing;
using AgentForge.Verticals.Abstractions;
using AgentForge.Verticals.Travel.Journey;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentForge.Verticals.Travel;

public sealed class TravelVerticalPlugin : IVerticalPlugin
    , IVerticalDeploymentValidator
{
    public IVerticalMcpRegistrar McpRegistrar { get; } = new TravelMcpRegistrar();

    public void ConfigureConfiguration(IConfigurationManager configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        TravelConfigurationFiles.AddConfigurationSources(configuration);
    }

    public void RegisterCommonServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpContextAccessor();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<TravelInvocationScope>();
        services.AddSingleton<TravelScenarioCatalog>();
        services.AddSingleton<TravelJourneyStore>();
        services.AddSingleton<TravelJourneyService>();
        services.AddSingleton<TravelHoldService>();
        services.AddSingleton<Func<AIFunction, AIFunction>>(_ => TravelHoldApprovalPolicy.Apply);
        services.AddSingleton<Func<ToolApprovalRequestContent, string?>>(_ => TravelHoldApprovalPolicy.Describe);
        services.AddHostedService<TravelSchemaInitializer>();
        services.AddSingleton((Action<IHostApplicationBuilder>)CheckoutIntegration.Configure);
        services.AddHostedService<CheckoutSchemaInitializer>();
        services.AddSingleton<TravelCheckoutGuard>();
        services.AddSingleton<TravelCheckoutService>();
        services.AddHttpClient<RazorpayClient>(http => { http.BaseAddress = new Uri("https://api.razorpay.com/v1/"); http.Timeout = TimeSpan.FromSeconds(30); });
        services.AddTransient<ITravelPaymentProvider>(sp => sp.GetRequiredService<RazorpayClient>());
        services.AddSingleton<AIContextProvider, TravelCheckoutContextProvider>();
        services.AddSingleton<AIContextProvider, TravelJourneyContextProvider>();
        services.AddSingleton<IValidateOptions<TravelCustomerProfileOptions>, TravelCustomerProfileOptionsValidator>();
        services.AddOptions<TravelCustomerProfileOptions>()
            .BindConfiguration(TravelCustomerProfileOptions.SectionName)
            .ValidateOnStart();
    }

    public void RegisterWebApiServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IScheduledActionHandler, TravelScheduledActionHandler>();
        services.AddSingleton((Action<IEndpointRouteBuilder>)CheckoutEndpoints.Map);
        services.AddHostedService<CheckoutProcessor>();
    }

    public IVerticalDescriptor CreateDescriptor(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var options = serviceProvider.GetRequiredService<IOptions<TravelCustomerProfileOptions>>().Value;
        var promptPath = TravelConfigurationFiles.ResolvePromptPath(configuration, options.PromptFileName);
        var prompt = File.ReadAllText(promptPath);

        return new ResolvedTravelVerticalDescriptor(options, prompt);
    }

    public string ResolveMcpServerName(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return TravelCustomerProfileBinder.ResolveRequiredMcpServerName(configuration);
    }

    public void ValidateDeployment()
    {
        TravelDataFiles.EnsureDataDirectoryExists();
        TravelAssetFiles.EnsureAssetDirectoryExists();
        TravelConfigurationFiles.EnsureDefaultFilesExist();
    }
}
