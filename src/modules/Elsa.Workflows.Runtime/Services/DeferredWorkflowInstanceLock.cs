using Medallion.Threading;

namespace Elsa.Workflows.Runtime;

/// <summary>
/// The lock of a workflow instance that is being created and run, taken when the run first stores the instance instead of
/// before the run starts.
/// </summary>
/// <remarks>
/// <para>
/// The distributed workflow client locks an instance so that no two runs of it overlap. Before a new instance's first commit
/// there is no stored instance, so nothing can reach it: not a resume (which needs a stored bookmark), not the restart of
/// interrupted workflows, a delete or the dispatch outbox (which all need the stored row). <see cref="DefaultCommitStateHandler"/>
/// calls <see cref="EnsureAcquiredAsync"/> before every commit, so the lock is taken before the first one writes anything,
/// and from then on it is held until the scope is disposed at the end of the run, as an eager lock would have been. A run
/// that commits only at its end (no commit strategy committing mid-run) holds the lock only for that last commit, not while
/// it runs.
/// </para>
/// <para>
/// If the lock cannot be taken, the acquisition fails for good: every later commit of the run fails the same way, so the run
/// fails rather than storing anything unlocked. The activities it has run by then are not undone.
/// </para>
/// <para>
/// The scope travels with the run's async flow, and so into any task started during the run; acquiring is done once, by
/// whichever caller comes first. It only answers for its own instance, so a child workflow run in-process on the same flow,
/// which has an id of its own, is not mistaken for it.
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
    /// the flow it was begun on and the flows it starts, and no longer once that method returns.
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
    /// failed, every call answers with that outcome without trying again.
    /// </summary>
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

/// <summary>
/// A new workflow instance's lock could not be taken before its first commit, so the run fails without storing anything.
/// The activities that ran before that commit are not undone; a retry runs them again.
/// </summary>
public sealed class DeferredInstanceLockException(string workflowInstanceId, Exception innerException)
    : Exception($"Could not take the lock of new workflow instance '{workflowInstanceId}' before its first commit; the run fails and nothing of it is stored.", innerException)
{
    /// <summary>The instance whose lock could not be taken.</summary>
    public string WorkflowInstanceId { get; } = workflowInstanceId;
}
