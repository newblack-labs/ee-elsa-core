using Elsa.Diagnostics.StructuredLogs.Persistence.Relational.Contracts;

namespace Elsa.Diagnostics.StructuredLogs.Persistence.PostgreSql.Services;

public class PostgreSqlStructuredLogDialect : IRelationalStructuredLogDialect
{
    public string ProviderName => "PostgreSQL";

    /// <summary>Npgsql binds named parameters with the same '@' prefix Microsoft.Data.Sqlite uses.</summary>
    public string ParameterPrefix => "@";

    /// <summary>
    /// Double-quoted, with embedded quotes doubled — the SQL standard form, same as SQLite.
    /// </summary>
    /// <remarks>
    /// Quoting is not cosmetic here. PostgreSQL folds unquoted identifiers to lower case, while
    /// FluentMigrator's PostgreSQL generator emits them quoted — so the table it creates is
    /// "StructuredLogEvents" with its capitals intact. Reading it back unquoted would look for
    /// structuredlogevents and fail. The shared SQL builder always routes identifiers through here, so
    /// both sides agree.
    /// </remarks>
    public string QuoteIdentifier(string identifier)
    {
        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    public string ApplyLimit(string sql, int limit)
    {
        return $"{sql} LIMIT {limit}";
    }

    /// <summary>
    /// A bare OFFSET, which PostgreSQL allows without a LIMIT.
    /// </summary>
    /// <remarks>
    /// This is the one place the two providers genuinely diverge, and the reason the dialect abstraction
    /// exists: SQLite cannot parse OFFSET without a preceding LIMIT, so it emits the sentinel
    /// "LIMIT -1 OFFSET n". PostgreSQL rejects a negative LIMIT outright, so copying that would fail at
    /// query time rather than at startup.
    /// </remarks>
    public string ApplyOffset(string sql, int offset)
    {
        return $"{sql} OFFSET {offset}";
    }
}
