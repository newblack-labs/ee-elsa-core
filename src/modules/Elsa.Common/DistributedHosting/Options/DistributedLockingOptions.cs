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
    /// Takes the lock of a newly created workflow instance only once the instance persists its first bookmark, instead of
    /// for its whole first run. Until then nothing else can reach the instance: resuming it needs a bookmark, so there is
    /// nothing to exclude, and a run that completes without ever waiting never holds the lock (or, with a database lock
    /// provider, the connection it keeps open). From that first bookmark on, the lock is held until the run ends, exactly
    /// as before, so a child workflow or timer that resumes the instance still waits for it. Off by default.
    /// </summary>
    public bool DeferNewInstanceLock { get; set; }
}
