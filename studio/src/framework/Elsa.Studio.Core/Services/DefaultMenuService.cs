using Elsa.Studio.Authorization;
using Elsa.Studio.Contracts;
using Elsa.Studio.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elsa.Studio.Services;

/// <inheritdoc />
public class DefaultMenuService : IMenuService
{
    private readonly IEnumerable<IMenuProvider> _menuProviders;
    private readonly IEnumerable<IMenuGroupProvider> _menuGroupProviders;
    private readonly IPermissionService? _permissionService;
    private readonly ILogger<DefaultMenuService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultMenuService"/> class.
    /// </summary>
    public DefaultMenuService(
        IEnumerable<IMenuProvider> menuProviders,
        IEnumerable<IMenuGroupProvider> menuGroupProviders,
        IPermissionService? permissionService = null,
        ILogger<DefaultMenuService>? logger = null)
    {
        _menuProviders = menuProviders;
        _menuGroupProviders = menuGroupProviders;
        _permissionService = permissionService;
        _logger = logger ?? NullLogger<DefaultMenuService>.Instance;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Each provider is isolated. Menu providers typically call the backend to decide what to
    /// contribute, so one unreachable API would otherwise abort the whole loop and cost the user every
    /// other provider's items — a navigation that is silently missing most of itself.
    /// </remarks>
    public async ValueTask<IEnumerable<MenuItem>> GetMenuItemsAsync(CancellationToken cancellationToken = default)
    {
        var menu = new List<MenuItem>();

        foreach (var menuProvider in _menuProviders)
        {
            try
            {
                var menuItems = await menuProvider.GetMenuItemsAsync(cancellationToken);
                menu.AddRange(menuItems);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogError(e, "Menu provider {MenuProvider} failed; its entries are missing from the navigation", menuProvider.GetType().Name);
            }
        }

        // No IPermissionService (Security not installed): do not filter. When the service is registered it
        // fail-closes via GET /identity/me/permissions — IdentityPermissionService never returns Unknown.
        var permissions = _permissionService != null
            ? await _permissionService.GetPermissionsAsync(cancellationToken)
            : UserPermissions.Unknown;

        return FilterByPermissions(menu, permissions).OrderBy(x => x.Order).ToList();
    }

    /// <inheritdoc />
    public async ValueTask<IEnumerable<MenuItemGroup>> GetMenuItemGroupsAsync(CancellationToken cancellationToken = default)
    {
        var groups = new List<MenuItemGroup>();

        foreach (var menuGroupProvider in _menuGroupProviders)
        {
            try
            {
                var menuGroups = await menuGroupProvider.GetMenuGroupsAsync(cancellationToken);
                groups.AddRange(menuGroups);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogError(e, "Menu group provider {MenuGroupProvider} failed; its groups are missing from the navigation", menuGroupProvider.GetType().Name);
            }
        }

        return groups.DistinctBy(x => x.Name).OrderBy(x => x.Order).ToList();
    }

    /// <summary>
    /// Removes the items the user lacks permissions for, and parents left without any visible children. Items are
    /// copied rather than mutated, since providers may hand out shared instances.
    /// </summary>
    private static IEnumerable<MenuItem> FilterByPermissions(IEnumerable<MenuItem> items, UserPermissions permissions)
    {
        foreach (var item in items)
        {
            if (!permissions.HasAll(item.RequiredPermissions))
            {
                continue;
            }

            if (item.SubMenuItems.Count == 0)
            {
                yield return item;
                continue;
            }

            var subMenuItems = FilterByPermissions(item.SubMenuItems, permissions).ToList();

            if (subMenuItems.Count > 0)
            {
                yield return item.WithSubMenuItems(subMenuItems);
            }
        }
    }
}
