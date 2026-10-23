using Medallion.Threading;

namespace Elsa.Workflows.Runtime;

/// <summary>
/// The lock of a workflow instance that is being created and run, taken when the run first stores the instance instead of
/// before the run starts.
/// </summary>
/// <remarks>
/// <para>
/// The distributed workflow client locks an instance so that no two runs of it overlap. It defers the lock only for an id the
/// runtime generated, which no stored instance has: before such an instance's first commit there is no stored bookmark to
/// resume and no row for the restart of interrupted workflows, a delete or the dispatch outbox to reach.
/// <see cref="DefaultCommitStateHandler"/> calls <see cref="EnsureAcquiredAsync"/> before every commit, so the lock is taken
/// before the first one writes anything, and is held from then until the scope is disposed at the end of the run — the rest
/// of the run, the commit's own handlers and deferred tasks included. A run that commits only at its end takes it there. A
/// custom <see cref="ICommitStateHandler"/> must call <see cref="EnsureAcquiredAsync"/> itself, or the instance runs unlocked.
/// </para>
/// <para>
/// If the lock cannot be taken, the acquisition fails for good: every later commit of the run fails the same way while the
/// scope lasts, so the run fails rather than storing its state unlocked. What it started by then is not undone (see
/// <see cref="DeferredInstanceLockException"/>).
/// </para>
/// <para>
/// The scope travels with the run's async flow, and so into any task started during the run; acquiring is done once, by
/// whichever caller comes first. A task that outlives the run and commits after the scope was disposed is not locked by it.
/// The scope only answers for its own instance, so a child workflow run in-process on the same flow, which has an id of its
/// own, is not mistaken for it.
/// </para>
/// </remarks>
internal sealed class DeferredWorkflowInstanceLock : IAsyncDisposable
{
    private static readonly AsyncLocal<DeferredWorkflowInstanceLock?> CurrentScope = new();

    private readonly Func<CancellationToken, Task<IDistributedSynchronizationHandle?>> _acquire;
    private readonly Func<IDistributedSynchronizationHandle?, Task> _release;
    private readonly object _gate = new();
    private Task<IDistributedSynchronizationHandle?>? _acquisition;
    private bool _disposed;

    private DeferredWorkflowInstanceLock(
        string workflowInstanceId,
        Func<CancellationToken, Task<IDistributedSynchronizationHandle?>> acquire,
        Func<IDistributedSynchronizationHandle?, Task> release)
    {
        WorkflowInstanceId = workflowInstanceId;
        _acquire = acquire;
        _release = release;
    }

    /// <summary>The instance this scope locks.</summary>
    internal string WorkflowInstanceId { get; }

    /// <summary>Whether the lock has been taken.</summary>
    internal bool IsAcquired => _acquisition is { IsCompletedSuccessfully: true };

    /// <summary>
    /// Opens a scope for the instance about to run on the current async flow. Nothing is locked yet. The scope is visible to
    /// the async method that called this and the flows that method starts, and no longer once that method returns.
    /// </summary>
    /// <param name="workflowInstanceId">The instance.</param>
    /// <param name="acquire">Takes the instance's lock; called once, by the first commit.</param>
    /// <param name="release">Releases what <paramref name="acquire"/> returned, when the scope is disposed.</param>
    internal static DeferredWorkflowInstanceLock Begin(
        string workflowInstanceId,
        Func<CancellationToken, Task<IDistributedSynchronizationHandle?>> acquire,
        Func<IDistributedSynchronizationHandle?, Task> release)
    {
        var scope = new DeferredWorkflowInstanceLock(workflowInstanceId, acquire, release);
        CurrentScope.Value = scope;
        return scope;
    }

    /// <summary>
    /// Takes the lock of <paramref name="workflowInstanceId"/> if a deferred scope on the current flow owns that instance;
    /// otherwise does nothing (an eagerly locked run, or no distributed runtime at all). Once taken, or once taking it has
    /// failed, later calls answer with that outcome without trying again, until the scope is disposed; after that they do
    /// nothing.
    /// </summary>
    /// <param name="workflowInstanceId">The instance about to be committed.</param>
    /// <param name="cancellationToken">Cancels the acquisition; a cancelled one stays cancelled for the rest of the scope.</param>
    public static Task EnsureAcquiredAsync(string workflowInstanceId, CancellationToken cancellationToken = default)
    {
        var scope = CurrentScope.Value;
        if (scope == null || scope.WorkflowInstanceId != workflowInstanceId)
            return Task.CompletedTask;

        return scope.AcquireAsync(cancellationToken);
    }

    private Task AcquireAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_disposed)
                return Task.CompletedTask;

            return _acquisition ??= _acquire(cancellationToken);
        }
    }

    /// <summary>Releases the lock if it was taken.</summary>
    public async ValueTask DisposeAsync()
    {
        Task<IDistributedSynchronizationHandle?>? acquisition;
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            acquisition = _acquisition;
        }

        if (acquisition is null)
            return;

        IDistributedSynchronizationHandle? handle;
        try
        {
            handle = await acquisition;
        }
        catch
        {
            return; // Never taken: nothing to release. The run already failed with the reason.
        }

        await _release(handle);
    }
}
