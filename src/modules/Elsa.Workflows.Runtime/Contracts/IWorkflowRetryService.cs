namespace Elsa.Workflows.Runtime;

public interface IWorkflowRetryService
{
    /// <summary>
    /// Retries a workflow instance.
    /// </summary>
    /// <remarks>Also cancels all children</remarks>
    Task<bool> RetryWorkflowAsync(string workflowInstanceId, CancellationToken cancellationToken = default);
}