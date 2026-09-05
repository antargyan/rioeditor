using Avalonia.Controls;
using Avalonia.Controls.Templates;
using RioEditor.App.ViewModels;

namespace RioEditor.App;

/// <summary>Maps <c>*ViewModel</c> to <c>*View</c> by convention.</summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control Build(object? parameter)
    {
        if (parameter is null)
        {
            return new TextBlock { Text = "No view model" };
        }

        var name = parameter.GetType().FullName!
            .Replace("ViewModel", "View", StringComparison.Ordinal)
            .Replace(".Views.Views", ".Views", StringComparison.Ordinal);

        var type = Type.GetType(name);
        return type is not null
            ? (Control)Activator.CreateInstance(type)!
            : new TextBlock { Text = $"Not found: {name}" };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
