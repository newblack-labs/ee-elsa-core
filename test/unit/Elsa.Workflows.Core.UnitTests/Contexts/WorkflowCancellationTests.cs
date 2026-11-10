using System.Collections;
using System.Reflection;
using Elsa.Testing.Shared;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Models;

namespace Elsa.Workflows.Core.UnitTests.Contexts;

/// <summary>
/// Cancelling a workflow's token must never throw, also when the workflow finished in the meantime: when a timer cancels
/// the token, the callback runs on a thread pool thread and an exception there terminates the process.
/// </summary>
public class WorkflowCancellationTests
{
    [Fact]
    public async Task Cancel_RunningWorkflow_EndsCancelled()
    {
        var context = await CreateContextAsync();
        SetSubStatus(context, WorkflowSubStatus.Executing);

        context.Cancel();

        Assert.Equal(WorkflowSubStatus.Cancelled, context.SubStatus);
        Assert.Contains(context.ExecutionLog, x => x.EventName == "Workflow cancelled");
    }

    [Theory]
    [InlineData(WorkflowSubStatus.Faulted)]
    [InlineData(WorkflowSubStatus.Finished)]
    [InlineData(WorkflowSubStatus.Cancelled)]
    public async Task Cancel_FinishedWorkflow_DoesNotThrowAndKeepsStatus(WorkflowSubStatus finished)
    {
        var context = await CreateContextAsync();
        SetSubStatus(context, finished);

        var exception = Record.Exception(context.Cancel);

        Assert.Null(exception);
        Assert.Equal(finished, context.SubStatus);
    }

    [Fact]
    public async Task Cancel_WorkflowThatFaultsBetweenTheCheckAndTheTransition_DoesNotThrowAndStaysFaulted()
    {
        var context = await CreateContextAsync();
        SetSubStatus(context, WorkflowSubStatus.Executing);

        // The workflow's own thread faults it after CancelWorkflow checked that it can still be cancelled and while it
        // writes its log entry: the entry is the last thing that happens before the transition.
        ReplaceExecutionLog(context, new FaultingLog(() => SetSubStatus(context, WorkflowSubStatus.Faulted)));

        var exception = Record.Exception(context.Cancel);

        Assert.Null(exception);
        Assert.Equal(WorkflowSubStatus.Faulted, context.SubStatus);
    }

    private static async Task<WorkflowExecutionContext> CreateContextAsync()
    {
        var fixture = new ActivityTestFixture(new Sequence());
        return (await fixture.BuildAsync()).WorkflowExecutionContext;
    }

    private static void SetSubStatus(WorkflowExecutionContext context, WorkflowSubStatus subStatus) =>
        typeof(WorkflowExecutionContext).GetProperty(nameof(WorkflowExecutionContext.SubStatus))!
            .GetSetMethod(nonPublic: true)!.Invoke(context, [subStatus]);

    private static void ReplaceExecutionLog(WorkflowExecutionContext context, ICollection<WorkflowExecutionLogEntry> log) =>
        typeof(WorkflowExecutionContext)
            .GetField($"<{nameof(WorkflowExecutionContext.ExecutionLog)}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, log);

    private sealed class FaultingLog(Action onAdd) : ICollection<WorkflowExecutionLogEntry>
    {
        private readonly List<WorkflowExecutionLogEntry> _entries = [];

        public void Add(WorkflowExecutionLogEntry item)
        {
            _entries.Add(item);
            onAdd();
        }

        public int Count => _entries.Count;
        public bool IsReadOnly => false;
        public void Clear() => _entries.Clear();
        public bool Contains(WorkflowExecutionLogEntry item) => _entries.Contains(item);
        public void CopyTo(WorkflowExecutionLogEntry[] array, int arrayIndex) => _entries.CopyTo(array, arrayIndex);
        public bool Remove(WorkflowExecutionLogEntry item) => _entries.Remove(item);
        public IEnumerator<WorkflowExecutionLogEntry> GetEnumerator() => _entries.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
