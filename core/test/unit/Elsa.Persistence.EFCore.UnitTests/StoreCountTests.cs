using System.Data.Common;
using Elsa.Common.Models;
using Elsa.Persistence.EFCore.Extensions;
using Elsa.Persistence.EFCore.Modules.Runtime;
using Elsa.Persistence.EFCore.Sqlite;
using Elsa.Workflows;
using Elsa.Workflows.Runtime.Entities;
using Elsa.Workflows.Runtime.Filters;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Elsa.Persistence.EFCore.UnitTests;

/// <summary>
/// A page's total is counted in the database. The stores used to load every matching row — for the execution log, every
/// record of an instance with its activity state and payload — only to count them in memory, so each page of a long
/// workflow's journal read its whole history.
/// </summary>
public sealed class StoreCountTests : IAsyncDisposable
{
    private readonly string _databasePath = Path.Join(Path.GetTempPath(), $"elsa-store-count-{Guid.NewGuid():N}.db");
    private readonly CommandCapture _commands = new();
    private readonly ServiceProvider _services;

    public StoreCountTests()
    {
        _services = new ServiceCollection()
            .AddLogging()
            .AddSqliteEntityModelCreatingHandlers()
            .AddDbContextFactory<RuntimeElsaDbContext>((_, builder) =>
                builder.UseElsaSqlite(typeof(RuntimeDbContextFactory).Assembly, $"Data Source={_databasePath};Default Timeout=30")
                    .AddInterceptors(_commands))
            .AddScoped<EntityStore<RuntimeElsaDbContext, WorkflowExecutionLogRecord>>()
            .AddScoped<EFCoreWorkflowExecutionLogStore>()
            .AddSingleton(Substitute.For<ISafeSerializer>())
            .BuildServiceProvider();
    }

    [Fact]
    public async Task The_execution_log_counts_a_page_in_the_database_and_loads_only_the_page()
    {
        await SeedAsync("long-instance", 5);
        await SeedAsync("other-instance", 3);
        var store = _services.GetRequiredService<EFCoreWorkflowExecutionLogStore>();
        _commands.Commands.Clear();

        var page = await store.FindManyAsync(
            new WorkflowExecutionLogRecordFilter { WorkflowInstanceId = "long-instance" },
            PageArgs.FromRange(0, 2));

        Assert.Equal(5, page.TotalCount);
        Assert.Equal([0L, 1L], page.Items.Select(x => x.Sequence));
        Assert.Contains(_commands.Commands, sql => sql.Contains("COUNT(", StringComparison.OrdinalIgnoreCase));
        // Every query that reads the records' activity state also limits how many it reads.
        var stateQueries = _commands.Commands.Where(sql => sql.Contains("SerializedActivityState", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(stateQueries);
        Assert.All(stateQueries, sql => Assert.Contains("LIMIT", sql, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CountAsync_applies_the_query_once()
    {
        await SeedAsync("instance", 5);
        var store = _services.GetRequiredService<EntityStore<RuntimeElsaDbContext, WorkflowExecutionLogRecord>>();

        // Of five records, skipping one leaves 4 when the query is applied once, and 3 when it is applied twice.
        var count = await store.CountAsync(query => query.OrderBy(x => x.Sequence).Skip(1), CancellationToken.None);

        Assert.Equal(4, count);
    }

    private async Task SeedAsync(string instanceId, int count)
    {
        await using var dbContext = await _services.GetRequiredService<IDbContextFactory<RuntimeElsaDbContext>>().CreateDbContextAsync();
        await dbContext.Database.EnsureCreatedAsync();
        for (var i = 0; i < count; i++)
        {
            dbContext.Set<WorkflowExecutionLogRecord>().Add(new WorkflowExecutionLogRecord
            {
                Id = $"{instanceId}-{i}",
                WorkflowDefinitionId = "definition",
                WorkflowDefinitionVersionId = "definition:1",
                WorkflowInstanceId = instanceId,
                ActivityInstanceId = $"activity-instance-{i}",
                ActivityId = "activity",
                ActivityType = "Elsa.WriteLine",
                ActivityNodeId = "node",
                Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(i),
                Sequence = i,
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

    /// <summary>Collects the text of every command EF Core runs.</summary>
    private sealed class CommandCapture : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            lock (Commands) Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            lock (Commands) Commands.Add(command.CommandText);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
