using CShells.Features;
using Elsa.Diagnostics.StructuredLogs.Persistence.PostgreSql.Extensions;
using Elsa.Diagnostics.StructuredLogs.Persistence.Relational.ShellFeatures;
using Elsa.Platform.PackageManifest.Generator.Hints;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;

namespace Elsa.Diagnostics.StructuredLogs.Persistence.PostgreSql.ShellFeatures;

/// <summary>
/// Provides PostgreSQL persistence for diagnostics structured logs.
/// </summary>
[ManifestFeatureCategory("Diagnostics")]
[ManifestFeatureCategory("Persistence")]
[ShellFeature(
    DisplayName = "PostgreSQL Structured Log Persistence",
    Description = "Provides PostgreSQL persistence for diagnostics structured logs",
    DependsOn = [typeof(StructuredLogRelationalPersistenceFeature)])]
[UsedImplicitly]
[ManifestInfrastructure("postgresql-database", "database", Reason = "Stores structured log records in PostgreSQL.", Providers = new[] { "PostgreSQL" }, ConfigurationKeys = new[] { "ConnectionString" })]
public class PostgreSqlStructuredLogPersistenceFeature : IShellFeature
{
    [ManifestSetting(
        DisplayName = "Connection String",
        Description = "PostgreSQL connection string used to store structured log records.",
        Category = "Persistence",
        Required = true,
        Sensitive = true,
        RestartRequired = true)]
    public string ConnectionString { get; set; } = "";

    [ManifestSetting(
        DisplayName = "Run Migrations On Startup",
        Description = "Run structured log PostgreSQL schema migrations when the application starts.",
        Category = "Persistence",
        DefaultValue = "true",
        RestartRequired = true)]
    public bool RunMigrationsOnStartup { get; set; } = true;

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddPostgreSqlStructuredLogPersistence(options =>
        {
            options.ConnectionString = ConnectionString;
            options.RunMigrationsOnStartup = RunMigrationsOnStartup;
        });
    }
}
