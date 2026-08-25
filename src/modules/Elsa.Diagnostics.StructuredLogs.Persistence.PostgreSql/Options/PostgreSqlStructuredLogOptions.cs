using Elsa.Diagnostics.StructuredLogs.Persistence.Relational.Options;

namespace Elsa.Diagnostics.StructuredLogs.Persistence.PostgreSql.Options;

public class PostgreSqlStructuredLogOptions
{
    /// <summary>
    /// Npgsql connection string for the database holding structured log records.
    /// </summary>
    /// <remarks>
    /// No default, unlike the SQLite provider: a wrong file path creates a stray database, whereas a
    /// wrong or absent connection string here should fail loudly rather than silently target something.
    /// </remarks>
    public string ConnectionString { get; set; } = "";

    /// <summary>
    /// Supplies the connection string at the moment it is needed, taking precedence over
    /// <see cref="ConnectionString"/>.
    /// </summary>
    /// <remarks>
    /// For deployments where the credential is not a static part of the connection string — Entra ID
    /// managed identity for Azure Database for PostgreSQL being the case this exists for, where the
    /// password is a bearer token that expires roughly hourly. Both the connection factory and the
    /// schema migrator call this per use, so a token is never cached beyond the operation that fetched
    /// it. Left null, the provider behaves exactly as before and uses <see cref="ConnectionString"/>.
    ///
    /// A delegate rather than an <c>NpgsqlDataSource</c> because the schema migrator runs through
    /// FluentMigrator, whose builder accepts only a connection string — there is no overload that takes
    /// a live connection, so a data source alone could not migrate.
    /// </remarks>
    public Func<CancellationToken, ValueTask<string>>? ConnectionStringProvider { get; set; }

    public bool RunMigrationsOnStartup { get; set; } = true;

    public RelationalStructuredLogOptions Relational { get; set; } = new();

    /// <summary>Resolves the connection string to use, preferring <see cref="ConnectionStringProvider"/>.</summary>
    public async ValueTask<string> ResolveConnectionStringAsync(CancellationToken cancellationToken = default)
    {
        var connectionString = ConnectionStringProvider is null
            ? ConnectionString
            : await ConnectionStringProvider(cancellationToken);

        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "No PostgreSQL connection string is configured for structured log persistence. Set it when calling UsePostgreSqlStorage.");

        return connectionString;
    }
}
