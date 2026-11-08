// Compiled into both projects, like ProbeModel.cs: the probe masks the connection strings it reads
// off DbContextOptions, the tool masks configuration, environment and free text, and the two agree.

using System.Text.RegularExpressions;

namespace FusionModules.Tool.Probe;

/// <summary>
/// What the inventory never writes out. One rule decides what a secret is — a name whose last
/// segment ends in password, secret, token, key and the like (<see cref="IsSecretName"/>) — and it
/// applies to configuration keys, environment variables, the keys of a connection string's pairs,
/// a URI's query parameters and a command line's flags alike.
/// </summary>
internal static class Secrets
{
    public const string Masked = "***";

    /// <summary>Free text is searched for a known secret this long or longer; anything shorter could be any word.</summary>
    public const int KnownValueMinimumLength = 6;

    // scheme://user:password@host — amqp, mongodb, redis and http URIs carry credentials so.
    private static readonly Regex UserInfo = new(@"(?i)\b([a-z][a-z0-9+.\-]*://[^:/@\s;]*):(?<password>[^@/\s;]+)@", RegexOptions.Compiled);

    // A URI's query parameter: ?sv=…&sig=… is a SAS token.
    private static readonly Regex QueryParameter = new(@"(?<=[?&])(?<name>[A-Za-z0-9_.\-]+)=(?<value>[^&#\s;,'""]*)", RegexOptions.Compiled);

    // NAME=value inside free text: an error message quoting a connection string, -e PASSWORD=… or
    // --password=… on a command line. Unlike a connection string's pair, the value ends at a space.
    private static readonly Regex Pair = new(@"(?<![A-Za-z0-9_])(?<name>[A-Za-z][A-Za-z0-9_.\-]*)\s*=\s*(?<value>""[^""]*""|'[^']*'|[^\s;,&""']+)", RegexOptions.Compiled);

    // --flag value on a command line; a value that starts with '-' is the next flag, not a value.
    private static readonly Regex Flag = new(@"(?<![A-Za-z0-9_\-])(?<flag>--?[A-Za-z][A-Za-z0-9_\-]*)[ \t]+(?<value>""[^""]*""|'[^']*'|[^\s""'\-][^\s""']*)", RegexOptions.Compiled);

    private static readonly Regex PairName = new(@"^[A-Za-z][A-Za-z0-9 _.\-]*$", RegexOptions.Compiled);

    private static readonly string[] SecretSuffixes = ["password", "passwd", "pwd", "secret", "token", "key", "credential", "credentials", "passphrase", "signature"];

    // Flags that take a secret without saying so: redis-server --requirepass, --masterauth.
    private static readonly HashSet<string> SecretFlags = new(StringComparer.OrdinalIgnoreCase) { "requirepass", "masterauth", "pass", "auth" };

    /// <summary>
    /// Whether a configuration key, environment variable, connection string key or flag names a
    /// secret: its last segment ends with password, pwd, secret, token, key, signature and the like
    /// (ClientSecret, ApiKey, Jwt:SigningKey, RABBITMQ_PASSWORD, SaslPassword, SharedAccessSignature).
    /// Array indices are skipped: Jwt:Keys:0 is judged by Keys.
    /// </summary>
    public static bool IsSecretName(string name)
    {
        var segments = name.Split([":", "__"], StringSplitOptions.RemoveEmptyEntries);
        var last = segments.LastOrDefault(s => !s.All(char.IsDigit)) ?? "";
        var compact = new string(last.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (compact.EndsWith('s') && compact.Length >= 4 && SecretSuffixes.Any(compact[..^1].EndsWith))
        {
            compact = compact[..^1];
        }

        return SecretSuffixes.Any(compact.EndsWith);
    }

    /// <summary>A configuration value as the inventory writes it: masked whole under a secret's name, else its secrets masked.</summary>
    public static string? Mask(string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return IsSecretValue(name, value) ? Masked : MaskConnectionString(value);
    }

    /// <summary>
    /// A connection string or URI with its secrets masked and the rest kept: the value of every
    /// pair whose key is a secret's name (Password=, Key=, ApiKey=, Token=, SaslPassword=,
    /// SharedAccessSignature=), a URI's user:password@ and a secret query parameter (sig=).
    /// </summary>
    public static string? MaskConnectionString(string? value) => value is null ? null : MaskConnectionString(value, null);

    /// <summary>
    /// Free text — a command line, an error message, a process's output — with its secrets masked:
    /// every occurrence of a value known to be a secret (<see cref="CollectSecrets"/>), NAME=value and
    /// --flag value under a secret's name, and URI credentials.
    /// </summary>
    public static string? MaskText(string? text, IReadOnlyCollection<string>? knownSecrets = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        // The known values first: a secret quoted whole goes, spaces and all, before the pair rule
        // would cut it at the first space.
        foreach (var secret in (knownSecrets ?? []).OrderByDescending(s => s.Length))
        {
            text = text.Replace(secret, Masked, StringComparison.Ordinal);
        }

        text = Pair.Replace(text, m => IsSecretValue(m.Groups["name"].Value, m.Groups["value"].Value)
            ? m.Value[..(m.Groups["value"].Index - m.Index)] + Masked
            : m.Value);
        text = Flag.Replace(text, m => IsSecretFlag(m.Groups["flag"].Value)
            ? m.Value[..(m.Groups["value"].Index - m.Index)] + Masked
            : m.Value);
        return MaskUris(text, null);
    }

    /// <summary>
    /// Adds the secrets a configuration value holds to <paramref name="into"/>: the whole value under
    /// a secret's name, else what <see cref="MaskConnectionString(string?)"/> would mask in it. What
    /// free text is then searched for (<see cref="MaskText"/>).
    /// </summary>
    public static void CollectSecrets(string name, string? value, ISet<string> into)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var found = new List<string>();
        if (IsSecretValue(name, value))
        {
            found.Add(value);
        }
        else
        {
            MaskConnectionString(value, found);
        }

        foreach (var secret in found.Select(s => s.Trim().Trim('"', '\'')))
        {
            if (secret.Length >= KnownValueMinimumLength && secret != Masked && !bool.TryParse(secret, out _))
            {
                into.Add(secret);
            }
        }
    }

    // A switch is not a secret: RequireUniqueEmail, ValidateIssuerSigningKey = true.
    private static bool IsSecretValue(string name, string value) => IsSecretName(name) && !bool.TryParse(value.Trim(), out _);

    private static bool IsSecretFlag(string flag)
    {
        var name = flag.TrimStart('-');
        return SecretFlags.Contains(name) || IsSecretName(name);
    }

    private static string MaskConnectionString(string value, List<string>? found)
    {
        if (value.Length == 0)
        {
            return value;
        }

        // Pairs are separated by ';' — or by ',' in a StackExchange.Redis string, which has no ';'.
        // A pair's value runs to the separator, spaces and all: Password=two words;.
        var separator = value.Contains(';') ? ';' : ',';
        var segments = value.Split(separator);
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            var equals = segment.IndexOf('=');
            if (equals > 0
                && PairName.IsMatch(segment[..equals].Trim())
                && segment[(equals + 1)..] is { Length: > 0 } secret
                && IsSecretValue(segment[..equals].Trim(), secret))
            {
                found?.Add(secret);
                segments[i] = segment[..(equals + 1)] + Masked;
            }
        }

        return MaskUris(string.Join(separator, segments), found);
    }

    private static string MaskUris(string text, List<string>? found)
    {
        text = QueryParameter.Replace(text, m =>
        {
            var name = m.Groups["name"].Value;
            var value = m.Groups["value"].Value;
            if (value.Length == 0 || value == Masked || !(name.Equals("sig", StringComparison.OrdinalIgnoreCase) || IsSecretValue(name, value)))
            {
                return m.Value;
            }

            found?.Add(value);
            return name + "=" + Masked;
        });

        return UserInfo.Replace(text, m =>
        {
            found?.Add(m.Groups["password"].Value);
            return m.Groups[1].Value + ":" + Masked + "@";
        });
    }
}
