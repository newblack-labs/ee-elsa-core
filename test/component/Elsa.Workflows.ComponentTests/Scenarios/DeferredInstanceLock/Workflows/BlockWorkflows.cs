using Elsa.Workflows.ComponentTests.Scenarios.DeferredInstanceLock.Activities;

namespace Elsa.Workflows.ComponentTests.Scenarios.DeferredInstanceLock.Workflows;

/// <summary>Waits without committing: a run that stores its instance only at its end.</summary>
public class BlockWithoutCommitWorkflow : WorkflowBase
{
    public static readonly string DefinitionId = Guid.NewGuid().ToString();

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.WithDefinitionId(DefinitionId);
        builder.Root = new BlockUntilReleased();
    }
}

/// <summary>Commits without a bookmark, then waits: an instance stored and executing before a slow step, as a commit strategy
/// that commits around activities leaves it.</summary>
public class CommitThenBlockWorkflow : WorkflowBase
{
    public static readonly string DefinitionId = Guid.NewGuid().ToString();

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.WithDefinitionId(DefinitionId);
        builder.Root = new BlockUntilReleased(commitFirst: true);
    }
}
