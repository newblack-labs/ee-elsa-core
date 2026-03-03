using Elsa.Common.Models;
using Elsa.Common.Multitenancy;
using Elsa.Workflows.Management.Entities;
using Elsa.Workflows.Management.Filters;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;

namespace Elsa.Workflows.Management.Activities.WorkflowDefinitionActivity;

/// <summary>
/// Provides activity descriptors based on <see cref="WorkflowDefinition"/>s stored in the database.
/// </summary>
public class WorkflowDefinitionActivityProvider(IWorkflowDefinitionStore store, WorkflowDefinitionActivityDescriptorFactory workflowDefinitionActivityDescriptorFactory, ITenantAccessor tenantAccessor, ILogger<WorkflowDefinitionActivityProvider> logger) : IActivityProvider
{
    /// <inheritdoc />
    public async ValueTask<IEnumerable<ActivityDescriptor>> GetDescriptorsAsync(CancellationToken cancellationToken = default)
    {
        var filter = new WorkflowDefinitionFilter
        {
            UsableAsActivity = true,
            VersionOptions = VersionOptions.All
        };

        var definitions = (await store.FindManyAsync(filter, cancellationToken)).ToList();

        if (definitions.Count == 0)
        {
            logger.LogInformation("WorkflowDefinitionActivityProvider: No workflow definitions with UsableAsActivity=true found in database");
        }
        else
        {
            var grouped = definitions.GroupBy(d => d.DefinitionId).ToList();
            logger.LogInformation("WorkflowDefinitionActivityProvider: Found {Count} workflow definition versions across {Definitions} definitions with UsableAsActivity=true",
                definitions.Count, grouped.Count);

            foreach (var group in grouped)
            {
                var published = group.Where(d => d.IsPublished).ToList();
                var latest = group.MaxBy(d => d.Version);
                logger.LogInformation("  - {Name} (definitionId={DefinitionId}): {VersionCount} versions, {PublishedCount} published, latest=v{LatestVersion} (isBrowsable={IsBrowsable})",
                    latest?.Name ?? "(unnamed)",
                    group.Key,
                    group.Count(),
                    published.Count,
                    latest?.Version,
                    published.Any());
            }
        }

        return CreateDescriptors(definitions).ToList();
    }

    private IEnumerable<ActivityDescriptor> CreateDescriptors(ICollection<WorkflowDefinition> definitions)
    {
        return definitions.Select(x => CreateDescriptor(x, definitions));
    }

    private ActivityDescriptor CreateDescriptor(WorkflowDefinition definition, ICollection<WorkflowDefinition> allDefinitions)
    {
        var latestPublishedVersion = allDefinitions
            .Where(x => x.DefinitionId == definition.DefinitionId && x.IsPublished)
            .MaxBy(x => x.Version);
        return workflowDefinitionActivityDescriptorFactory.CreateDescriptor(definition, latestPublishedVersion);
    }
}
