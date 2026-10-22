using Elsa.Extensions;
using Elsa.Workflows.ComponentTests.Scenarios.DeferredInstanceLock.Activities;

namespace Elsa.Workflows.ComponentTests.Scenarios.DeferredInstanceLock.Workflows;

// No WriteLine here or in RunsStraightThroughWorkflow: AsyncWorkflowRunner counts the WriteLine records of every workflow
// that runs at the time, so other scenarios would count these.
public class BookmarkThenBlockWorkflow : WorkflowBase
{
    public static readonly string DefinitionId = Guid.NewGuid().ToString();

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.WithDefinitionId(DefinitionId);
        builder.Root = new BookmarkThenBlock();
    }
}
