namespace Depup.App.Services;

public interface INavigationService
{
    object CurrentPage { get; }
    void Register(string name, object page);
    void Navigate(string destination);
}
