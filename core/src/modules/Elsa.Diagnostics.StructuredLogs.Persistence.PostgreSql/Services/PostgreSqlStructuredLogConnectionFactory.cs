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
        var connectionString = await options.Value.ResolveConnectionStringAsync(cancellationToken);
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
