using Elsa.Common.DistributedHosting;
using Elsa.Common.Models;
using Elsa.Workflows.ComponentTests.Abstractions;
using Elsa.Workflows.ComponentTests.Fixtures;
using Elsa.Workflows.ComponentTests.Scenarios.DeferredInstanceLock.Activities;
using Elsa.Workflows.ComponentTests.Scenarios.DeferredInstanceLock.Workflows;
using Elsa.Workflows.ComponentTests.Scenarios.DistributedLockResilience.Mocks;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Messages;
using Microsoft.Extensions.DependencyInjection;
using Medallion.Threading;
using Microsoft.Extensions.Options;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Filters;

namespace Elsa.Workflows.ComponentTests.Scenarios.DeferredInstanceLock;

/// <summary>
/// <see cref="DistributedLockingOptions.DeferNewInstanceLock"/>: a new instance takes its lock at its first commit, so it holds
/// none while it runs before that, and from that commit on nothing else runs it — with a bookmark or without one.
/// </summary>
public class DeferredInstanceLockTests(App app) : AppComponentTest(app)
{
    private SelectiveMockLockProvider SelectiveMockProvider => Scope.ServiceProvider.GetRequiredService<SelectiveMockLockProvider>();
    private DistributedLockingOptions LockingOptions => Scope.ServiceProvider.GetRequiredService<IOptions<DistributedLockingOptions>>().Value;
    private IWorkflowRuntime WorkflowRuntime => Scope.ServiceProvider.GetRequiredService<IWorkflowRuntime>();
    private IWorkflowInstanceStore InstanceStore => Scope.ServiceProvider.GetRequiredService<IWorkflowInstanceStore>();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunThatCommitsOnlyAtItsEnd_HoldsTheLockOnlyWhenNotDeferred(bool deferred)
    {
        await using var _ = UseDeferredLock(deferred);
        var client = await WorkflowRuntime.CreateClientAsync();
        var instanceId = client.WorkflowInstanceId;
        var probe = RunProbe.For(instanceId);

        try
        {
            var run = client.CreateAndRunInstanceAsync(new()
            {
                WorkflowDefinitionHandle = WorkflowDefinitionHandle.ByDefinitionId(BlockWithoutCommitWorkflow.DefinitionId, VersionOptions.Latest)
            });
            await probe.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // Waiting on another system: the deferred run holds no lock (and no connection for it), the eager one does.
            await using (var probeHandle = await SelectiveMockProvider.RealProvider.TryAcquireLockAsync(LockName(instanceId)))
                Assert.Equal(deferred, probeHandle != null);

            probe.Release.SetResult();
            var response = await run.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(WorkflowStatus.Finished, response.Status);
        }
        finally
        {
            probe.Release.TrySetResult();
            RunProbe.Remove(instanceId);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FirstCommitWithoutBookmark_KeepsAnotherRunOut(bool deferred)
    {
        // A stored, executing instance is what the restart of interrupted workflows, a delete or the dispatch outbox reach
        // without a bookmark: from the first commit on the instance must be locked.
        await using var _ = UseDeferredLock(deferred);
        var client = await WorkflowRuntime.CreateClientAsync();
        var instanceId = client.WorkflowInstanceId;
        var probe = RunProbe.For(instanceId);

        try
        {
            var firstRun = client.CreateAndRunInstanceAsync(new()
            {
                WorkflowDefinitionHandle = WorkflowDefinitionHandle.ByDefinitionId(CommitThenBlockWorkflow.DefinitionId, VersionOptions.Latest)
            });
            await probe.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(30));

            await using (var probeHandle = await SelectiveMockProvider.RealProvider.TryAcquireLockAsync(LockName(instanceId)))
                Assert.Null(probeHandle);

            // What the restart of an interrupted workflow does: run the stored instance again, with no bookmark.
            var secondClient = await WorkflowRuntime.CreateClientAsync(instanceId);
            var secondRun = secondClient.RunInstanceAsync(new RunWorkflowInstanceRequest());
            await Task.Delay(TimeSpan.FromSeconds(1));
            Assert.Equal(1, probe.Entered);

            probe.Release.SetResult();
            await firstRun.WaitAsync(TimeSpan.FromSeconds(30));
            await secondRun.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            probe.Release.TrySetResult();
            RunProbe.Remove(instanceId);
        }
    }

    [Fact]
    public async Task LockThatCannotBeTaken_FailsTheRunAndStoresNothing()
    {
        await using var _ = UseDeferredLock(true);
        var client = await WorkflowRuntime.CreateClientAsync();
        var instanceId = client.WorkflowInstanceId;
        var lockName = LockName(instanceId);
        var mock = SelectiveMockProvider.MockLock(lockName);
        mock.FailAcquisitionTimesForLock(lockName, 20);

        try
        {
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => client.CreateAndRunInstanceAsync(new()
            {
                WorkflowDefinitionHandle = WorkflowDefinitionHandle.ByDefinitionId(RunsStraightThroughWorkflow.DefinitionId, VersionOptions.Latest)
            }));

            Assert.Contains(Chain(failure), e => e is DeferredInstanceLockException locked && locked.WorkflowInstanceId == instanceId);
            Assert.Null(await InstanceStore.FindAsync(new WorkflowInstanceFilter { Id = instanceId }));
        }
        finally
        {
            SelectiveMockProvider.Unmock(lockName);
        }
    }

    private static IEnumerable<Exception> Chain(Exception? exception)
    {
        for (; exception != null; exception = exception.InnerException)
            yield return exception;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BookmarkCommittedWhileRunning_CannotBeResumedUntilTheRunEnds(bool deferred)
    {
        await using var _ = UseDeferredLock(deferred);
        var client = await WorkflowRuntime.CreateClientAsync();
        var instanceId = client.WorkflowInstanceId;
        var probe = BookmarkThenBlockProbe.For(instanceId);

        try
        {
            var firstRun = client.CreateAndRunInstanceAsync(new()
            {
                WorkflowDefinitionHandle = WorkflowDefinitionHandle.ByDefinitionId(BookmarkThenBlockWorkflow.DefinitionId, VersionOptions.Latest)
            });

            // The bookmark is in the database and the run is still going: the window a resuming child or timer would hit.
            var bookmarkId = await probe.BookmarkCommitted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await using (var probeHandle = await SelectiveMockProvider.RealProvider.TryAcquireLockAsync(LockName(instanceId)))
                Assert.Null(probeHandle);

            var resumeClient = await WorkflowRuntime.CreateClientAsync(instanceId);
            var resume = resumeClient.RunInstanceAsync(new RunWorkflowInstanceRequest { BookmarkId = bookmarkId });

            await Task.Delay(TimeSpan.FromSeconds(1));
            Assert.False(resume.IsCompleted, "The bookmark was resumed while the instance's first run still held it.");

            probe.Release.SetResult();
            var firstResponse = await firstRun.WaitAsync(TimeSpan.FromSeconds(30));
            var resumeResponse = await resume.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(WorkflowSubStatus.Suspended, firstResponse.SubStatus);
            Assert.Equal(WorkflowStatus.Finished, resumeResponse.Status);
            Assert.False(probe.ResumedWhileRunning);
        }
        finally
        {
            probe.Release.TrySetResult();
            BookmarkThenBlockProbe.Remove(instanceId);
        }
    }

    private static string LockName(string workflowInstanceId) => $"workflow-instance:{workflowInstanceId}";

    private IAsyncDisposable UseDeferredLock(bool deferred)
    {
        var options = LockingOptions;
        var previous = options.DeferNewInstanceLock;
        options.DeferNewInstanceLock = deferred;
        return new RestoreOption(() => options.DeferNewInstanceLock = previous);
    }

    private sealed class RestoreOption(Action restore) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            restore();
            return ValueTask.CompletedTask;
        }
    }
}
