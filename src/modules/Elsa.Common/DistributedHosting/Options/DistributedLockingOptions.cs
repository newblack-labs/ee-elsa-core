using JetBrains.Annotations;

namespace Elsa.Common.DistributedHosting;

/// <summary>
/// Provides options related to distributed locking, which is used by the workflow runtime.
/// </summary>
[UsedImplicitly]
public class DistributedLockingOptions
{
    /// <summary>
    /// The maximum amount of time to wait before giving up trying to acquire a lock. Defaults to 10 minutes.
    /// </summary>
    public TimeSpan LockAcquisitionTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Acknowledges that the distributed workflow runtime is using a lock provider that only coordinates within this node or local file system.
    /// Enable this for single-host development or tests to suppress the startup warning. Clustered deployments should configure a cross-node provider such as Redis, SQL Server, or PostgreSQL.
    /// </summary>
    public bool AllowLocalLockProviderInDistributedRuntime { get; set; }

    /// <summary>
    /// Takes the lock of a newly created workflow instance at the run's first commit instead of before the run starts, when
    /// the runtime generated the instance's id (a caller-supplied id may name a stored instance, so it is locked at once).
    /// Before that commit the instance is not stored; from it on the lock is held until the run ends, exactly as before. The
    /// gain is for runs that commit only at their end (no commit strategy that commits mid-run): they hold the lock — and,
    /// with a database lock provider, the connection it keeps open — from that last commit instead of for the whole run,
    /// including its waits on other systems. A run that commits after every activity takes the lock at its first commit, so
    /// it gains little. Works with <c>DefaultCommitStateHandler</c>; a custom commit handler must take the lock itself. Off
    /// by default.
    /// </summary>
    /// <remarks>
    /// Taking the lock now happens inside the run: the commit that takes it waits up to <see cref="LockAcquisitionTimeout"/>
    /// per attempt, for up to four attempts on transient errors. If it cannot be taken the run fails with a
    /// <c>DeferredInstanceLockException</c> and its state is not stored; what its activities and completion notifications
    /// started is not undone (side effects, a dispatched child, a parent's resume), and a failure at a commit made inside an
    /// activity is first handled as that activity's fault, so branches already scheduled may still run. A retry runs the
    /// activities again. With the eager lock the run would not have started. Cancellation is not wrapped.
    /// </remarks>
    public bool DeferNewInstanceLock { get; set; }
}
