using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using silaa.ViewModels;

namespace silaa.UI;

public sealed partial class VaultPage : UserControl
{
    private VaultViewModel? _viewModel;

    public VaultPage()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => AttachViewModel(DataContext as VaultViewModel);
        KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape || _viewModel?.IsDialogOpen != true) return;
            args.Handled = true;
            _viewModel.CancelCommand.Execute(null);
        };
    }

    private void AttachViewModel(VaultViewModel? viewModel)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = viewModel;
        if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(VaultViewModel.IsDialogOpen) || _viewModel?.IsDialogOpen != true) return;
        Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("VaultNameInput")?.Focus());
    }
}
