using System.Text.Json.Serialization;

namespace RioEditor.Core.Models;

/// <summary>Editor colour scheme. Persisted verbatim in settings.json / localStorage.</summary>
public enum AppTheme
{
    Light,
    Dark
}

/// <summary>Everything that survives an application restart.</summary>
public sealed class AppSettings
{
    // The generic converter is the source-generator-friendly one; the non-generic
    // JsonStringEnumConverter needs reflection and is unusable in a trimmed WASM build.
    [JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
    public AppTheme Theme { get; set; } = AppTheme.Light;

    /// <summary>Last successfully opened/saved document; restored on startup when it still exists.</summary>
    public string? LastOpenedFile { get; set; }

    public double WindowWidth { get; set; } = 1180;

    public double WindowHeight { get; set; } = 780;

    public bool WindowMaximized { get; set; }

    /// <summary>Seconds between autosave passes. Zero disables autosave.</summary>
    public int AutosaveIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// WASM compatibility switches. The browser sandbox has no file system and no native WebView,
    /// so the head persists an in-browser draft instead of writing to disk.
    /// </summary>
    public WasmSettings Wasm { get; set; } = new();

    /// <summary>Usage counters behind the sponsorship prompt. Local only.</summary>
    public SponsorSettings Sponsor { get; set; } = new();
}

public sealed class WasmSettings
{
    /// <summary>Keep the working buffer in localStorage so a reload does not lose work.</summary>
    public bool PersistDraftInBrowserStorage { get; set; } = true;

    /// <summary>
    /// Load Mermaid/KaTeX from a CDN. Off means those blocks render as plain fenced code,
    /// which is what you want for an offline or CSP-locked deployment.
    /// </summary>
    public bool AllowRemoteScripts { get; set; } = true;

    /// <summary>Saving on WASM goes through a browser download rather than an in-place write.</summary>
    public bool UseDownloadFallbackForSave { get; set; } = true;
}
