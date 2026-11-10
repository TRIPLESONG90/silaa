using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace silaa.ViewModels;

public partial class BrowserViewModel : ObservableObject
{
    private readonly Action _navigate;
    private readonly Action _goBack;
    private readonly Action _goForward;
    private readonly Action _refresh;
    private readonly Action _return;

    public BrowserViewModel(NavigationViewModel navigation, Action navigate, Action goBack,
        Action goForward, Action refresh, Action returnToPage)
    {
        Navigation = navigation;
        _navigate = navigate;
        _goBack = goBack;
        _goForward = goForward;
        _refresh = refresh;
        _return = returnToPage;
    }

    public NavigationViewModel Navigation { get; }

    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _status = "준비";

    [RelayCommand] private void Navigate() => _navigate();
    [RelayCommand] private void GoBack() => _goBack();
    [RelayCommand] private void GoForward() => _goForward();
    [RelayCommand] private void Refresh() => _refresh();
    [RelayCommand] private void ReturnToPage() => _return();
}
