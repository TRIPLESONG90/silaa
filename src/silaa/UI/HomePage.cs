using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using silaa.ViewModels;

namespace silaa.UI;

public sealed partial class HomePage : UserControl
{
    public HomePage()
    {
        AvaloniaXamlLoader.Load(this);
        var input = this.FindControl<TextBox>("QuickPromptBox")
                    ?? throw new InvalidOperationException("QuickPromptBox not found.");
        input.AddHandler(KeyDownEvent, OnQuickPromptKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnQuickPromptKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || args.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        if (DataContext is not HomePageViewModel viewModel) return;
        args.Handled = true;
        if (viewModel.BeginConversationCommand.CanExecute(null))
            viewModel.BeginConversationCommand.Execute(null);
    }
}
