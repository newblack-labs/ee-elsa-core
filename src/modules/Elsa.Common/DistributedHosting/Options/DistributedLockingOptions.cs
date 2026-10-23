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
    /// Takes the lock of a newly created workflow instance at the run's first commit instead of before the run starts. Until
    /// that commit the instance is not stored, so nothing else can reach it. From the first commit on the lock is held until
    /// the run ends, exactly as before. The gain is for runs that commit only at their end (no commit strategy that commits
    /// mid-run): they hold the lock — and, with a database lock provider, the connection it keeps open — only for that last
    /// commit instead of for the whole run, including its waits on other systems. A run that commits after every activity
    /// takes the lock at its first commit, so it gains little. Off by default.
    /// </summary>
    /// <remarks>
    /// Taking the lock now happens inside the run: the commit that takes it waits up to <see cref="LockAcquisitionTimeout"/>
    /// per attempt, retried on transient errors. If it cannot be taken the run fails with a <c>DeferredInstanceLockException</c>
    /// and nothing of it is stored; the activities that ran before that commit are not undone, and a retry runs them again.
    /// With the eager lock the run would not have started.
    /// </remarks>
    public bool DeferNewInstanceLock { get; set; }
}
