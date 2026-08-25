using System.Data.Common;
using Elsa.Diagnostics.StructuredLogs.Persistence.PostgreSql.Options;
using Elsa.Diagnostics.StructuredLogs.Persistence.Relational.Contracts;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Elsa.Diagnostics.StructuredLogs.Persistence.PostgreSql.Services;

public class PostgreSqlStructuredLogConnectionFactory(IOptions<PostgreSqlStructuredLogOptions> options) : IRelationalStructuredLogConnectionFactory
{
    public async ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connectionString = options.Value.ConnectionString;

        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                "No PostgreSQL connection string is configured for structured log persistence. Set it when calling UsePostgreSqlStorage.");

        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
