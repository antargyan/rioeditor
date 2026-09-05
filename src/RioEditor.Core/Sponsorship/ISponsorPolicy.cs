namespace RioEditor.Core.Sponsorship;

/// <summary>
/// Decides whether and when to mention sponsorship. Kept as pure policy so the thresholds are
/// visible in one place and can be reasoned about without running the UI.
/// </summary>
public interface ISponsorPolicy
{
    /// <summary>The sponsorship page opened by the prompt.</summary>
    Uri SponsorUri { get; }

    /// <summary>Records an app start. Call once per launch, before <see cref="ShouldPromptAsync"/>.</summary>
    ValueTask RecordLaunchAsync(CancellationToken cancellationToken = default);

    /// <summary>Records a successful save — the app's evidence that real work happened.</summary>
    ValueTask RecordSaveAsync(CancellationToken cancellationToken = default);

    /// <summary>True only when every usage threshold is met and the quiet period has elapsed.</summary>
    bool ShouldPrompt();

    ValueTask RecordPromptShownAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops the prompt permanently. Used by both "Sponsor" and "No thanks".</summary>
    ValueTask DismissForeverAsync(CancellationToken cancellationToken = default);
}
