// Builds the skill's three shapes from one source.
//
//   skill/src            the authored form, and also the plugin's shape
//   skill/plugin         a Claude Code plugin: SKILL.md plus references/ and assets/
//   skill/dist/SKILL.md  one portable file, references inlined, for tools that take a single
//                        markdown instruction file
//   skill/dist/fusion-modules-rules.md
//                        the routing layer alone, for an always-on rules file
//
// Run from the repository root:  dotnet run --project tools/skillgen
// CI runs it and fails if the working tree changed, so the outputs cannot drift from the source.

using System.Text;
using System.Text.RegularExpressions;

var root = FindRepositoryRoot(AppContext.BaseDirectory);
var source = Path.Combine(root, "skill", "src");
var plugin = Path.Combine(root, "skill", "plugin");
var dist = Path.Combine(root, "skill", "dist");

if (!Directory.Exists(source))
{
    Console.Error.WriteLine($"No skill source at {source}");
    return 1;
}

BuildPlugin();
BuildPortable();
BuildRules();

Console.WriteLine($"skill/plugin and skill/dist rebuilt from skill/src");
return 0;

// The plugin form is the source form, so this is a copy — but a copy that deletes first, so a
// renamed reference does not leave a stale file behind that nothing points at any more.
void BuildPlugin()
{
    var skill = Path.Combine(plugin, "skills", "fusion-modules");

    if (Directory.Exists(skill))
    {
        Directory.Delete(skill, recursive: true);
    }

    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        var target = Path.Combine(skill, Path.GetRelativePath(source, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target, overwrite: true);
    }
}

void BuildPortable()
{
    var (frontMatter, body) = ReadSkill();
    var references = ReadReferences();

    var output = new StringBuilder();
    output.AppendLine(frontMatter.TrimEnd());
    output.AppendLine();
    output.AppendLine(Linkify(body, references).TrimEnd());

    foreach (var reference in references)
    {
        output.AppendLine();
        output.AppendLine("---");
        output.AppendLine();
        output.AppendLine(Linkify(Demote(reference.Content), references).TrimEnd());
    }

    Write(Path.Combine(dist, "SKILL.md"), output.ToString());
}

// The routing layer on its own: the phases, the rules of engagement, and no detail. Sized for a
// Cursor rule or a copilot-instructions file, where everything is always in context and length is
// the whole cost.
void BuildRules()
{
    var (_, body) = ReadSkill();
    var references = ReadReferences();

    // Nothing to link to in a single always-on file. The link text stays — it is already the
    // section's short name — and only a bare path, which would read as a dead link, is replaced.
    var text = Regex.Replace(body, @"\[([^\]]+)\]\(references/[^)]+\)", "$1");
    foreach (var reference in references)
    {
        text = text.Replace($"references/{reference.FileName}", $"the “{reference.Title}” section", StringComparison.Ordinal);
    }

    var note =
        "<!-- Generated from skill/src/SKILL.md. The routing layer only; the phases it names are " +
        "detailed in the full skill at https://github.com/gandjustas/modulith/blob/main/skill/dist/SKILL.md -->";

    Write(Path.Combine(dist, "fusion-modules-rules.md"), $"{note}\n\n{text.Trim()}\n");
}

(string FrontMatter, string Body) ReadSkill()
{
    var text = File.ReadAllText(Path.Combine(source, "SKILL.md")).ReplaceLineEndings("\n");
    var match = Regex.Match(text, @"\A---\r?\n.*?\r?\n---\r?\n", RegexOptions.Singleline);

    return match.Success
        ? (match.Value.TrimEnd('\n'), text[match.Length..].TrimStart('\n'))
        : (string.Empty, text);
}

List<Reference> ReadReferences()
{
    var directory = Path.Combine(source, "references");
    if (!Directory.Exists(directory))
    {
        return [];
    }

    return [.. Directory.EnumerateFiles(directory, "*.md")
        .OrderBy(Path.GetFileName, StringComparer.Ordinal)
        .Select(file =>
        {
            var content = File.ReadAllText(file).ReplaceLineEndings("\n");
            var title = Regex.Match(content, @"^#\s+(.+)$", RegexOptions.Multiline).Groups[1].Value.Trim();

            return new Reference(Path.GetFileName(file), title, Slug(title), content);
        })];
}

// references/data.md -> #phase-3-data, so cross-references survive being flattened into one file.
string Linkify(string text, List<Reference> references)
{
    foreach (var reference in references)
    {
        // A link whose text is the path reads as a broken link once the path is gone, so it
        // takes the reference's own title instead.
        text = Regex.Replace(
            text,
            $@"\[references/{Regex.Escape(reference.FileName)}\]\(references/{Regex.Escape(reference.FileName)}\)",
            $"[{reference.Title}](#{reference.Anchor})");

        text = text.Replace($"](references/{reference.FileName})", $"](#{reference.Anchor})", StringComparison.Ordinal);

        // The same link seen from inside references/, where a sibling is named on its own. Missed
        // for as long as this has existed, because a bare name is a valid link in the source tree
        // and only stops resolving once the files are flattened into one.
        text = text.Replace($"]({reference.FileName})", $"](#{reference.Anchor})", StringComparison.Ordinal);
    }

    return text;
}

// Every heading down one level, so each reference's H1 sits under the skill's H1 rather than
// competing with it.
string Demote(string text) =>
    Regex.Replace(text, @"^(#{1,5})\s", "#$1 ", RegexOptions.Multiline);

string Slug(string title) =>
    Regex.Replace(title.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');

void Write(string path, string content)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);

    // Normalised line endings, so the freshness check in CI is not a line-ending argument.
    File.WriteAllText(path, content.ReplaceLineEndings("\n"), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
}

static string FindRepositoryRoot(string start)
{
    for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "FusionModules.slnx")))
        {
            return directory.FullName;
        }
    }

    throw new InvalidOperationException($"No repository root above '{start}'.");
}

internal sealed record Reference(string FileName, string Title, string Anchor, string Content);
