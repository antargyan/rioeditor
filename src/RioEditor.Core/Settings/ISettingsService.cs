using RioEditor.Core.Models;

namespace RioEditor.Core.Settings;

public interface ISettingsService
{
    /// <summary>The live settings instance. Mutate then call <see cref="SaveAsync"/>.</summary>
    AppSettings Current { get; }

    ValueTask<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(CancellationToken cancellationToken = default);
}
