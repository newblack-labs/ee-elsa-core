using Elsa.Diagnostics.StructuredLogs.Persistence.PostgreSql.Extensions;
using Elsa.Diagnostics.StructuredLogs.Persistence.PostgreSql.Options;
using Elsa.Features.Abstractions;
using Elsa.Features.Services;

namespace Elsa.Diagnostics.StructuredLogs.Persistence.PostgreSql.Features;

public class PostgreSqlStructuredLogPersistenceFeature(IModule module) : FeatureBase(module)
{
    public Action<PostgreSqlStructuredLogOptions>? ConfigureOptions { get; set; }

    public override void Apply()
    {
        Services.AddPostgreSqlStructuredLogPersistence(ConfigureOptions);
    }
}
