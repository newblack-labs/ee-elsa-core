using System.Reflection;
using Elsa.Studio.Attributes;
using Elsa.Studio.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elsa.Studio.Services;

/// <inheritdoc />
public class DefaultFeatureService : IFeatureService
{
    private readonly IEnumerable<IFeature> _features;
    private readonly IRemoteFeatureProvider _remoteFeatureProvider;
    private readonly ILogger<DefaultFeatureService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultFeatureService"/> class.
    /// </summary>
    public DefaultFeatureService(
        IEnumerable<IFeature> features,
        IRemoteFeatureProvider remoteFeatureProvider,
        ILogger<DefaultFeatureService>? logger = null)
    {
        _features = features;
        _remoteFeatureProvider = remoteFeatureProvider;
        _logger = logger ?? NullLogger<DefaultFeatureService>.Instance;
    }
    
    /// <inheritdoc />
    public event Action? Initialized;

    /// <inheritdoc />
    public bool IsInitialized { get; private set; }

    /// <inheritdoc />
    public IEnumerable<IFeature> GetFeatures()
    {
        return _features.ToList();
    }

    /// <inheritdoc />
    public async Task InitializeFeaturesAsync(CancellationToken cancellationToken = default)
    {
        var remoteFeatures = (await _remoteFeatureProvider.ListAsync(cancellationToken)).ToList();

        foreach (var feature in GetFeatures())
        {
            var remoteFeatureName = feature.GetType().GetCustomAttribute<RemoteFeatureAttribute>()?.Name;

            if (!string.IsNullOrWhiteSpace(remoteFeatureName) &&
                !RemoteFeatureCatalog.Contains(remoteFeatures, remoteFeatureName))
                continue;

            try
            {
                await feature.InitializeAsync(cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Isolated per feature, for the same reason DefaultMenuService isolates its providers:
                // one throwing feature would otherwise skip every feature after it AND never raise
                // Initialized, so the shell would come up with no dashboard widgets and no error. The
                // filter lets a genuine cancellation abort the loop, but keeps a TaskCanceledException
                // from an HTTP timeout — which is also an OperationCanceledException — contained.
                _logger.LogError(e, "Feature {Feature} failed to initialize and was skipped", feature.GetType().Name);
            }
        }

        OnInitialized();
    }

    private void OnInitialized()
    {
        IsInitialized = true;
        Initialized?.Invoke();
    }
}