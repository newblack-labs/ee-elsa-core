using Elsa.Extensions;
using Elsa.Workflows.Activities;

namespace Elsa.Workflows.ComponentTests.Scenarios.DeferredInstanceLock.Workflows;

public class RunsStraightThroughWorkflow : WorkflowBase
{
    public static readonly string DefinitionId = Guid.NewGuid().ToString();

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.WithDefinitionId(DefinitionId);
        builder.Root = new Sequence();
    }
}
