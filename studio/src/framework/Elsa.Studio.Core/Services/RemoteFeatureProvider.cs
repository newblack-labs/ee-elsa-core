using System.Net;
using Elsa.Api.Client.Resources.Features.Contracts;
using Elsa.Api.Client.Resources.Features.Models;
using Elsa.Studio.Contracts;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using Refit;

namespace Elsa.Studio.Services;

/// <summary>
/// A feature service that uses a remote backend to retrieve feature flags.
/// </summary>
public class RemoteFeatureProvider(
    IBackendApiClientProvider remoteBackendApiClientProvider,
    AuthenticationStateProvider? authenticationStateProvider = null,
    ILogger<RemoteFeatureProvider>? logger = null) : IRemoteFeatureProvider
{
    /// <summary>
    /// How long an unavailable catalog is treated as unavailable before another attempt.
    /// </summary>
    /// <remarks>
    /// Short enough that a backend still starting is picked up on the next menu rebuild, long enough
    /// that a permanently absent endpoint does not produce one request per feature-gated menu provider
    /// per rebuild — several of them, serialized behind the lock, on every token change.
    /// </remarks>
    private static readonly TimeSpan UnavailableBackoff = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _catalogLock = new(1, 1);
    private IReadOnlyCollection<FeatureDescriptor>? _catalog;
    private Uri? _catalogUrl;
    private Uri? _unavailableUrl;
    private DateTimeOffset _retryCatalogAfter = DateTimeOffset.MinValue;
    private bool _loggedUnavailable;

    /// <inheritdoc />
    public async Task<bool> IsEnabledAsync(string featureName, CancellationToken cancellationToken = default)
    {
        var catalog = await GetCatalogAsync(cancellationToken);
        return RemoteFeatureCatalog.Contains(catalog, featureName);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<FeatureDescriptor>> ListAsync(CancellationToken cancellationToken = default) =>
        await GetCatalogAsync(cancellationToken);

    private async Task<IReadOnlyCollection<FeatureDescriptor>> GetCatalogAsync(CancellationToken cancellationToken)
    {
        if (authenticationStateProvider is not null)
        {
            var authenticationState = await authenticationStateProvider.GetAuthenticationStateAsync();
            if (authenticationState.User.Identity?.IsAuthenticated != true)
                return [];
        }

        if (_catalog is not null && _catalogUrl == remoteBackendApiClientProvider.Url)
            return _catalog;

        // Backed off rather than cached for the session. A 404 here is as likely to be transient as
        // permanent — the backend still starting, or a stale proxy route — and caching the empty result
        // hid every [RemoteFeature]-gated menu for the rest of the session, since the gate fails
        // closed. Retrying on every single call instead would storm a permanently absent endpoint.
        // The backoff belongs to the backend that answered 404, so switching environments retries at once.
        if (IsBackingOff(remoteBackendApiClientProvider.Url))
            return [];

        await _catalogLock.WaitAsync(cancellationToken);
        try
        {
            var api = await remoteBackendApiClientProvider.GetApiAsync<IFeaturesApi>(cancellationToken);
            var backendUrl = remoteBackendApiClientProvider.Url;

            if (_catalog is not null && _catalogUrl == backendUrl)
                return _catalog;

            if (IsBackingOff(backendUrl))
                return [];

            try
            {
                var response = await api.ListAsync(cancellationToken);
                _catalog = response.Items.ToArray();
                _catalogUrl = backendUrl;
                _unavailableUrl = null;
                _loggedUnavailable = false;
                return _catalog;
            }
            catch (ApiException e) when (e.StatusCode is HttpStatusCode.NotFound)
            {
                _unavailableUrl = backendUrl;
                _retryCatalogAfter = DateTimeOffset.UtcNow + UnavailableBackoff;

                // Logged once per outage, not once per call: the gate fails closed, so without a line
                // here an incomplete sidebar is indistinguishable from a correctly gated one.
                if (!_loggedUnavailable)
                {
                    _loggedUnavailable = true;
                    logger?.LogWarning(
                        e,
                        "Feature catalog unavailable (404). Feature-gated navigation will be incomplete until the backend responds; retrying in {Backoff}",
                        UnavailableBackoff);
                }

                return [];
            }
            catch (ApiException e) when (e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // Expected before sign-in completes, so Debug rather than Warning — and not backed off,
                // because the very next call may be the authenticated one.
                logger?.LogDebug(e, "Feature catalog not readable yet ({StatusCode})", e.StatusCode);
                return [];
            }
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    private bool IsBackingOff(Uri? backendUrl) =>
        _unavailableUrl is not null && _unavailableUrl == backendUrl && DateTimeOffset.UtcNow < _retryCatalogAfter;
}
