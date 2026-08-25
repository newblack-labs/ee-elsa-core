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

    public bool RunMigrationsOnStartup { get; set; } = true;

    public RelationalStructuredLogOptions Relational { get; set; } = new();
}
