namespace RioEditor.Core.Models;

/// <summary>
/// Usage evidence behind the sponsorship prompt. Counters only — nothing here identifies the user
/// or leaves the device; it exists so the app can tell "actually uses this" from "opened it once".
/// </summary>
public sealed class SponsorSettings
{
    public DateTimeOffset? FirstLaunchUtc { get; set; }

    public int LaunchCount { get; set; }

    /// <summary>Distinct calendar days the app was opened — the honest measure of a habit.</summary>
    public int ActiveDays { get; set; }

    public DateTimeOffset? LastActiveUtc { get; set; }

    /// <summary>Successful saves. Real work, as opposed to merely launching the app.</summary>
    public int SaveCount { get; set; }

    public int PromptCount { get; set; }

    public DateTimeOffset? LastPromptUtc { get; set; }

    /// <summary>Set by "Sponsor" or "No thanks". Once true, the prompt never appears again.</summary>
    public bool Dismissed { get; set; }
}
