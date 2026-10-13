using System.Diagnostics.CodeAnalysis;
using Elsa.Studio.Contracts;
using Elsa.Studio.Extensions;
using Elsa.Studio.Models;
using Microsoft.AspNetCore.Components;

namespace Elsa.Studio.Services;

/// <inheritdoc />
public class DefaultAppBarService : IAppBarService
{
    private readonly ICollection<AppBarElement> _elements = new List<AppBarElement>();

    // Keyed on the contributing type, which is the only stable identity available. AppBarElement is a
    // plain class with reference equality, and every Add builds a fresh instance holding a fresh
    // render-fragment delegate, so AddElement's own Contains check can never match a repeat
    // contribution. MainLayout adds DarkModeToggle and ProductInfo from OnInitialized, and the shell is
    // rebuilt when the layout changes — moving between the sign-in screen's layout and the app's — so
    // each sign-in appended another copy of both icons until the next hard refresh.
    private readonly HashSet<Type> _contributedTypes = [];

    /// <inheritdoc />
    public event Action? AppBarItemsChanged;

    /// <inheritdoc />
    public IEnumerable<AppBarElement> AppBarElements => _elements.OrderBy(x => x.Order).ToList();

    /// <inheritdoc />
    public IEnumerable<RenderFragment> AppBarComponents => AppBarElements.Select(x => x.Component).ToList();

    /// <inheritdoc />
    public void AddAppBarItem<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>() where T : IComponent
    {
        AddComponent<T>();
    }

    /// <inheritdoc />
    public void AddComponent<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(float? order = null) where T : IComponent
    {
        // Keyed on the component type, not the element type: every call here produces a plain
        // AppBarElement, so keying on the element would collapse all components into one.
        if (!_contributedTypes.Add(typeof(T)))
            return;

        AddElement(new AppBarElement
        {
            Order = order ?? 0,
            Component = builder => builder.CreateComponent<T>()
        });
    }

    /// <inheritdoc />
    public void AddElement<T>(float? order = null) where T : AppBarElement, new()
    {
        // Same guard for the element-typed overload. Nothing in the tree calls this today (every
        // contributor uses AddComponent), so it is guarded for consistency rather than to fix an
        // observed duplicate — the deprecated entry point is AddAppBarItem, not AddComponent.
        if (!_contributedTypes.Add(typeof(T)))
            return;

        var element = new T();

        if (order.HasValue)
            element.Order = order.Value;

        AddElement(element);
    }

    /// <inheritdoc />
    public void AddElement(AppBarElement element)
    {
        // The raw escape hatch: the caller supplies the instance, so identity is theirs to manage and
        // only reference equality is checked here.
        if (_elements.Contains(element))
            return;

        _elements.Add(element);
        AppBarItemsChanged?.Invoke();
    }
}
