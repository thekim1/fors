using Ume.LlmGateway.Infrastructure;
using Ume.LlmGateway.Infrastructure.Provisioning;
using Ume.LlmGateway.MigrationService;

var builder = Host.CreateApplicationBuilder(args);
builder.AddDeploymentSecrets();
builder.AddServiceDefaults();
builder.AddGatewayDatabase();
builder.AddGatewaySecurity();
builder.Services.LoadKeyRingOnFirstUse();
builder.Services.AddValidatedOptions<SeedOptions>(SeedOptions.SectionName);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddTransient<DevSeeder>();
builder.Services.AddValidatedOptions<ProvisioningOptions>(ProvisioningOptions.SectionName);
builder.Services.AddTransient<IntegrationProvisioner>();
builder.Services.AddTransient<IntegrationManifests>();
builder.Services.AddHostedService<MigrationWorker>();

var host = builder.Build();
host.Run();
