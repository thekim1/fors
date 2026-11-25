using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Testcontainers.PostgreSql;
using Fors.Domain;
using Fors.Domain.Entities;
using Fors.Gateway.Routing;
using Fors.Infrastructure.Persistence;
using Fors.TestKit;

namespace Fors.Gateway.Tests;

/// <summary>
/// The catalogue reads providers, deployments and prices as a split query: three statements. A deployment committed on
/// an earlier provider between the deployments and the prices statements once made EF stitch the prices onto the wrong
/// deployments, so the models after it had no price until the next reload and their requests cost 0 (a flaky
/// <c>AudioTests</c> in CI). The reads now run in one REPEATABLE READ transaction. Its own database, since the test
/// writes behind the catalogue's back.
/// </summary>
public sealed class CatalogConsistencyTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Builder().Build();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync(Ct);
        await TestPostgres.MigrateAsync(_postgres.GetConnectionString(), Ct);
    }

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task A_deployment_committed_during_the_read_leaves_every_model_with_its_own_prices()
    {
        // Two providers in a fixed id order (the split query orders by it), each with a priced model.
        var first = Provider(Guid.CreateVersion7(DateTimeOffset.UtcNow.AddMinutes(-1)), "first", "first/model", 1m);
        var second = Provider(Guid.CreateVersion7(DateTimeOffset.UtcNow), "second", "second/model", 2m);
        await using (var seed = TestPostgres.Context(_postgres.GetConnectionString()))
        {
            seed.ProviderAccounts.AddRange(first, second);
            await seed.SaveChangesAsync(Ct);
        }

        // Just before the prices statement runs, another connection commits a new priced model on the first provider.
        var interceptor = new BeforePrices(async () =>
        {
            await using var other = TestPostgres.Context(_postgres.GetConnectionString());
            other.ModelDeployments.Add(Deployment(first.Id, "first/late", 9m));
            await other.SaveChangesAsync(Ct);
        });
        await using var db = new GatewayDbContext(new DbContextOptionsBuilder<GatewayDbContext>()
            .UseNpgsql(_postgres.GetConnectionString()).AddInterceptors(interceptor).Options);

        var rows = await GatewayCatalog.ReadRowsAsync(db, Ct);

        interceptor.Ran.ShouldBeTrue();
        var deployments = rows.Providers.SelectMany(p => p.Deployments).ToDictionary(d => d.Name);
        deployments.Keys.Order().ShouldBe(["first/model", "second/model"]); // the late model is not in this snapshot
        deployments["first/model"].Prices.Single().InputPerMillionUsd.ShouldBe(1m);
        deployments["second/model"].Prices.Single().InputPerMillionUsd.ShouldBe(2m);
    }

    private static ProviderAccount Provider(Guid id, string name, string model, decimal input)
    {
        var provider = new ProviderAccount
        {
            Id = id, Name = name, Type = ProviderType.OpenAICompatible, BaseUrl = "http://localhost:1/v1", Residency = DataResidency.Eu,
            Capabilities = ProviderCapabilities.ChatCompletions, CreatedAt = DateTimeOffset.UtcNow,
        };
        provider.Deployments.Add(Deployment(provider.Id, model, input));
        return provider;
    }

    private static ModelDeployment Deployment(Guid provider, string name, decimal input) => new()
    {
        Name = name, UpstreamModel = name, Kind = ModelKind.Chat, ProviderAccountId = provider,
        Prices = [new ModelPrice { InputPerMillionUsd = input, OutputPerMillionUsd = input, EffectiveFrom = DateTimeOffset.UtcNow.AddDays(-1) }],
    };

    /// <summary>Runs <paramref name="action"/> once, before the first statement that reads <c>ModelPrices</c>.</summary>
    private sealed class BeforePrices(Func<Task> action) : DbCommandInterceptor
    {
        public bool Ran { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!Ran && command.CommandText.Contains("\"ModelPrices\"", StringComparison.Ordinal))
            {
                Ran = true;
                await action();
            }

            return result;
        }
    }
}
