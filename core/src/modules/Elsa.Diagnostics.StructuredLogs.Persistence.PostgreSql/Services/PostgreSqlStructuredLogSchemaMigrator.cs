using Elsa.Diagnostics.StructuredLogs.Persistence.PostgreSql.Options;
using Elsa.Diagnostics.StructuredLogs.Persistence.Relational.Contracts;
using Elsa.Diagnostics.StructuredLogs.Persistence.Relational.Migrations;
using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Elsa.Diagnostics.StructuredLogs.Persistence.PostgreSql.Services;

public class PostgreSqlStructuredLogSchemaMigrator(IOptions<PostgreSqlStructuredLogOptions> options) : IStructuredLogSchemaMigrator
{
    public ValueTask MigrateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var connectionString = options.Value.ConnectionString;

        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "No PostgreSQL connection string is configured for structured log persistence. Set it when calling UsePostgreSqlStorage.");

        // The migration itself is shared with the SQLite provider and needs no PostgreSQL variant: it is
        // written against FluentMigrator's fluent API rather than raw SQL, so the column types are
        // translated per provider (AsString(int.MaxValue) becomes text here, AsInt64 becomes bigint).
        using var services = new ServiceCollection()
            .AddLogging()
            .AddFluentMigratorCore()
            .ConfigureRunner(builder => builder
                .AddPostgres()
                .WithGlobalConnectionString(connectionString)
                .ScanIn(typeof(M001CreateStructuredLogTables).Assembly).For.Migrations())
            .BuildServiceProvider(false);

        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
        return ValueTask.CompletedTask;
    }
}
