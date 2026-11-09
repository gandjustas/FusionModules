using System.Text.Json;

namespace FusionModules.Tool;

internal static class AppSettingsReader
{
    /// <summary>
    /// appsettings.json and appsettings.*.json beside the project, flattened to configuration keys
    /// (Logging:LogLevel:Default) — the same keys IConfiguration and the environment use, so a
    /// value from compose or Aspire can be compared with a value from a file.
    /// </summary>
    public static SortedDictionary<string, SortedDictionary<string, string?>>? Read(string projectDirectory)
    {
        var files = Directory.EnumerateFiles(projectDirectory, "appsettings*.json")
            .Where(f => Path.GetFileName(f).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (files.Count == 0)
        {
            return null;
        }

        var result = new SortedDictionary<string, SortedDictionary<string, string?>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var keys = new SortedDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                Flatten(document.RootElement, "", keys);
            }
            catch (JsonException exception)
            {
                keys["(unreadable)"] = exception.Message;
            }

            result[Path.GetFileName(file)] = keys;
        }

        return result;
    }

    private static void Flatten(JsonElement element, string prefix, SortedDictionary<string, string?> keys)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                // A "//" property is a comment by convention, and $schema is for the editor: neither is
                // configuration anything reads.
                foreach (var property in element.EnumerateObject().Where(property => !property.Name.StartsWith("//", StringComparison.Ordinal) && property.Name != "$schema"))
                {
                    Flatten(property.Value, prefix.Length == 0 ? property.Name : $"{prefix}:{property.Name}", keys);
                }

                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Flatten(item, $"{prefix}:{index++}", keys);
                }

                break;
            case JsonValueKind.Null:
                keys[prefix] = null;
                break;
            default:
                keys[prefix] = element.ToString();
                break;
        }
    }

    /// <summary>An environment variable name as a configuration key: ConnectionStrings__db is ConnectionStrings:db.</summary>
    public static string KeyOf(string environmentVariable) => environmentVariable.Replace("__", ":", StringComparison.Ordinal);
}

internal sealed record LaunchProfile(string Name, Dictionary<string, string> Environment, string? ApplicationUrl, string? CommandLineArgs);

internal static class LaunchSettingsReader
{
    /// <summary>Properties/launchSettings.json: the profiles `dotnet run` would offer, "Project" ones only.</summary>
    public static List<LaunchProfile> Read(string projectDirectory)
    {
        var path = Path.Combine(projectDirectory, "Properties", "launchSettings.json");
        var profiles = new List<LaunchProfile>();
        if (!File.Exists(path))
        {
            return profiles;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!document.RootElement.TryGetProperty("profiles", out var elements))
            {
                return profiles;
            }

            foreach (var profile in elements.EnumerateObject())
            {
                if (profile.Value.TryGetProperty("commandName", out var command) && command.GetString() != "Project")
                {
                    continue;
                }

                var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (profile.Value.TryGetProperty("environmentVariables", out var variables) && variables.ValueKind == JsonValueKind.Object)
                {
                    foreach (var variable in variables.EnumerateObject())
                    {
                        environment[variable.Name] = variable.Value.ToString();
                    }
                }

                profiles.Add(new LaunchProfile(
                    profile.Name,
                    environment,
                    profile.Value.TryGetProperty("applicationUrl", out var url) ? url.GetString() : null,
                    profile.Value.TryGetProperty("commandLineArgs", out var args) ? args.GetString() : null));
            }
        }
        catch (JsonException)
        {
        }

        return profiles;
    }
}
