using Elsa.Common.Models;
using Elsa.Common.Multitenancy;
using Elsa.Persistence.EFCore.EntityHandlers;
using Elsa.Persistence.EFCore.Extensions;
using Elsa.Persistence.EFCore.Modules.Management;
using Elsa.Persistence.EFCore.Sqlite;
using Elsa.Tenants.Options;
using Elsa.Testing.Shared.Multitenancy;
using Elsa.Workflows;
using Elsa.Workflows.Management.Entities;
using Elsa.Workflows.Management.Filters;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Elsa.Persistence.EFCore.UnitTests;

/// <summary>
/// A page of workflow definitions counts the same rows it returns: across tenants when the filter is tenant-agnostic, and
/// the current tenant's otherwise.
/// </summary>
public sealed class WorkflowDefinitionStoreCountTests : IAsyncDisposable
{
    private readonly string _databasePath = Path.Join(Path.GetTempPath(), $"elsa-definition-count-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;

    public WorkflowDefinitionStoreCountTests()
    {
        _services = new ServiceCollection()
            .AddLogging()
            .AddSqliteEntityModelCreatingHandlers()
            .AddScoped<IEntityModelCreatingHandler, SetTenantIdFilter>()
            .AddSingleton(Options.Create(new TenantsOptions { IsEnabled = true }))
            .AddSingleton<ITenantAccessor>(new TestTenantAccessor("tenant-a"))
            // The tenant filter is part of the model, so this context must not share a cached model with one built without it.
            .AddDbContextFactory<ManagementElsaDbContext>((_, builder) =>
                builder.UseElsaSqlite(typeof(ManagementDbContextFactory).Assembly, $"Data Source={_databasePath};Default Timeout=30")
                    .EnableServiceProviderCaching(false))
            .AddScoped<EntityStore<ManagementElsaDbContext, WorkflowDefinition>>()
            .AddScoped<EFCoreWorkflowDefinitionStore>()
            .AddSingleton(Substitute.For<IPayloadSerializer>())
            .BuildServiceProvider();
    }

    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    public async Task FindManyAsync_CountsTheRowsItReturns(bool tenantAgnostic, int expected)
    {
        await SeedAsync(("definition-a:1", "tenant-a"), ("definition-b:1", "tenant-b"));
        var store = _services.GetRequiredService<EFCoreWorkflowDefinitionStore>();

        var page = await store.FindManyAsync(new WorkflowDefinitionFilter { TenantAgnostic = tenantAgnostic }, PageArgs.FromRange(0, 10));

        Assert.Equal(expected, page.Items.Count);
        Assert.Equal(expected, page.TotalCount);
    }

    private async Task SeedAsync(params (string Id, string TenantId)[] rows)
    {
        await using var dbContext = await _services.GetRequiredService<IDbContextFactory<ManagementElsaDbContext>>().CreateDbContextAsync();
        await dbContext.Database.EnsureCreatedAsync();
        foreach (var (id, tenantId) in rows)
        {
            dbContext.Set<WorkflowDefinition>().Add(new WorkflowDefinition
            {
                Id = id,
                DefinitionId = id.Split(':')[0],
                Version = 1,
                MaterializerName = "Json",
                TenantId = tenantId,
                CreatedAt = DateTimeOffset.UnixEpoch,
            });
        }

        await dbContext.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
            File.Delete(_databasePath);
    }
}
