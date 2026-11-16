using Ume.LlmGateway.AdminApi.Budgets;
using Ume.LlmGateway.AdminApi.Catalog;
using Ume.LlmGateway.AdminApi.Catalog.Pricing;
using Ume.LlmGateway.AdminApi.Common;
using Ume.LlmGateway.AdminApi.DataAccess;
using Ume.LlmGateway.AdminApi.Integrations;
using Ume.LlmGateway.AdminApi.Keys;
using Ume.LlmGateway.AdminApi.Operations;
using Ume.LlmGateway.AdminApi.Organisation;
using Ume.LlmGateway.AdminApi.Reports;
using Ume.LlmGateway.AdminApi.RoutingRules;
using Ume.LlmGateway.AdminApi.Security;
using Ume.LlmGateway.AdminApi.Settings;
using Ume.LlmGateway.AdminApi.Setup;
using Ume.LlmGateway.Infrastructure.Provisioning;

namespace Ume.LlmGateway.AdminApi;

public static class AdminServices
{
    /// <summary>
    /// The admin API's options and services, by feature. Endpoints stay thin and call the scoped services, which carry the
    /// signed-in user through <see cref="AdminContext"/>; endpoints never call each other.
    /// </summary>
    public static IServiceCollection AddAdminServices(this IServiceCollection services)
    {
        services.AddValidatedOptions<AdminOptions>(AdminOptions.SectionName);
        services.AddValidatedOptions<GatewayLinkOptions>(GatewayLinkOptions.SectionName);
        services.AddValidatedOptions<DataApiLinkOptions>(DataApiLinkOptions.SectionName);
        // Only a set authority is checked: the admin API starts without one, and sign-in is then not configured.
        services.AddValidatedOptions<OidcOptions>(OidcOptions.SectionName).Validate<IHostEnvironment>((o, environment) =>
            string.IsNullOrWhiteSpace(o.Authority) || OidcOptions.IsUsableAuthority(o.Authority, !environment.IsDevelopment()), OidcOptions.AuthorityError);
        services.AddValidatedOptions<RetentionOptions>(RetentionOptions.SectionName);
        services.AddValidatedOptions<SetupOptions>(SetupOptions.SectionName);

        services.AddHttpContextAccessor();
        services.AddScoped<AdminContext>();
        services.AddScoped<OwnerScopeResolver>();
        services.AddSingleton<AdminClientAddresses>();
        services.AddSingleton<AccessDeniedThrottle>();

        services.AddScoped<OrganisationService>();
        services.AddScoped<KeyService>();
        services.AddScoped<IntegrationService>();
        services.AddScoped<IntegrationImportService>();
        services.AddSingleton<IntegrationPresets>();
        services.AddTransient<IntegrationProvisioner>();
        services.AddScoped<IntegrationApiService>();
        services.AddSingleton<IntegrationKeyStore>();
        services.AddScoped<DataAccessService>();
        services.AddScoped<BudgetAdminService>();
        services.AddScoped<RoutingRuleService>();
        services.AddScoped<ReportService>();
        services.AddScoped<UsageMetricsService>();
        services.AddScoped<PiiIncidentService>();

        services.AddScoped<ProviderCatalogService>();
        // One attempt within discovery's own 20 s timeout: an admin testing a connection should not sit through retries.
        services.AddHttpClient(ModelDiscovery.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10) })
            .RemoveStandardResilienceHandler();
        services.AddSingleton<AzureRetailPrices>();
        services.AddHttpClient(AzureRetailPrices.HttpClientName);
        services.AddSingleton<ModelListService>();
        services.AddHttpClient(ModelListService.HttpClientName);

        services.AddScoped<OperationsService>();
        services.AddScoped<ProviderStabilityService>();
        services.AddScoped<ConfigTransferService>();
        services.AddSingleton<GatewayOperationsClient>();
        services.AddHttpClient(GatewayOperationsClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

        services.AddScoped<GatewaySettingsService>();
        // The admin console's sign-in values: the deployment's, else the database's (the client secret encrypted), else the defaults.
        services.AddScoped<SignInSettingsService>();
        services.AddSingleton<SignInSecretProtector>();
        // Setup mode: the setup code, sessions and test sign-in; evaluated at start, which writes the code to standard output.
        services.AddSingleton<SetupSecret>();
        services.AddSingleton<SetupCodeThrottle>();
        services.AddSingleton<SetupEventSampler>();
        services.AddSingleton<SetupCleanup>();
        services.AddSingleton(new SetupConsole());
        services.AddSingleton(_ => SetupProviderClient.Create());
        services.AddScoped<SetupMode>();
        services.AddScoped<SetupSessions>();
        services.AddScoped<SetupTestSignIn>();
        services.AddScoped<SetupService>();
        services.AddHostedService<SetupStartup>();
        services.AddScoped<BrandingService>();
        services.AddSingleton<BrandingCache>();
        services.AddSingleton<ReleaseCheck>();
        services.AddHttpClient(ReleaseCheck.HttpClientName);

        // Background jobs, singletons so their endpoints can trigger them and read their state.
        services.AddSingleton<RetentionService>();
        services.AddHostedService(sp => sp.GetRequiredService<RetentionService>());
        services.AddSingleton<ModelListSync>();
        services.AddHostedService(sp => sp.GetRequiredService<ModelListSync>());
        return services;
    }
}
