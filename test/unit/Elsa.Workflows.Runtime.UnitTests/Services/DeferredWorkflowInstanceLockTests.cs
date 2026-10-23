using Medallion.Threading;
using NSubstitute;

namespace Elsa.Workflows.Runtime.UnitTests.Services;

/// <summary>
/// <see cref="DeferredWorkflowInstanceLock"/>: the lock of a new instance is taken once, by the first commit of that instance,
/// a failure to take it fails every later commit, and it is released once at the end.
/// </summary>
public class DeferredWorkflowInstanceLockTests
{
    private int _acquisitions;
    private int _releases;

    private Task<IDistributedSynchronizationHandle?> Acquire(CancellationToken _)
    {
        Interlocked.Increment(ref _acquisitions);
        return Task.FromResult<IDistributedSynchronizationHandle?>(Substitute.For<IDistributedSynchronizationHandle>());
    }

    private Task Release(IDistributedSynchronizationHandle? _)
    {
        Interlocked.Increment(ref _releases);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Nothing_is_locked_until_a_commit_asks()
    {
        await using (var scope = DeferredWorkflowInstanceLock.Begin("instance-1", Acquire, Release))
            Assert.False(scope.IsAcquired);

        Assert.Equal(0, _acquisitions);
        Assert.Equal(0, _releases);
    }

    [Fact]
    public async Task The_first_commit_takes_the_lock_and_the_end_of_the_run_releases_it_once()
    {
        var scope = DeferredWorkflowInstanceLock.Begin("instance-1", Acquire, Release);

        await DeferredWorkflowInstanceLock.EnsureAcquiredAsync("instance-1");
        await DeferredWorkflowInstanceLock.EnsureAcquiredAsync("instance-1");
        Assert.True(scope.IsAcquired);
        await scope.DisposeAsync();
        await scope.DisposeAsync();

        Assert.Equal(1, _acquisitions);
        Assert.Equal(1, _releases);
    }

    [Fact]
    public async Task Callers_racing_on_the_run_take_the_lock_once()
    {
        // The scope flows into tasks the run starts, so two commits could ask at once.
        var gate = new TaskCompletionSource();
        var scope = DeferredWorkflowInstanceLock.Begin("instance-1", async ct =>
        {
            Interlocked.Increment(ref _acquisitions);
            await gate.Task;
            return Substitute.For<IDistributedSynchronizationHandle>();
        }, Release);

        var first = Task.Run(() => DeferredWorkflowInstanceLock.EnsureAcquiredAsync("instance-1"));
        var second = Task.Run(() => DeferredWorkflowInstanceLock.EnsureAcquiredAsync("instance-1"));
        await Task.Delay(50);
        gate.SetResult();
        await Task.WhenAll(first, second);
        await scope.DisposeAsync();

        Assert.Equal(1, _acquisitions);
        Assert.Equal(1, _releases);
    }

    [Fact]
    public async Task A_lock_that_could_not_be_taken_fails_every_later_commit_without_trying_again()
    {
        var scope = DeferredWorkflowInstanceLock.Begin("instance-1", _ =>
        {
            Interlocked.Increment(ref _acquisitions);
            return Task.FromException<IDistributedSynchronizationHandle?>(new TimeoutException("lock busy"));
        }, Release);

        await Assert.ThrowsAsync<TimeoutException>(() => DeferredWorkflowInstanceLock.EnsureAcquiredAsync("instance-1"));
        await Assert.ThrowsAsync<TimeoutException>(() => DeferredWorkflowInstanceLock.EnsureAcquiredAsync("instance-1"));
        await scope.DisposeAsync();

        Assert.Equal(1, _acquisitions);
        Assert.Equal(0, _releases);
    }

    [Fact]
    public async Task Another_instances_commit_does_not_take_this_lock()
    {
        await using var scope = DeferredWorkflowInstanceLock.Begin("instance-1", Acquire, Release);

        await DeferredWorkflowInstanceLock.EnsureAcquiredAsync("instance-2");

        Assert.False(scope.IsAcquired);
        Assert.Equal(0, _acquisitions);
    }

    [Fact]
    public async Task A_child_run_on_the_same_flow_does_not_hide_its_parent_once_it_returns()
    {
        await using var parent = DeferredWorkflowInstanceLock.Begin("parent", Acquire, Release);

        await RunChildAsync();
        await DeferredWorkflowInstanceLock.EnsureAcquiredAsync("parent");

        Assert.True(parent.IsAcquired);
        Assert.Equal(2, _acquisitions);
    }

    [Fact]
    public async Task A_commit_after_the_run_ended_takes_nothing()
    {
        var scope = DeferredWorkflowInstanceLock.Begin("instance-1", Acquire, Release);
        await scope.DisposeAsync();

        await DeferredWorkflowInstanceLock.EnsureAcquiredAsync("instance-1");

        Assert.Equal(0, _acquisitions);
    }

    [Fact]
    public async Task Without_a_deferred_scope_a_commit_takes_nothing()
    {
        await DeferredWorkflowInstanceLock.EnsureAcquiredAsync("instance-1");

        Assert.Equal(0, _acquisitions);
    }

    private async Task RunChildAsync()
    {
        // As DistributedWorkflowClient.CreateAndRunInstanceAsync does for an in-process child: its own scope, gone when it returns.
        await using var child = DeferredWorkflowInstanceLock.Begin("child", Acquire, Release);
        await DeferredWorkflowInstanceLock.EnsureAcquiredAsync("child");
        await DeferredWorkflowInstanceLock.EnsureAcquiredAsync("parent");
        Assert.True(child.IsAcquired);
    }
}
