namespace Elsa.Workflows.Runtime;

/// <summary>
/// A new workflow instance whose lock was deferred (<c>DistributedLockingOptions.DeferNewInstanceLock</c>) could not be locked
/// at its first commit, so the run fails and its state is not stored. What its activities and completion notifications had
/// started by then is not undone: their side effects, a dispatched child, a parent's resume. A failure at a commit made
/// inside an activity is first handled as that activity's fault, so other branches already scheduled may still run.
/// Cancellation is not wrapped.
/// </summary>
public sealed class DeferredInstanceLockException(string workflowInstanceId, string reason, Exception? innerException)
    : Exception($"Could not take the lock of new workflow instance '{workflowInstanceId}' at its first commit ({reason}); the run fails and its state is not stored.", innerException)
{
    /// <summary>The instance whose lock could not be taken.</summary>
    public string WorkflowInstanceId { get; } = workflowInstanceId;
}
