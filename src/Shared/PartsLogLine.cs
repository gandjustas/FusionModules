using Microsoft.Extensions.Logging;

namespace FusionModules;

/// <summary>
/// One structured log line made of labelled parts — the host's <c>Topology:</c> line and the EF Core
/// package's <c>Model of …:</c> line — written alike: every part <c>label: value</c>, separated by
/// <c>;</c>, an empty part left out of the message, and every part a field of the entry, empty or not,
/// so that a log store finds the same fields on every replica.
/// </summary>
/// <remarks>
/// Compiled into the FusionModules package and the EF Core package, each as an internal type of its
/// own. The message template is built from the parts that are there, so a log store that renders the
/// template (<c>{OriginalFormat}</c>) shows the line as it was written.
/// </remarks>
internal static class PartsLogLine
{
    /// <summary>Writes the line at <see cref="LogLevel.Information"/>.</summary>
    /// <param name="logger">Where it goes.</param>
    /// <param name="prefix">What comes before the parts, as a template and as written: <c>Model of {Context}: </c>.</param>
    /// <param name="leading">Fields the prefix's template names.</param>
    /// <param name="parts">The parts, in the order they are written.</param>
    /// <param name="whenEmpty">What follows the prefix when every part is empty.</param>
    public static void Write(
        ILogger logger,
        (string Template, string Message) prefix,
        IEnumerable<KeyValuePair<string, object?>> leading,
        IReadOnlyList<(string Label, string Field, string Value)> parts,
        string whenEmpty = "")
    {
        var present = parts.Where(part => part.Value.Length > 0).ToList();
        var template = prefix.Template + (present.Count == 0 ? whenEmpty : string.Join("; ", present.Select(part => $"{part.Label}: {{{part.Field}}}")));
        var message = prefix.Message + (present.Count == 0 ? whenEmpty : string.Join("; ", present.Select(part => $"{part.Label}: {part.Value}")));

        var fields = leading
            .Concat(parts.Select(part => new KeyValuePair<string, object?>(part.Field, part.Value)))
            .Append(new KeyValuePair<string, object?>("{OriginalFormat}", template))
            .ToList();

        logger.Log(LogLevel.Information, default, fields, null, (_, _) => message);
    }
}
