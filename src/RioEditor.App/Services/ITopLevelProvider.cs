using Avalonia.Controls;

namespace RioEditor.App.Services;

/// <summary>
/// Avalonia's storage APIs hang off a <see cref="TopLevel"/>, which view models must not reach for
/// directly. The root view publishes itself here once it is attached to the visual tree.
/// </summary>
public interface ITopLevelProvider
{
    TopLevel? TopLevel { get; set; }
}

public sealed class TopLevelProvider : ITopLevelProvider
{
    public TopLevel? TopLevel { get; set; }
}
