using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace RioEditor.App.Views;

public partial class ToolbarView : UserControl
{
    public ToolbarView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
