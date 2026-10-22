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

namespace Elsa.Workflows.ComponentTests.Scenarios.DeferredInstanceLock;

/// <summary>
/// <see cref="DistributedLockingOptions.DeferNewInstanceLock"/>: a new instance takes its lock only when it persists its first
/// bookmark, and still cannot be resumed while its first run is going on.
/// </summary>
public class DeferredInstanceLockTests(App app) : AppComponentTest(app)
{
    private SelectiveMockLockProvider SelectiveMockProvider => Scope.ServiceProvider.GetRequiredService<SelectiveMockLockProvider>();
    private DistributedLockingOptions LockingOptions => Scope.ServiceProvider.GetRequiredService<IOptions<DistributedLockingOptions>>().Value;
    private IWorkflowRuntime WorkflowRuntime => Scope.ServiceProvider.GetRequiredService<IWorkflowRuntime>();

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task RunThatNeverWaits_LocksOnlyWhenNotDeferred(bool deferred, int expectedAcquisitions)
    {
        await using var _ = UseDeferredLock(deferred);
        var client = await WorkflowRuntime.CreateClientAsync();
        var lockName = LockName(client.WorkflowInstanceId);
        var counter = SelectiveMockProvider.MockLock(lockName);
        counter.Reset();

        try
        {
            var response = await client.CreateAndRunInstanceAsync(new()
            {
                WorkflowDefinitionHandle = WorkflowDefinitionHandle.ByDefinitionId(RunsStraightThroughWorkflow.DefinitionId, VersionOptions.Latest)
            });

            Assert.Equal(WorkflowStatus.Finished, response.Status);
            Assert.Equal(expectedAcquisitions, counter.AcquisitionAttemptCount);
        }
        finally
        {
            SelectiveMockProvider.Unmock(lockName);
        }
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
