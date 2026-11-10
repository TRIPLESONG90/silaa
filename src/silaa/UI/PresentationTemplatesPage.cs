using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Collections.Specialized;
using silaa.ViewModels;

namespace silaa.UI;

public sealed partial class PresentationTemplatesPage : UserControl
{
    private ScrollViewer _scroll = null!;
    private ScrollViewer _editorScroll = null!;
    private PresentationTemplatesViewModel? _viewModel;

    public PresentationTemplatesPage()
    {
        AvaloniaXamlLoader.Load(this);
        _scroll = this.FindControl<ScrollViewer>("ConversationScroll")
                  ?? throw new InvalidOperationException("ConversationScroll not found.");
        _editorScroll = this.FindControl<ScrollViewer>("EditorConversationScroll")
                        ?? throw new InvalidOperationException("EditorConversationScroll not found.");
        var input = this.FindControl<TextBox>("SurveyInput")
                    ?? throw new InvalidOperationException("SurveyInput not found.");
        input.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        var editorInput = this.FindControl<TextBox>("EditorInput")
                          ?? throw new InvalidOperationException("EditorInput not found.");
        editorInput.AddHandler(KeyDownEvent, OnEditorInputKeyDown, RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDragEnterHandler(this, OnFileDragEnter);
        DragDrop.AddDragOverHandler(this, OnFileDragOver);
        DragDrop.AddDragLeaveHandler(this, OnFileDragLeave);
        DragDrop.AddDropHandler(this, OnFileDrop);
        DataContextChanged += (_, _) => BindViewModel();
    }

    private void BindViewModel()
    {
        if (_viewModel is not null)
        {
            _viewModel.Messages.CollectionChanged -= OnMessagesChanged;
            _viewModel.EditorMessages.CollectionChanged -= OnMessagesChanged;
        }
        _viewModel = DataContext as PresentationTemplatesViewModel;
        if (_viewModel is not null)
        {
            _viewModel.Messages.CollectionChanged += OnMessagesChanged;
            _viewModel.EditorMessages.CollectionChanged += OnMessagesChanged;
        }
        ScrollToEnd();
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs args) => ScrollToEnd();

    private void ScrollToEnd() => Dispatcher.UIThread.Post(() =>
    {
        _scroll.ScrollToEnd();
        _editorScroll.ScrollToEnd();
    }, DispatcherPriority.Background);

    private void OnInputKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || args.KeyModifiers.HasFlag(KeyModifiers.Shift) || _viewModel is null) return;
        args.Handled = true;
        if (_viewModel.SendCommand.CanExecute(null)) _viewModel.SendCommand.Execute(null);
    }

    private void OnEditorInputKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || args.KeyModifiers.HasFlag(KeyModifiers.Shift) || _viewModel is null) return;
        args.Handled = true;
        if (_viewModel.SendEditorCommand.CanExecute(null)) _viewModel.SendEditorCommand.Execute(null);
    }

    private void OnFileDragOver(object? sender, DragEventArgs args)
    {
        var supported = args.DataTransfer.TryGetFiles()?.Any(file =>
            file.TryGetLocalPath() is { } path && IsSupportedTemplate(path)) == true;
        if (_viewModel is not null) _viewModel.IsDragOver = supported;
        args.DragEffects = supported ? DragDropEffects.Copy : DragDropEffects.None;
        args.Handled = true;
    }

    private void OnFileDragEnter(object? sender, DragEventArgs args) => OnFileDragOver(sender, args);

    private void OnFileDragLeave(object? sender, RoutedEventArgs args)
    {
        if (_viewModel is not null) _viewModel.IsDragOver = false;
    }

    private async void OnFileDrop(object? sender, DragEventArgs args)
    {
        args.Handled = true;
        if (_viewModel is null) return;
        _viewModel.IsDragOver = false;
        var paths = args.DataTransfer.TryGetFiles()?
            .Select(file => file.TryGetLocalPath())
            .Where(path => path is not null && IsSupportedTemplate(path))
            .Cast<string>()
            .ToList() ?? [];
        foreach (var path in paths) await _viewModel.ImportFileAsync(path);
    }

    private static bool IsSupportedTemplate(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".docx" or ".pptx" or ".html" or ".htm";
}
