using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace silaa.UI;

public sealed partial class AutomationPage : UserControl
{
    public AutomationPage() => AvaloniaXamlLoader.Load(this);
}
