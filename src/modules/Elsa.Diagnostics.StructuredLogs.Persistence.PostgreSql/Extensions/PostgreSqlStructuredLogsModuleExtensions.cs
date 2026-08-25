using Elsa.Common;
using Elsa.Diagnostics.StructuredLogs.Features;
using Elsa.Diagnostics.StructuredLogs.Persistence.PostgreSql.Features;
using Elsa.Diagnostics.StructuredLogs.Persistence.PostgreSql.Options;
using Elsa.Diagnostics.StructuredLogs.Persistence.PostgreSql.Services;
using Elsa.Diagnostics.StructuredLogs.Persistence.Relational.Contracts;
using Elsa.Diagnostics.StructuredLogs.Persistence.Relational.Extensions;
using Elsa.Diagnostics.StructuredLogs.Persistence.Relational.Options;
using Elsa.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Elsa.Diagnostics.StructuredLogs.Persistence.PostgreSql.Extensions;

public static class PostgreSqlStructuredLogsModuleExtensions
{
    public static StructuredLogsFeature UsePostgreSqlStorage(this StructuredLogsFeature feature, string connectionString, Action<PostgreSqlStructuredLogOptions>? configure = null)
    {
        feature.Module.Use<PostgreSqlStructuredLogPersistenceFeature>(postgres =>
        {
            postgres.ConfigureOptions = options =>
            {
                options.ConnectionString = connectionString;
                configure?.Invoke(options);
            };
        });

        return feature;
    }

    public static StructuredLogsFeature UsePostgreSqlStorage(this StructuredLogsFeature feature, Action<PostgreSqlStructuredLogOptions>? configure = null)
    {
        feature.Module.Use<PostgreSqlStructuredLogPersistenceFeature>(postgres => postgres.ConfigureOptions = configure);
        return feature;
    }

    public static IServiceCollection AddPostgreSqlStructuredLogPersistence(this IServiceCollection services, Action<PostgreSqlStructuredLogOptions>? configure = null)
    {
        if (configure != null)
            services.Configure(configure);

        services.AddOptions<PostgreSqlStructuredLogOptions>();
        services.AddOptions<RelationalStructuredLogOptions>().Configure<IOptions<PostgreSqlStructuredLogOptions>>((relational, postgres) => Copy(postgres.Value.Relational, relational));

        services.TryAddSingleton<IRelationalStructuredLogConnectionFactory, PostgreSqlStructuredLogConnectionFactory>();
        services.TryAddSingleton<IRelationalStructuredLogDialect, PostgreSqlStructuredLogDialect>();
        services.TryAddSingleton<IStructuredLogSchemaMigrator, PostgreSqlStructuredLogSchemaMigrator>();
        services.TryAddSingleton<PostgreSqlStructuredLogStartupService>();
        services.AddHostedService(sp => sp.GetRequiredService<PostgreSqlStructuredLogStartupService>());
        services.AddScoped<IStartupTask>(sp => sp.GetRequiredService<PostgreSqlStructuredLogStartupService>());
        services.AddRelationalStructuredLogPersistence();

        return services;
    }

    private static void Copy(RelationalStructuredLogOptions source, RelationalStructuredLogOptions target)
    {
        target.WriteQueue.Capacity = source.WriteQueue.Capacity;
        target.WriteQueue.BatchSize = source.WriteQueue.BatchSize;
        target.WriteQueue.FlushInterval = source.WriteQueue.FlushInterval;
        target.WriteQueue.ShutdownFlushTimeout = source.WriteQueue.ShutdownFlushTimeout;
        target.Retention.MaxAge = source.Retention.MaxAge;
        target.Retention.MaxRows = source.Retention.MaxRows;
        target.Retention.CleanupOnStartup = source.Retention.CleanupOnStartup;
    }
}
