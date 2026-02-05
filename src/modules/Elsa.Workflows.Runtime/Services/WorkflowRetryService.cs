using Elsa.Workflows.Management;
using Elsa.Workflows.Runtime.Messages;
using Microsoft.Extensions.DependencyInjection;

namespace Elsa.Workflows.Runtime;

public class WorkflowRetryService(IWorkflowInstanceManager instanceManager, IServiceProvider serviceProvider) : IWorkflowRetryService
{
    public async Task<bool> RetryWorkflowAsync(string workflowInstanceId, CancellationToken cancellationToken = default)
    {
        var workflow = await instanceManager.FindByIdAsync(workflowInstanceId, cancellationToken);
        workflow.Status = WorkflowStatus.Running;
        workflow.SubStatus = WorkflowSubStatus.Pending;
        await instanceManager.UpdateAsync(workflow, cancellationToken);
        
        var client = ActivatorUtilities.CreateInstance<LocalWorkflowClient>(serviceProvider, workflowInstanceId);
        await client.RunInstanceAsync(workflow, new RunWorkflowInstanceRequest
        {
            Variables = new Dictionary<string, object>()
        }, cancellationToken);
        
        return true;
    }
}