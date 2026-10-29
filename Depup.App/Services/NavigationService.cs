using Depup.App.ViewModels;

namespace Depup.App.Services;

public sealed class NavigationService : INavigationService
{
    private readonly Dictionary<string, object> _pages = new(StringComparer.OrdinalIgnoreCase);
    public object CurrentPage { get; private set; } = null!;

    public void Register(string name, object page)
    {
        _pages[name] = page;
        CurrentPage ??= page;
    }

    public void Navigate(string destination)
    {
        if (_pages.TryGetValue(destination, out var page)) CurrentPage = page;
    }
}
