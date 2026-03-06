using Elsa.Abstractions;
using Elsa.Alterations.AlterationTypes;
using Elsa.Alterations.Core.Contracts;
using Elsa.Alterations.Core.Results;
using Elsa.Common.Models;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Entities;
using Elsa.Workflows.Management.Filters;
using Elsa.Workflows;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Contracts;
using Elsa.Workflows.Runtime.Requests;
using JetBrains.Annotations;

namespace Elsa.Alterations.Endpoints.Workflows.Retry;

/// <summary>
/// Retries the specified workflow instances.
/// </summary>
[PublicAPI]
public class Retry : ElsaEndpoint<Request, Response>
{
    private readonly IAlterationRunner _alterationRunner;
    private readonly IWorkflowDispatcher _workflowDispatcher;
    private readonly IWorkflowInstanceStore _workflowInstanceStore;
    private readonly IWorkflowDefinitionStore _workflowDefinitionStore;

    /// <inheritdoc />
    public Retry(IAlterationRunner alterationRunner, IWorkflowDispatcher workflowDispatcher, IWorkflowInstanceStore workflowInstanceStore, IWorkflowDefinitionStore workflowDefinitionStore)
    {
        _alterationRunner = alterationRunner;
        _workflowDispatcher = workflowDispatcher;
        _workflowInstanceStore = workflowInstanceStore;
        _workflowDefinitionStore = workflowDefinitionStore;
    }

    /// <inheritdoc />
    public override void Configure()
    {
        Routes("/alterations/workflows/retry");
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.POST);
        ConfigurePermissions("run:alterations");
    }

    /// <inheritdoc />
    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        var allResults = new List<RunAlterationsResult>();

        // Load each workflow instance.
        var workflowInstances = (await _workflowInstanceStore.FindManyAsync(new WorkflowInstanceFilter { Ids = request.WorkflowInstanceIds }, cancellationToken)).ToList();

        foreach (var workflowInstance in workflowInstances)
        {
            // Build the alteration plan: migrate to latest published version, then re-schedule faulted activities.
            var alterations = new List<IAlteration>();

            // Migrate to latest published version if a newer one exists.
            var latestPublished = await _workflowDefinitionStore.FindAsync(new WorkflowDefinitionFilter
            {
                DefinitionId = workflowInstance.DefinitionId,
                VersionOptions = VersionOptions.Published
            }, cancellationToken);

            if (latestPublished != null && latestPublished.Version > workflowInstance.Version)
                alterations.Add(new Migrate { TargetVersion = latestPublished.Version });

            // Schedule faulted activities.
            var activityIds = GetActivityIds(request, workflowInstance).ToList();
            alterations.AddRange(activityIds.Select(activityId => new ScheduleActivity { ActivityId = activityId }));

            // Run the plan.
            var results = await _alterationRunner.RunAsync([workflowInstance.Id], alterations, cancellationToken);
            allResults.AddRange(results);

            // Transition the workflow from Faulted back to a running state so the dispatcher can pick it up.
            if (workflowInstance.SubStatus == WorkflowSubStatus.Faulted)
            {
                var updatedInstance = await _workflowInstanceStore.FindAsync(new WorkflowInstanceFilter { Id = workflowInstance.Id }, cancellationToken);
                if (updatedInstance != null)
                {
                    updatedInstance.WorkflowState.SubStatus = WorkflowSubStatus.Executing;
                    updatedInstance.WorkflowState.Incidents.Clear();
                    await _workflowInstanceStore.SaveAsync(updatedInstance, cancellationToken);
                }
            }

            // Schedule updated workflow.
            await _workflowDispatcher.DispatchAsync(new DispatchWorkflowInstanceRequest(workflowInstance.Id), cancellationToken: cancellationToken);
        }

        // Write response.
        var response = new Response(allResults);
        await Send.OkAsync(response, cancellationToken);
    }

    private IEnumerable<string> GetActivityIds(Request request, WorkflowInstance workflowInstance)
    {
        // If activity IDs are explicitly specified, use them.
        if (request.ActivityIds?.Any() == true)
            return request.ActivityIds;

        // Otherwise, select IDs of all faulted activities.
        return workflowInstance.WorkflowState.Incidents.Select(x => x.ActivityId).Distinct().ToList();
    }
}