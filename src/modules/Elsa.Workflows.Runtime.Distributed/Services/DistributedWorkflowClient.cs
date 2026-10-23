using Elsa.Common.DistributedHosting;
using Elsa.Resilience;
using Elsa.Workflows.Runtime.Messages;
using Elsa.Workflows.State;
using Medallion.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace Elsa.Workflows.Runtime.Distributed;

public class DistributedWorkflowClient(
    string workflowInstanceId,
    IDistributedLockProvider distributedLockProvider,
    ITransientExceptionDetector transientExceptionDetector,
    IOptions<DistributedLockingOptions> distributedLockingOptions,
    IServiceProvider serviceProvider,
    ILogger<DistributedWorkflowClient> logger,
    bool instanceIdGenerated = false)
    : IWorkflowClient
{
    private readonly LocalWorkflowClient _localWorkflowClient = ActivatorUtilities.CreateInstance<LocalWorkflowClient>(serviceProvider, workflowInstanceId);
    private readonly Lazy<ResiliencePipeline> _retryPipeline = new(() => CreateRetryPipeline(transientExceptionDetector, logger, workflowInstanceId));
    public string WorkflowInstanceId => workflowInstanceId;

    public async Task<CreateWorkflowInstanceResponse> CreateInstanceAsync(CreateWorkflowInstanceRequest request, CancellationToken cancellationToken = default)
    {
        return await _localWorkflowClient.CreateInstanceAsync(request, cancellationToken);
    }

    public async Task<RunWorkflowInstanceResponse> RunInstanceAsync(RunWorkflowInstanceRequest request, CancellationToken cancellationToken = default)
    {
        var result = await WithLockAsync(async () => await _localWorkflowClient.RunInstanceAsync(request, cancellationToken), cancellationToken);
        return result;
    }

    public async Task<RunWorkflowInstanceResponse> CreateAndRunInstanceAsync(CreateAndRunWorkflowInstanceRequest request, CancellationToken cancellationToken = default)
    {
        // We need to lock newly created workflow instances too, because it might dispatch child workflows that attempt to resume the parent workflow.
        // For example, when using a DispatchWorkflow activity configured to wait for the dispatched workflow to complete.
        // Deferred only for an id the runtime generated: a caller-supplied id (a dispatch naming its instance, the transactional
        // dispatcher's pre-generated one) may name an instance that is stored already, which other runs can reach at once.
        if (!distributedLockingOptions.Value.DeferNewInstanceLock || !instanceIdGenerated)
            return await WithLockAsync(async () => await _localWorkflowClient.CreateAndRunInstanceAsync(request, cancellationToken), cancellationToken);

        // A generated id is stored first by this run's first commit, so the commit handler takes the lock there, and it is held
        // from then until the run ends. A run that commits only at its end holds it from that commit on.
        await using var instanceLock = DeferredWorkflowInstanceLock.Begin(WorkflowInstanceId, AcquireDeferredLockAsync, ReleaseLockAsync);
        return await _localWorkflowClient.CreateAndRunInstanceAsync(request, cancellationToken);
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        await _localWorkflowClient.CancelAsync(cancellationToken);
    }

    public async Task<WorkflowState> ExportStateAsync(CancellationToken cancellationToken = default)
    {
        return await _localWorkflowClient.ExportStateAsync(cancellationToken);
    }

    public async Task ImportStateAsync(WorkflowState workflowState, CancellationToken cancellationToken = default)
    {
        await _localWorkflowClient.ImportStateAsync(workflowState, cancellationToken);
    }

    public async Task<bool> InstanceExistsAsync(CancellationToken cancellationToken = default)
    {
        return await _localWorkflowClient.InstanceExistsAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(CancellationToken cancellationToken = default)
    {
        // Use the same distributed lock as for execution to prevent concurrent DB writes
        return await WithLockAsync(async () => await _localWorkflowClient.DeleteAsync(cancellationToken), cancellationToken);
    }

    private string LockKey => $"workflow-instance:{WorkflowInstanceId}";

    private async Task<TReturn> WithLockAsync<TReturn>(Func<Task<TReturn>> func, CancellationToken cancellationToken = default)
    {
        var lockHandle = await AcquireLockWithRetryAsync(LockKey, cancellationToken);

        try
        {
            return await func();
        }
        finally
        {
            await ReleaseLockAsync(lockHandle);
        }
    }

    /// <summary>
    /// Takes a new instance's deferred lock at its first commit. A failure fails the run: logged here with the instance id,
    /// then thrown (and rethrown by every later commit of the run, which Elsa's middleware logs again as a warning). As a
    /// backstop, an instance already stored under this id when the lock is taken fails the run too: its first commit would
    /// overwrite a row another run reached unlocked.
    /// </summary>
    private async Task<IDistributedSynchronizationHandle?> AcquireDeferredLockAsync(CancellationToken cancellationToken)
    {
        IDistributedSynchronizationHandle? handle;
        try
        {
            handle = await AcquireLockWithRetryAsync(LockKey, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Could not take the lock of new workflow instance {WorkflowInstanceId} before its first commit; the run fails and its state is not stored. What its activities and completion notifications started is not undone.", WorkflowInstanceId);
            throw new DeferredInstanceLockException(WorkflowInstanceId, "its lock could not be taken", ex);
        }

        if (await _localWorkflowClient.InstanceExistsAsync(cancellationToken))
        {
            await ReleaseLockAsync(handle);
            logger.LogError("New workflow instance {WorkflowInstanceId} is already stored when its first commit takes the lock; the run fails rather than overwrite it.", WorkflowInstanceId);
            throw new DeferredInstanceLockException(WorkflowInstanceId, "an instance with its id is already stored", null);
        }

        return handle;
    }

    private async Task<IDistributedSynchronizationHandle?> AcquireLockWithRetryAsync(string lockKey, CancellationToken cancellationToken = default)
    {
        var lockTimeout = distributedLockingOptions.Value.LockAcquisitionTimeout;

        return await _retryPipeline.Value.ExecuteAsync(async ct =>
            await distributedLockProvider.AcquireLockAsync(lockKey, lockTimeout, ct),
            cancellationToken);
    }

    private async Task ReleaseLockAsync(IDistributedSynchronizationHandle? lockHandle)
    {
        if (lockHandle == null)
            return;

        try
        {
            await lockHandle.DisposeAsync();
        }
        catch (Exception ex)
        {
            // Log but don't throw - the work is already done, and the lock
            // will be automatically released when the connection dies
            logger.LogWarning(ex, "Failed to release distributed lock for workflow instance {WorkflowInstanceId}. The lock will be automatically released by the database.", WorkflowInstanceId);
        }
    }

    private static ResiliencePipeline CreateRetryPipeline(
        ITransientExceptionDetector transientExceptionDetector,
        ILogger<DistributedWorkflowClient> logger,
        string workflowInstanceId)
    {
        const int maxRetryAttempts = 3;

        return new ResiliencePipelineBuilder()
            .AddRetry(new()
            {
                MaxRetryAttempts = maxRetryAttempts,
                Delay = TimeSpan.FromMilliseconds(500),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = new PredicateBuilder().Handle<Exception>(transientExceptionDetector.IsTransient),
                OnRetry = args =>
                {
                    logger.LogWarning(args.Outcome.Exception, "Transient error acquiring lock for workflow instance {WorkflowInstanceId}. Attempt {AttemptNumber} of {MaxAttempts}.", workflowInstanceId, args.AttemptNumber + 1, maxRetryAttempts);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }
}
