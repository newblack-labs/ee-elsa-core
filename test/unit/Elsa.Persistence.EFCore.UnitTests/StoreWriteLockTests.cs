using System.Data.Common;
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
/// The per-entity write lock in <see cref="Store{TDbContext,TEntity}"/> is skipped only on PostgreSQL and MySQL without
/// tenancy, whose upserts settle two writers of the same key in the database; SQLite (one writer per file), SQL Server and
/// Oracle (MERGE) keep taking turns, and so does every provider with tenancy on. The server providers are faked by name
/// over SQLite. Each test uses its own entity type, because the lock is static per closed store type.
/// </summary>
public sealed class StoreWriteLockTests : IDisposable
{
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);

    private readonly string _databaseFile = Path.Combine(Path.GetTempPath(), $"store-write-lock-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task SaveAsync_OnSqlite_SavesOfTheSameEntityTypeTakeTurns()
    {
        var store = CreateStore<SqliteLockEntity>(FakeProvider.None);

        var maxInFlight = await MaxOverlapAsync<SqliteLockEntity>((i, hold) => store.SaveAsync(new() { Id = $"entity-{i}" }, x => x.Id, hold));

        Assert.Equal(1, maxInFlight);
    }

    [Fact]
    public async Task SaveAsync_OnPostgreSql_SavesRunConcurrently()
    {
        var store = CreateStore<ServerLockEntity>(FakeProvider.PostgreSql);

        var maxInFlight = await MaxOverlapAsync<ServerLockEntity>((i, hold) => store.SaveAsync(new() { Id = $"entity-{i}" }, x => x.Id, hold));

        Assert.True(maxInFlight > 1, $"saves ran one at a time (max {maxInFlight} in flight)");
    }

    [Fact]
    public async Task SaveAsync_OnMySql_SavesRunConcurrently()
    {
        var store = CreateStore<MySqlLockEntity>(FakeProvider.MySql);

        var maxInFlight = await MaxOverlapAsync<MySqlLockEntity>((i, hold) => store.SaveAsync(new() { Id = $"entity-{i}" }, x => x.Id, hold));

        Assert.True(maxInFlight > 1, $"saves ran one at a time (max {maxInFlight} in flight)");
    }

    [Fact]
    public async Task SaveAsync_OnSqlServer_SavesOfTheSameEntityTypeTakeTurns()
    {
        var store = CreateStore<SqlServerLockEntity>(FakeProvider.SqlServer);

        var maxInFlight = await MaxOverlapAsync<SqlServerLockEntity>((i, hold) => store.SaveAsync(new() { Id = $"entity-{i}" }, x => x.Id, hold));

        Assert.Equal(1, maxInFlight);
    }

    [Fact]
    public async Task SaveAsync_OnOracle_SavesOfTheSameEntityTypeTakeTurns()
    {
        var store = CreateStore<OracleLockEntity>(FakeProvider.Oracle);

        var maxInFlight = await MaxOverlapAsync<OracleLockEntity>((i, hold) => store.SaveAsync(new() { Id = $"entity-{i}" }, x => x.Id, hold));

        Assert.Equal(1, maxInFlight);
    }

    [Fact]
    public async Task AddManyAsync_OnSqlite_WritesTakeTurns()
    {
        var store = CreateStore<SqliteAddLockEntity>(FakeProvider.None);

        var maxInFlight = await MaxOverlapAsync<SqliteAddLockEntity>((i, hold) => store.AddManyAsync([new() { Id = $"entity-{i}" }], hold));

        Assert.Equal(1, maxInFlight);
    }

    [Fact]
    public async Task AddManyAsync_OnPostgreSql_WritesRunConcurrently()
    {
        var store = CreateStore<ServerAddLockEntity>(FakeProvider.PostgreSql);

        var maxInFlight = await MaxOverlapAsync<ServerAddLockEntity>((i, hold) => store.AddManyAsync([new() { Id = $"entity-{i}" }], hold));

        Assert.True(maxInFlight > 1, $"writes ran one at a time (max {maxInFlight} in flight)");
    }

    [Fact]
    public async Task SaveManyAsync_OnPostgreSql_SavesRunConcurrently()
    {
        var store = CreateStore<ServerSaveManyLockEntity>(FakeProvider.PostgreSql, services: CreateServices(tenancyEnabled: false));

        var maxInFlight = await MaxOverlapAsync<ServerSaveManyLockEntity>((i, hold) => store.SaveManyAsync([new() { Id = $"entity-{i}" }], x => x.Id, hold));

        Assert.True(maxInFlight > 1, $"saves ran one at a time (max {maxInFlight} in flight)");
    }

    [Fact]
    public async Task WithTenancyOnPostgreSql_SaveManyAsync_SaveAsync_And_AddManyAsync_TakeTurns()
    {
        var store = CreateStore<TenancyLockEntity>(FakeProvider.PostgreSql, services: CreateServices(tenancyEnabled: true));

        // SaveManyAsync's ownership check and upsert are two statements: a SaveAsync or AddManyAsync of the same key by
        // another tenant must not land between them (#8490).
        var maxInFlight = await MaxOverlapAsync<TenancyLockEntity>((i, hold) => (i % 3) switch
        {
            0 => store.SaveManyAsync([new() { Id = "shared" }], x => x.Id, hold),
            1 => store.SaveAsync(new() { Id = "shared" }, x => x.Id, hold),
            _ => store.AddManyAsync([new() { Id = "shared" }], hold),
        });

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
    public async Task SaveManyAsync_WhenALaterChunkDeadlocks_TheRetryStartsFromNothingSaved()
    {
        // 60 rows are two upsert statements of 50 and 10; the second one deadlocks once.
        var upserts = new UpsertCommandInterceptor(_databaseFile, failOnUpsert: 2);
        var store = CreateStore<ChunkEntity>(FakeProvider.PostgreSql, upserts, CreateServices(tenancyEnabled: false));
        var entities = Enumerable.Range(0, 60).Select(i => new ChunkEntity { Id = $"k{i:D2}", Value = "ours" }).ToList();

        await store.SaveManyAsync(entities, x => x.Id, CancellationToken.None);

        // Upserts 1 and 2 were the failed attempt, 3 and 4 the retry. Before the retry's first chunk, the failed attempt's
        // first chunk is gone: had it stayed, the retry would write it a second time, over anything written in between.
        Assert.Equal(4, upserts.RowsBeforeUpsert.Count);
        Assert.Equal(0, upserts.RowsBeforeUpsert[2]);
        await using var check = CreateContext<ChunkEntity>(FakeProvider.None);
        Assert.Equal(60, await check.Set<ChunkEntity>().CountAsync());
    }

    [Fact]
    public async Task SaveManyAsync_OnPostgreSql_UpsertsInKeyOrder()
    {
        var upserts = new UpsertCommandInterceptor(_databaseFile);
        var store = CreateStore<KeyOrderEntity>(FakeProvider.PostgreSql, upserts, CreateServices(tenancyEnabled: false));

        await store.SaveManyAsync(new[] { "c", "a", "b" }.Select(id => new KeyOrderEntity { Id = id, Value = "value" }), x => x.Id, CancellationToken.None);

        Assert.Equal(["a", "b", "c"], upserts.KeyParameters);
    }

    [Fact]
    public void TheFakeProviders_HaveTheNamesOfTheRealOnes()
    {
        using var postgreSql = new DbContext(new DbContextOptionsBuilder().UseNpgsql("Host=localhost").Options);
        using var oracle = new DbContext(new DbContextOptionsBuilder().UseOracle("Data Source=localhost").Options);

        Assert.Equal(postgreSql.Database.ProviderName, new FakePostgreSqlDatabaseProvider().Name);
        Assert.Equal(oracle.Database.ProviderName, new FakeOracleDatabaseProvider().Name);
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

    /// <summary>
    /// Starts six writes at once. Each holds its callback open for a moment and then stops the write, so only whether the
    /// callbacks overlapped is measured, not the SQL a faked provider would send; the highest overlap is returned. A write
    /// that never finishes (a lock that is not released) fails the test instead of hanging it.
    /// </summary>
    private static async Task<int> MaxOverlapAsync<TEntity>(Func<int, Func<LockDbContext<TEntity>, TEntity, CancellationToken, ValueTask>, Task> write)
        where TEntity : class, ILockEntity, new()
    {
        var inFlight = 0;
        var maxInFlight = 0;

        async ValueTask Hold(LockDbContext<TEntity> _, TEntity __, CancellationToken ct)
        {
            InterlockedMax(ref maxInFlight, Interlocked.Increment(ref inFlight));
            await Task.Delay(150, ct);
            Interlocked.Decrement(ref inFlight);
            throw new StopWriteException();
        }

        var writes = Enumerable.Range(0, 6).Select(i => Task.Run(() => write(i, Hold))).ToList();
        foreach (var running in writes)
            await Assert.ThrowsAsync<StopWriteException>(() => running.WaitAsync(WriteTimeout));

        return maxInFlight;
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private static IServiceProvider CreateServices(bool tenancyEnabled)
    {
        var tenantAccessor = Substitute.For<ITenantAccessor>();
        tenantAccessor.TenantId.Returns(string.Empty);
        return new ServiceCollection()
            .AddSingleton(tenantAccessor)
            .AddSingleton(Options.Create(new TenantsOptions { IsEnabled = tenancyEnabled }))
            .BuildServiceProvider();
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
            .UseSqlite(ConnectionString(_databaseFile));
        switch (provider)
        {
            case FakeProvider.PostgreSql:
                builder.ReplaceService<IDatabaseProvider, FakePostgreSqlDatabaseProvider>();
                break;
            case FakeProvider.MySql:
                builder.ReplaceService<IDatabaseProvider, FakeMySqlDatabaseProvider>();
                break;
            case FakeProvider.SqlServer:
                builder.ReplaceService<IDatabaseProvider, FakeSqlServerDatabaseProvider>();
                break;
            case FakeProvider.Oracle:
                builder.ReplaceService<IDatabaseProvider, FakeOracleDatabaseProvider>();
                break;
        }

        if (interceptor != null)
            builder.AddInterceptors(interceptor);
        return new LockDbContext<TEntity>(builder.Options);
    }

    private static string ConnectionString(string databaseFile) =>
        new SqliteConnectionStringBuilder { DataSource = databaseFile, Pooling = false, DefaultTimeout = 30 }.ToString();

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

    public sealed class MySqlLockEntity : ILockEntity
    {
        public string Id { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    public sealed class SqlServerLockEntity : ILockEntity
    {
        public string Id { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    public sealed class OracleLockEntity : ILockEntity
    {
        public string Id { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    public sealed class SqliteAddLockEntity : ILockEntity
    {
        public string Id { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    public sealed class ServerAddLockEntity : ILockEntity
    {
        public string Id { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    public sealed class ServerSaveManyLockEntity : ILockEntity
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

    public sealed class ChunkEntity : ILockEntity
    {
        public string Id { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    public sealed class KeyOrderEntity : ILockEntity
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
        // The PostgreSQL upsert names the table with its schema; SQLite accepts "main" for its own database.
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<TEntity>().ToTable("Entities", "main").HasKey(x => x.Id);
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

    /// <summary>
    /// Watches the bulk upsert's statements: counts the stored rows (on a connection of its own) before each, records the
    /// keys the first one sends, and can fail one of them with a PostgreSQL deadlock.
    /// </summary>
    private sealed class UpsertCommandInterceptor(string databaseFile, int? failOnUpsert = null) : DbCommandInterceptor
    {
        private int _upserts;

        public List<long> RowsBeforeUpsert { get; } = [];
        public List<string> KeyParameters { get; } = [];

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.StartsWith("INSERT INTO \"main\".\"Entities\"", StringComparison.Ordinal))
                return result;

            var upsert = ++_upserts;
            RowsBeforeUpsert.Add(await CountRowsAsync(cancellationToken));

            if (upsert == 1)
                KeyParameters.AddRange(command.Parameters.Cast<DbParameter>().Select(p => p.Value).OfType<string>().Where(v => v != "value"));

            if (upsert == failOnUpsert)
                throw new PostgresException("deadlock detected", "ERROR", "ERROR", "40P01");

            return result;
        }

        private async Task<long> CountRowsAsync(CancellationToken cancellationToken)
        {
            await using var connection = new SqliteConnection(ConnectionString(databaseFile));
            await connection.OpenAsync(cancellationToken);
            await using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM \"Entities\"";
            return (long)(await count.ExecuteScalarAsync(cancellationToken))!;
        }
    }

    private sealed class StopWriteException : Exception;

    public enum FakeProvider
    {
        None,
        PostgreSql,
        MySql,
        SqlServer,
        Oracle,
    }

    private sealed class FakePostgreSqlDatabaseProvider : IDatabaseProvider
    {
        public string Name => "Npgsql.EntityFrameworkCore.PostgreSQL";

        public bool IsConfigured(IDbContextOptions options) => true;
    }

    private sealed class FakeMySqlDatabaseProvider : IDatabaseProvider
    {
        public string Name => "Pomelo.EntityFrameworkCore.MySql";

        public bool IsConfigured(IDbContextOptions options) => true;
    }

    private sealed class FakeSqlServerDatabaseProvider : IDatabaseProvider
    {
        public string Name => "Microsoft.EntityFrameworkCore.SqlServer";

        public bool IsConfigured(IDbContextOptions options) => true;
    }

    private sealed class FakeOracleDatabaseProvider : IDatabaseProvider
    {
        public string Name => "Oracle.EntityFrameworkCore";

        public bool IsConfigured(IDbContextOptions options) => true;
    }
}
