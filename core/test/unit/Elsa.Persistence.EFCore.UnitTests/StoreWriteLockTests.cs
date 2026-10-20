using Elsa.Common.Multitenancy;
using Elsa.Persistence.EFCore;
using Elsa.Tenants.Options;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;

namespace Elsa.Persistence.EFCore.UnitTests;

/// <summary>
/// The per-entity write lock in <see cref="Store{TDbContext,TEntity}"/> is skipped only on PostgreSQL and MySQL, whose
/// upserts settle two writers of the same key in the database; SQLite (one writer per file), SQL Server and Oracle (MERGE)
/// keep taking turns. Each test uses its own entity type, because the lock is static per closed store type.
/// </summary>
public sealed class StoreWriteLockTests : IDisposable
{
    private readonly string _databaseFile = Path.Combine(Path.GetTempPath(), $"store-write-lock-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task SaveAsync_OnSqlite_SavesOfTheSameEntityTypeTakeTurns()
    {
        var store = CreateStore<SqliteLockEntity>(FakeProvider.None);

        var maxInFlight = await RunConcurrentSavesAsync(store, (id, value) => new SqliteLockEntity { Id = id, Value = value });

        Assert.Equal(1, maxInFlight);
    }

    [Fact]
    public async Task SaveAsync_OnAServerDatabase_SavesRunConcurrently()
    {
        var store = CreateStore<ServerLockEntity>(FakeProvider.PostgreSql);

        var maxInFlight = await RunConcurrentSavesAsync(store, (id, value) => new ServerLockEntity { Id = id, Value = value });

        Assert.True(maxInFlight > 1, $"saves ran one at a time (max {maxInFlight} in flight)");
    }

    [Fact]
    public async Task SaveAsync_OnSqlServer_SavesOfTheSameEntityTypeTakeTurns()
    {
        var store = CreateStore<SqlServerLockEntity>(FakeProvider.SqlServer);

        var maxInFlight = await RunConcurrentSavesAsync(store, (id, value) => new SqlServerLockEntity { Id = id, Value = value });

        Assert.Equal(1, maxInFlight);
    }

    [Fact]
    public async Task SaveManyAsync_WithTenancyOnAServerDatabase_SavesTakeTurns()
    {
        var tenantAccessor = Substitute.For<ITenantAccessor>();
        tenantAccessor.TenantId.Returns(string.Empty);
        var services = new ServiceCollection()
            .AddSingleton(tenantAccessor)
            .AddSingleton(Options.Create(new TenantsOptions { IsEnabled = true }))
            .BuildServiceProvider();
        var store = CreateStore<TenancyLockEntity>(FakeProvider.PostgreSql, services: services);
        var inFlight = 0;
        var maxInFlight = 0;

        // Each save stops in its callback, before the upsert: only whether the callbacks overlapped is measured.
        var saves = Enumerable.Range(0, 6).Select(i => Task.Run(() => store.SaveManyAsync(
            [new TenancyLockEntity { Id = $"entity-{i}", Value = "value" }],
            x => x.Id,
            async (_, _, ct) =>
            {
                InterlockedMax(ref maxInFlight, Interlocked.Increment(ref inFlight));
                await Task.Delay(150, ct);
                Interlocked.Decrement(ref inFlight);
                throw new StopSaveException();
            }))).ToList();

        foreach (var save in saves)
            await Assert.ThrowsAsync<StopSaveException>(() => save);
        Assert.Equal(1, maxInFlight);
    }

    [Fact]
    public async Task SaveAsync_OnPostgreSql_RetriesADeadlock()
    {
        var deadlock = new FailOnceInterceptor(() => new PostgresException("deadlock detected", "ERROR", "ERROR", "40P01"));
        var store = CreateStore<DeadlockEntity>(FakeProvider.PostgreSql, deadlock);

        await store.SaveAsync(new DeadlockEntity { Id = "deadlocked", Value = "ours" }, x => x.Id);

        await using var check = CreateContext<DeadlockEntity>(FakeProvider.None);
        var saved = await check.Set<DeadlockEntity>().SingleAsync();
        Assert.True(deadlock.Failed);
        Assert.Equal("ours", saved.Value);
    }

    [Fact]
    public async Task QueryAsync_WithASelector_AppliesAPagingQueryOnce()
    {
        var store = CreateStore<PagingEntity>(FakeProvider.None);
        foreach (var i in Enumerable.Range(0, 5))
            await store.SaveAsync(new PagingEntity { Id = $"entity-{i}", Value = "value" }, x => x.Id);

        var ids = await store.QueryAsync(q => q.OrderBy(x => x.Id).Skip(2).Take(2), x => x.Id, CancellationToken.None);

        Assert.Equal(["entity-2", "entity-3"], ids);
    }

    [Fact]
    public async Task SaveAsync_WhenAnotherWriterInsertsTheSameKeyFirst_UpdatesTheRowInstead()
    {
        var racer = new RacingInsertInterceptor("racing", "theirs");
        var store = CreateStore<RaceEntity>(FakeProvider.None, racer);

        await store.SaveAsync(new RaceEntity { Id = "racing", Value = "ours" }, x => x.Id);

        await using var check = CreateContext<RaceEntity>(FakeProvider.None);
        var saved = await check.Set<RaceEntity>().SingleAsync();
        Assert.True(racer.Inserted);
        Assert.Equal("ours", saved.Value);
    }

    /// <summary>Saves six entities at once; each holds its save open for a moment, and the highest number of saves in
    /// that moment together is returned.</summary>
    private static async Task<int> RunConcurrentSavesAsync<TEntity>(Store<LockDbContext<TEntity>, TEntity> store, Func<string, string, TEntity> create)
        where TEntity : class, ILockEntity, new()
    {
        var inFlight = 0;
        var maxInFlight = 0;

        await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(() => store.SaveAsync(
            create($"entity-{i}", "value"),
            x => x.Id,
            async (_, _, ct) =>
            {
                var now = Interlocked.Increment(ref inFlight);
                InterlockedMax(ref maxInFlight, now);
                await Task.Delay(150, ct);
                Interlocked.Decrement(ref inFlight);
            }))));

        return maxInFlight;
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private Store<LockDbContext<TEntity>, TEntity> CreateStore<TEntity>(FakeProvider provider, IInterceptor? interceptor = null, IServiceProvider? services = null)
        where TEntity : class, ILockEntity, new()
    {
        // The schema is created through the real SQLite provider; the store's own contexts may report another one.
        using (var setup = CreateContext<TEntity>(FakeProvider.None))
            setup.Database.EnsureCreated();

        return new Store<LockDbContext<TEntity>, TEntity>(
            new Factory<TEntity>(() => CreateContext<TEntity>(provider, interceptor)),
            services ?? new ServiceCollection().BuildServiceProvider());
    }

    private LockDbContext<TEntity> CreateContext<TEntity>(FakeProvider provider, IInterceptor? interceptor = null)
        where TEntity : class, ILockEntity, new()
    {
        var builder = new DbContextOptionsBuilder<LockDbContext<TEntity>>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = _databaseFile, Pooling = false, DefaultTimeout = 30 }.ToString());
        if (provider == FakeProvider.PostgreSql)
            builder.ReplaceService<IDatabaseProvider, FakePostgreSqlDatabaseProvider>();
        if (provider == FakeProvider.SqlServer)
            builder.ReplaceService<IDatabaseProvider, FakeSqlServerDatabaseProvider>();
        if (interceptor != null)
            builder.AddInterceptors(interceptor);
        return new LockDbContext<TEntity>(builder.Options);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databaseFile))
            File.Delete(_databaseFile);
    }

    public interface ILockEntity
    {
        string Id { get; set; }
        string Value { get; set; }
    }

    public sealed class SqliteLockEntity : ILockEntity
    {
        public string Id { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    public sealed class ServerLockEntity : ILockEntity
    {
        public string Id { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    public sealed class SqlServerLockEntity : ILockEntity
    {
        public string Id { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    public sealed class TenancyLockEntity : ILockEntity
    {
        public string Id { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    public sealed class DeadlockEntity : ILockEntity
    {
        public string Id { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    public sealed class PagingEntity : ILockEntity
    {
        public string Id { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    public sealed class RaceEntity : ILockEntity
    {
        public string Id { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    public sealed class LockDbContext<TEntity>(DbContextOptions<LockDbContext<TEntity>> options) : DbContext(options)
        where TEntity : class, ILockEntity
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<TEntity>().ToTable("Entities").HasKey(x => x.Id);
    }

    private sealed class Factory<TEntity>(Func<LockDbContext<TEntity>> create) : IDbContextFactory<LockDbContext<TEntity>>
        where TEntity : class, ILockEntity
    {
        public LockDbContext<TEntity> CreateDbContext() => create();
    }

    /// <summary>Stands in for another writer: inserts the key the save is about to insert, just before it does.</summary>
    private sealed class RacingInsertInterceptor(string id, string value) : SaveChangesInterceptor
    {
        public bool Inserted { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Inserted && eventData.Context is { } context
                && context.ChangeTracker.Entries().Any(e => e.State == EntityState.Added))
            {
                Inserted = true;
                await context.Database.ExecuteSqlRawAsync(
                    "INSERT INTO \"Entities\" (\"Id\", \"Value\") VALUES ({0}, {1})", [id, value], cancellationToken);
            }

            return result;
        }
    }

    /// <summary>Fails the first save with the given exception, as the database would on a deadlock.</summary>
    private sealed class FailOnceInterceptor(Func<Exception> create) : SaveChangesInterceptor
    {
        public bool Failed { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Failed)
                return ValueTask.FromResult(result);

            Failed = true;
            throw create();
        }
    }

    private sealed class StopSaveException : Exception;

    public enum FakeProvider
    {
        None,
        PostgreSql,
        SqlServer,
    }

    private sealed class FakeSqlServerDatabaseProvider : IDatabaseProvider
    {
        public string Name => "Microsoft.EntityFrameworkCore.SqlServer";

        public bool IsConfigured(IDbContextOptions options) => true;
    }

    private sealed class FakePostgreSqlDatabaseProvider : IDatabaseProvider
    {
        public string Name => "Npgsql.EntityFrameworkCore.PostgreSQL";

        public bool IsConfigured(IDbContextOptions options) => true;
    }
}
