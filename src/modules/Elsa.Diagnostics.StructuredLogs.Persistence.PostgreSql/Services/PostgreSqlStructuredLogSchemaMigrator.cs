using Elsa.Diagnostics.StructuredLogs.Persistence.PostgreSql.Options;
using Elsa.Diagnostics.StructuredLogs.Persistence.Relational.Contracts;
using Elsa.Diagnostics.StructuredLogs.Persistence.Relational.Migrations;
using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Elsa.Diagnostics.StructuredLogs.Persistence.PostgreSql.Services;

public class PostgreSqlStructuredLogSchemaMigrator(IOptions<PostgreSqlStructuredLogOptions> options) : IStructuredLogSchemaMigrator
{
    public async ValueTask MigrateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Resolved rather than read, so a deployment whose credential is a short-lived token — Entra ID
        // managed identity — migrates with a live one instead of a connection string that carries no
        // password at all.
        var connectionString = await options.Value.ResolveConnectionStringAsync(cancellationToken);

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
    }
}
