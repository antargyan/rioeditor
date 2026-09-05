using System.Text.Json.Serialization;
using RioEditor.Core.Models;

namespace RioEditor.Core.Editor;

/// <summary>
/// The host -> engine envelope. One flexible shape rather than a hierarchy, because the payload
/// crosses a JavaScript boundary where a discriminated union buys nothing.
///
/// Serialized through <see cref="EditorJsonContext"/> (source-generated): WebAssembly publishes
/// with trimming, where reflection-based <c>JsonSerializer</c> is disabled outright.
/// </summary>
internal sealed class HostMessage
{
    public required string Type { get; init; }

    public string? Html { get; init; }

    public string? Markdown { get; init; }

    public string? RequestId { get; init; }

    /// <summary>Command name for <c>type: "command"</c> (bold, heading, link, …).</summary>
    public string? Name { get; init; }

    /// <summary>Numeric command argument — currently only the heading level.</summary>
    public int? Level { get; init; }

    /// <summary>Payload of a <c>hostResponse</c> (the answer to an engine-initiated request).</summary>
    public string? Value { get; init; }

    public string? Url { get; init; }

    public string? Text { get; init; }

    public string? Language { get; init; }

    public int? Rows { get; init; }

    public int? Columns { get; init; }

    public string? Theme { get; init; }

    /// <summary>Which piece of state the host is asking the engine for.</summary>
    public string? Request { get; init; }
}

/// <summary>
/// Source-generated serialization contract for everything RioEditor writes as JSON.
/// Keeping every serializable type listed here is what makes the app trim- and AOT-safe.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(HostMessage))]
[JsonSerializable(typeof(string))]
internal sealed partial class EditorJsonContext : JsonSerializerContext;

/// <summary>
/// Separate context for persisted state. A source-generated context bakes its options in, so
/// "indented, for a file a human may open" needs its own context rather than a cloned
/// <see cref="JsonSerializerOptions"/> — cloning loses the generated metadata at runtime.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
