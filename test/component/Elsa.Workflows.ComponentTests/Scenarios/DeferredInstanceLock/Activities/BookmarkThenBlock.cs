using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Elsa.Workflows.Models;

namespace Elsa.Workflows.ComponentTests.Scenarios.DeferredInstanceLock.Activities;

/// <summary>
/// Creates a bookmark, commits it so that the instance can be resumed, then keeps the run going until the test releases it —
/// the window in which a child workflow or timer could resume an instance that is still running.
/// </summary>
public class BookmarkThenBlock : Activity
{
    public BookmarkThenBlock([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var probe = BookmarkThenBlockProbe.For(context.WorkflowExecutionContext.Id);
        var bookmark = context.CreateBookmark(new CreateBookmarkArgs
        {
            BookmarkName = nameof(BookmarkThenBlock),
            Callback = OnResumedAsync
        });

        await context.WorkflowExecutionContext.CommitAsync();
        probe.BookmarkCommitted.TrySetResult(bookmark.Id);
        await probe.Release.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static ValueTask OnResumedAsync(ActivityExecutionContext context)
    {
        var probe = BookmarkThenBlockProbe.For(context.WorkflowExecutionContext.Id);
        probe.ResumedWhileRunning = !probe.Release.Task.IsCompleted;
        return context.CompleteActivityAsync();
    }
}

/// <summary>
/// What the test and <see cref="BookmarkThenBlock"/> exchange, per workflow instance.
/// </summary>
public class BookmarkThenBlockProbe
{
    private static readonly ConcurrentDictionary<string, BookmarkThenBlockProbe> Probes = new();

    public static BookmarkThenBlockProbe For(string workflowInstanceId) => Probes.GetOrAdd(workflowInstanceId, _ => new());

    public static void Remove(string workflowInstanceId) => Probes.TryRemove(workflowInstanceId, out _);

    /// <summary>Completed with the bookmark's id once it has been committed.</summary>
    public TaskCompletionSource<string> BookmarkCommitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completed by the test to let the first run finish.</summary>
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Whether the bookmark was resumed before the first run was released.</summary>
    public bool ResumedWhileRunning { get; set; }
}
