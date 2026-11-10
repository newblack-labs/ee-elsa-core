namespace Elsa.Workflows;

/// <summary>
/// Provides context to the currently executing workflow.
/// </summary>
public partial class WorkflowExecutionContext
{
    private ICollection<CancellationTokenSource> _cancellationTokenSources = new List<CancellationTokenSource>();
    private ICollection<CancellationTokenRegistration> _cancellationRegistrations = new List<CancellationTokenRegistration>();

    /// <summary>
    /// Cancels the workflow and all it's children.
    /// </summary>
    public void Cancel()
    {
        foreach (var source in _cancellationTokenSources)
            source.Cancel();
        
        _cancellationTokenSources.Clear();
    }
    
    private void CancelWorkflow()
    {
        Bookmarks.Clear();
        _completionCallbackEntries.Clear();

        if (!CanTransitionTo(WorkflowSubStatus.Cancelled))
            return;

        AddExecutionLogEntry("Workflow cancelled");
        
        // The workflow's own thread can finish (e.g. fault) between the check above and this transition, without a lock.
        // TransitionTo then throws, and when the token is cancelled by a timer that throw is unhandled on a thread pool
        // thread and terminates the process. A finished workflow stays as it is, so there is nothing left to cancel.
        // A finished status is never left again, so the filter holds for the exception TransitionTo throws for that.
        try
        {
            TransitionTo(WorkflowSubStatus.Cancelled);
        }
        catch when (!CanTransitionTo(WorkflowSubStatus.Cancelled))
        {
            return;
        }

        foreach (var registration in _cancellationRegistrations)
            registration.Dispose();
    }
}