using Medallion.Threading;

namespace Elsa.Workflows.Runtime;

/// <summary>
/// The lock of a workflow instance that is being created and run, taken only once something else could reach the instance.
/// </summary>
/// <remarks>
/// <para>
/// The distributed workflow client locks an instance so that two nodes never run it at the same time. A new instance cannot
/// be run by anyone else until it persists a bookmark, since resuming needs one — so <see cref="DefaultCommitStateHandler"/>
/// calls <see cref="EnsureAcquiredAsync"/> before a commit that persists bookmarks, and the lock is taken there, before the
/// bookmark becomes visible. From then on it is held until the scope is disposed at the end of the run, as an eager lock
/// would have been.
/// </para>
/// <para>
/// The scope travels with the run's async flow. It only answers for its own instance, so a child workflow run in-process on
/// the same flow, which has an id of its own, is not mistaken for it.
/// </para>
/// </remarks>
public sealed class DeferredWorkflowInstanceLock : IAsyncDisposable
{
    private static readonly AsyncLocal<DeferredWorkflowInstanceLock?> CurrentScope = new();

    private readonly Func<CancellationToken, Task<IDistributedSynchronizationHandle?>> _acquire;
    private readonly Func<IDistributedSynchronizationHandle?, Task> _release;
    private readonly DeferredWorkflowInstanceLock? _previous;
    private IDistributedSynchronizationHandle? _handle;
    private bool _disposed;

    private DeferredWorkflowInstanceLock(
        string workflowInstanceId,
        Func<CancellationToken, Task<IDistributedSynchronizationHandle?>> acquire,
        Func<IDistributedSynchronizationHandle?, Task> release,
        DeferredWorkflowInstanceLock? previous)
    {
        WorkflowInstanceId = workflowInstanceId;
        _acquire = acquire;
        _release = release;
        _previous = previous;
    }

    /// <summary>
    /// The instance this scope locks.
    /// </summary>
    public string WorkflowInstanceId { get; }

    /// <summary>
    /// Whether the lock has been taken.
    /// </summary>
    public bool IsAcquired { get; private set; }

    /// <summary>
    /// Opens a scope for the instance about to run on the current async flow. Nothing is locked yet.
    /// </summary>
    /// <param name="workflowInstanceId">The instance.</param>
    /// <param name="acquire">Takes the instance's lock; called at most once.</param>
    /// <param name="release">Releases what <paramref name="acquire"/> returned, when the scope is disposed.</param>
    public static DeferredWorkflowInstanceLock Begin(
        string workflowInstanceId,
        Func<CancellationToken, Task<IDistributedSynchronizationHandle?>> acquire,
        Func<IDistributedSynchronizationHandle?, Task> release)
    {
        var scope = new DeferredWorkflowInstanceLock(workflowInstanceId, acquire, release, CurrentScope.Value);
        CurrentScope.Value = scope;
        return scope;
    }

    /// <summary>
    /// Takes the lock of <paramref name="workflowInstanceId"/> if a deferred scope on the current flow owns that instance
    /// and has not taken it yet; otherwise does nothing (an eagerly locked run, or no distributed runtime at all).
    /// </summary>
    public static Task EnsureAcquiredAsync(string workflowInstanceId, CancellationToken cancellationToken = default)
    {
        var scope = CurrentScope.Value;
        if (scope == null || scope.IsAcquired || scope._disposed || scope.WorkflowInstanceId != workflowInstanceId)
            return Task.CompletedTask;

        return scope.AcquireAsync(cancellationToken);
    }

    private async Task AcquireAsync(CancellationToken cancellationToken)
    {
        // A run commits one step at a time, so there is no concurrent caller to guard against here.
        _handle = await _acquire(cancellationToken);
        IsAcquired = true;
    }

    /// <summary>
    /// Restores the enclosing scope and releases the lock if it was taken.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        // Not an async method: the async-local is restored on the caller's flow, not on a copy of it.
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;
        if (CurrentScope.Value == this)
            CurrentScope.Value = _previous;

        return IsAcquired ? new(_release(_handle)) : ValueTask.CompletedTask;
    }
}
