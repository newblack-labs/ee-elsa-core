using ConsoleLogStreaming.Core.Options;
using Elsa.Diagnostics.ConsoleLogs.Extensions;
using Elsa.Diagnostics.ConsoleLogs.Services;
using Elsa.Extensions;
using Elsa.Features.Abstractions;
using Elsa.Features.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Elsa.Diagnostics.ConsoleLogs.Features;

public class ConsoleLogsFeature(IModule module) : FeatureBase(module)
{
    public Action<ConsoleLogOptions>? ConfigureOptions { get; set; }

    public override void Configure()
    {
        Module.AddFastEndpointsAssembly<ConsoleLogsFeature>();
    }

    public override void Apply()
    {
        Services.AddConsoleLogsServices(ConfigureOptions);

        // AddConsoleLogsServices only arranges for capture to start through CShells, as a scoped
        // IShellInitializer resolved by the shell lifecycle. A classic Elsa host has no shell
        // lifecycle, so nothing ever calls IConsoleLogCapture.StartAsync there: the REST and SignalR
        // surfaces come up and report a healthy connection, but no console line is ever captured.
        // Start it from the host lifetime instead, which is what AddConsoleLogsHost does for hosts
        // that wire this module up themselves. Only the classic feature does this — the CShells shell
        // feature keeps using the shell initializer, so capture is still started exactly once.
        Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ConsoleLogCaptureHostedService>());

        Module.AddFastEndpointsFromModule();
    }
}
