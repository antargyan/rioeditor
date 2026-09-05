using RioEditor.Core.Settings;

namespace RioEditor.Core.Sponsorship;

/// <summary>
/// The thresholds below are deliberately conservative. A donation prompt is a favour being asked of
/// someone who already chose to use the thing; asking early, often, or of someone who has barely
/// opened the app is how a free app makes itself annoying.
/// </summary>
public sealed class SponsorPolicy : ISponsorPolicy
{
    /// <summary>Nothing is asked in the first fortnight, however heavy the usage.</summary>
    private const int MinDaysSinceFirstLaunch = 14;

    /// <summary>Distinct days opened. Five separate days is a habit, not a trial.</summary>
    private const int MinActiveDays = 5;

    private const int MinLaunches = 8;

    /// <summary>Saves, so someone who only ever looked at the welcome document is never asked.</summary>
    private const int MinSaves = 3;

    /// <summary>Asked at most three times in the lifetime of the install.</summary>
    private const int MaxPrompts = 3;

    /// <summary>Two months of silence between asks.</summary>
    private const int MinDaysBetweenPrompts = 60;

    private readonly ISettingsService _settings;
    private readonly TimeProvider _time;

    public SponsorPolicy(ISettingsService settings, TimeProvider? time = null)
    {
        _settings = settings;
        _time = time ?? TimeProvider.System;
    }

    public Uri SponsorUri { get; } = new("https://github.com/sponsors/antargyan");

    public async ValueTask RecordLaunchAsync(CancellationToken cancellationToken = default)
    {
        var sponsor = _settings.Current.Sponsor;
        var now = _time.GetUtcNow();

        sponsor.FirstLaunchUtc ??= now;
        sponsor.LaunchCount++;

        // Count a day once, no matter how many times the app is opened within it.
        if (sponsor.LastActiveUtc is not { } last || last.UtcDateTime.Date != now.UtcDateTime.Date)
        {
            sponsor.ActiveDays++;
            sponsor.LastActiveUtc = now;
        }

        await _settings.SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RecordSaveAsync(CancellationToken cancellationToken = default)
    {
        _settings.Current.Sponsor.SaveCount++;
        await _settings.SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public bool ShouldPrompt()
    {
        var sponsor = _settings.Current.Sponsor;

        if (sponsor.Dismissed || sponsor.PromptCount >= MaxPrompts)
        {
            return false;
        }

        if (sponsor.FirstLaunchUtc is not { } firstLaunch)
        {
            return false;
        }

        var now = _time.GetUtcNow();

        if ((now - firstLaunch).TotalDays < MinDaysSinceFirstLaunch ||
            sponsor.ActiveDays < MinActiveDays ||
            sponsor.LaunchCount < MinLaunches ||
            sponsor.SaveCount < MinSaves)
        {
            return false;
        }

        // Already asked once: stay quiet for a long while before asking again.
        return sponsor.LastPromptUtc is not { } lastPrompt ||
               (now - lastPrompt).TotalDays >= MinDaysBetweenPrompts;
    }

    public async ValueTask RecordPromptShownAsync(CancellationToken cancellationToken = default)
    {
        var sponsor = _settings.Current.Sponsor;
        sponsor.PromptCount++;
        sponsor.LastPromptUtc = _time.GetUtcNow();
        await _settings.SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DismissForeverAsync(CancellationToken cancellationToken = default)
    {
        _settings.Current.Sponsor.Dismissed = true;
        await _settings.SaveAsync(cancellationToken).ConfigureAwait(false);
    }
}
