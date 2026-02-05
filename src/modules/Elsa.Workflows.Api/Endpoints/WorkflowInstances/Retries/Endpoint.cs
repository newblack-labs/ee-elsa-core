using Elsa.Abstractions;
using Elsa.Workflows.Runtime;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Http;

namespace Elsa.Workflows.Api.Endpoints.WorkflowInstances.Retries;

[PublicAPI]
internal class Retry(IWorkflowRetryService workflowRetryService)
    : ElsaEndpoint<Request>
{
    public override void Configure()
    {
        Post("/workflow-instances/{id}/retries");
        ConfigurePermissions("cancel:workflow-instances");
        //Allows for post with empty body
        Description(x => x.Accepts<Request>("*/*"), clearDefaults: true);
    }
    
    public override async Task HandleAsync(Request request, CancellationToken cancellationToken)
    {
        await workflowRetryService.RetryWorkflowAsync(request.Id, cancellationToken);
        
        await SendOkAsync(cancellationToken);
    }
}