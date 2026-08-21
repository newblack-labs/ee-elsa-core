using Elsa.ExternalAuthentication.Notifications;
using Elsa.Mediator.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Elsa.ExternalAuthentication.Services;

/// <summary>Publishes redacted security events after the caller has committed its state change.</summary>
public sealed class ExternalAuthenticationSecurityNotifier(IServiceProvider services)
{
    public async ValueTask PublishAsync(INotification notification, CancellationToken cancellationToken = default)
    {
        // This type is registered as a singleton, so the injected provider is the root container and
        // INotificationSender is scoped. Resolving it directly throws under scope validation (which
        // ASP.NET Core enables in Development, taking every brokered sign-in down with it) and, where
        // validation is off, captures a scoped service in the root container instead. Take a scope.
        await using var scope = services.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetService<INotificationSender>();

        if (sender is null)
            return;

        await sender.SendAsync(notification, cancellationToken);
    }

    public static SecurityEventContext Context(
        string? actorId,
        string? tenantId,
        string? connectionId,
        string? userId,
        SecurityEventOutcome outcome,
        string summary) => new(
        actorId,
        tenantId,
        connectionId,
        userId,
        DateTimeOffset.UtcNow,
        outcome,
        Guid.NewGuid().ToString("N"),
        summary);
}
