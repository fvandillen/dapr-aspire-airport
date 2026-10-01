using Airport.Contracts;
using Dapr.Client;
using Dapr.Workflow;

namespace Airport.FlightOperations.Workflows;

public sealed class AirportResetWorkflow : Workflow<AirportResetRequest, bool>
{
    public const string InstancePrefix = "RESET-";
    public const string CompletedEventName = "atc-reset-completed";

    public override async Task<bool> RunAsync(WorkflowContext context, AirportResetRequest request)
    {
        await context.CallActivityAsync<bool>(nameof(PublishAirportResetActivity), request);
        var completed = await context.WaitForExternalEventAsync<AirportResetCompleted>(
            CompletedEventName, TimeSpan.FromSeconds(60));
        if (completed.WorkflowId != context.InstanceId)
            throw new InvalidOperationException("Reset acknowledgement does not match this workflow.");
        return true;
    }
}

public sealed class PublishAirportResetActivity(DaprClient dapr, ILogger<PublishAirportResetActivity> logger)
    : WorkflowActivity<AirportResetRequest, bool>
{
    public override async Task<bool> RunAsync(WorkflowActivityContext context, AirportResetRequest request)
    {
        await dapr.PublishEventAsync(DaprTopics.PubSubName, DaprTopics.AirportResetRequests, request);
        logger.LogInformation("Published airport reset request for workflow {WorkflowId}", request.WorkflowId);
        return true;
    }
}
