using Elsa.Mediator.Contracts;
using Elsa.Workflows.Management;
using Elsa.Workflows.Runtime.Models;
using Elsa.Workflows.Runtime.Notifications;
using Microsoft.Extensions.Logging;

namespace Elsa.Workflows.Runtime;

/// <inheritdoc />
public class DefaultRegistriesPopulator(
    IWorkflowDefinitionStorePopulator workflowDefinitionStorePopulator,
    IActivityRegistryPopulator activityRegistryPopulator,
    INotificationSender notificationSender,
    ILogger<DefaultRegistriesPopulator> logger) : IRegistriesPopulator
{
    /// <inheritdoc />
    public async Task PopulateAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Registry population starting (5-stage sequence)");

        // Stage 1: Populate the activity registry.
        // Because workflow definitions can be used as activities, we need to make sure that the activity registry is populated before we populate the workflow definition store.
        logger.LogInformation("Stage 1: Populating activity registry (initial)");
        await activityRegistryPopulator.PopulateRegistryAsync(cancellationToken);

        // Stage 2: Populate the workflow definition store.
        logger.LogInformation("Stage 2: Populating workflow definition store");
        await workflowDefinitionStorePopulator.PopulateStoreAsync(false, cancellationToken);

        // Stage 3: Re-populate the activity registry.
        // After the workflow definition store has been populated, we need to re-populate the activity registry to make sure that the activity descriptors are up-to-date.
        logger.LogInformation("Stage 3: Re-populating activity registry (with workflow-as-activities)");
        await activityRegistryPopulator.PopulateRegistryAsync(cancellationToken);

        // Stage 4. Re-update the workflow definition store with the current set of activities.
        // Finally, we need to re-populate the workflow definition store to make sure that the workflow definitions are up-to-date.
        logger.LogInformation("Stage 4: Re-updating workflow definition store");
        var workflowDefinitions = await workflowDefinitionStorePopulator.PopulateStoreAsync(true, cancellationToken);

        // Stage 5: Publish a notification that the workflow definitions have been reloaded. This ensures other replicated nodes can update their activity registry.
        logger.LogInformation("Stage 5: Publishing workflow definitions reloaded notification");
        var reloadedWorkflowDefinitions = workflowDefinitions.Select(ReloadedWorkflowDefinition.FromDefinition).ToList();
        var notification = new WorkflowDefinitionsReloaded(reloadedWorkflowDefinitions);
        await notificationSender.SendAsync(notification, cancellationToken);

        logger.LogInformation("Registry population complete");
    }
}
