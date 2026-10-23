using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Elsa.Workflows.ComponentTests.Scenarios.DeferredInstanceLock.Activities;

/// <summary>
/// Keeps the run going until the test releases it, optionally committing first: a run that waits on another system, with
/// or without having stored its instance yet.
/// </summary>
public class BlockUntilReleased : Activity
{
    public BlockUntilReleased(bool commitFirst = false, [CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
        CommitFirst = commitFirst;
    }

    public bool CommitFirst { get; set; }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var probe = RunProbe.For(context.WorkflowExecutionContext.Id);
        Interlocked.Increment(ref probe.Entered);
        if (CommitFirst)
            await context.WorkflowExecutionContext.CommitAsync();
        probe.Waiting.TrySetResult();
        await probe.Release.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await context.CompleteActivityAsync();
    }
}

/// <summary>What the test and <see cref="BlockUntilReleased"/> exchange, per workflow instance.</summary>
public class RunProbe
{
    private static readonly ConcurrentDictionary<string, RunProbe> Probes = new();

    public static RunProbe For(string workflowInstanceId) => Probes.GetOrAdd(workflowInstanceId, _ => new());

    public static void Remove(string workflowInstanceId) => Probes.TryRemove(workflowInstanceId, out _);

    /// <summary>How many runs entered the activity.</summary>
    public int Entered;

    /// <summary>Completed once a run is waiting in the activity (after its commit, when it commits first).</summary>
    public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completed by the test to let the run finish.</summary>
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
