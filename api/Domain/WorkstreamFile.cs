using System.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Workstream.Api.Domain;

/// <summary>
/// What a repository's <c>workstream.yml</c> says a desktop for it comes with. A setting the file leaves out is
/// what a new environment gets: the standard size, the usual apps, no setup script, no variables, no
/// instructions. Variables are names only; their values are set in Workstream and never written to the file.
/// </summary>
public sealed record WorkstreamFileSettings(string Size, IReadOnlyList<string> Apps, string? Setup, IReadOnlyList<string> Variables, string? Instructions);

/// <summary>The file read: its settings when it is valid, or what is wrong with it, a line each.</summary>
public sealed record WorkstreamFileRead(WorkstreamFileSettings? Settings, IReadOnlyList<string> Errors)
{
    public bool IsValid => Settings is not null;
}

/// <summary>
/// <c>workstream.yml</c> at a repository's root: an environment written down beside the code it is for
/// (docs/environments). It is read strictly, so a mistake is named rather than half-used: a setting the file
/// does not know, a size or an app Workstream does not have, a variable's name a shell cannot read, or YAML
/// that does not parse are each an error with its line.
/// </summary>
public static class WorkstreamFile
{
    public const string Path = "workstream.yml";
    /// <summary>A file larger than this is not read.</summary>
    public const int MaxBytes = 64 * 1024;

    static readonly string[] Keys = ["size", "setup", "apps", "variables", "instructions"];

    public static WorkstreamFileRead Read(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes) return Failed($"{Path} is larger than {MaxBytes / 1024} KB.");
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException error)
        {
            return Failed($"Line {error.Start.Line}: this is not valid YAML. {Plain(error)}");
        }
        if (stream.Documents.Count == 0) return new WorkstreamFileRead(Defaults(), []);
        if (stream.Documents.Count > 1) return Failed($"{Path} holds more than one YAML document. Keep one.");
        var root = stream.Documents[0].RootNode;
        if (root is YamlScalarNode { Value: null or "" or "~" or "null" }) return new WorkstreamFileRead(Defaults(), []);
        if (root is not YamlMappingNode settings) return Failed($"Line {root.Start.Line}: {Path} is a list of settings, such as size: standard.");

        var errors = new List<string>();
        var size = DesktopSizes.Default;
        IReadOnlyList<string> apps = AppCatalog.Defaults;
        string? setup = null;
        string? instructions = null;
        IReadOnlyList<string> variables = [];
        foreach (var (keyNode, value) in settings.Children)
        {
            var key = (keyNode as YamlScalarNode)?.Value ?? "";
            var line = keyNode.Start.Line;
            switch (key)
            {
                case "size":
                    if (Scalar(value) is { } named && DesktopSizes.Has(named)) size = named;
                    else errors.Add($"Line {line}: size is {string.Join(", ", DesktopSizes.Keys.SkipLast(1))} or {DesktopSizes.Keys[^1]}{(Scalar(value) is { Length: > 0 } wrong ? $", not {wrong}" : "")}.");
                    break;
                case "setup":
                    setup = Text(value, "setup", WorkEnvironment.MaxScriptLength, line, errors);
                    break;
                case "instructions":
                    instructions = Text(value, "instructions", WorkEnvironment.MaxInstructionsLength, line, errors);
                    break;
                case "apps":
                    if (List(value, "apps", line, errors) is { } listed)
                    {
                        var unknown = listed.Where(app => !AppCatalog.Has(app)).ToList();
                        if (unknown.Count > 0)
                        {
                            errors.Add($"Line {line}: {string.Join(", ", unknown)} {(unknown.Count == 1 ? "is not an app" : "are not apps")} a desktop can come with. The apps are {string.Join(", ", AppCatalog.All.Select(x => x.Id))}.");
                        }
                        else
                        {
                            apps = [.. listed.Distinct(StringComparer.Ordinal)];
                        }
                    }
                    break;
                case "variables":
                    if (List(value, "variables", line, errors) is { } names)
                    {
                        var bad = names.Where(name => !IsVariableName(name)).ToList();
                        if (bad.Count > 0) errors.Add($"Line {line}: {string.Join(", ", bad)}: a variable's name uses letters, digits and underscores, and does not start with a digit.");
                        else if (names.Count > WorkEnvironment.MaxVariables) errors.Add($"Line {line}: an environment holds at most {WorkEnvironment.MaxVariables} variables.");
                        else variables = [.. names.Distinct(StringComparer.Ordinal)];
                    }
                    break;
                default:
                    errors.Add($"Line {line}: {(key.Length > 0 ? key : "this")} is not a setting of {Path}. The settings are {string.Join(", ", Keys)}.");
                    break;
            }
        }
        return errors.Count > 0 ? new WorkstreamFileRead(null, errors) : new WorkstreamFileRead(new WorkstreamFileSettings(size, apps, setup, variables, instructions), []);
    }

    /// <summary>
    /// The file for an environment's settings, written so that <see cref="Read"/> gives the same settings back.
    /// Variables are their names only.
    /// </summary>
    public static string Write(WorkstreamFileSettings settings)
    {
        var text = new StringBuilder();
        text.Append("# What a Workstream desktop for this repository comes with (Workstream's docs, Environments).\n");
        text.Append("size: ").Append(settings.Size).Append('\n');
        text.Append("apps: [").Append(string.Join(", ", settings.Apps)).Append("]\n");
        if (settings.Variables.Count > 0)
        {
            text.Append("# Names only: their values are set in Workstream.\n");
            text.Append("variables: [").Append(string.Join(", ", settings.Variables)).Append("]\n");
        }
        Block(text, "setup", settings.Setup);
        Block(text, "instructions", settings.Instructions);
        return text.ToString();
    }

    static void Block(StringBuilder text, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        // A literal block keeps every line as it is; "|-" drops the last line break, which the form never has.
        var lines = value.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        // A first line that starts with a space needs the indentation said.
        text.Append(key).Append(lines[0].Length > 0 && char.IsWhiteSpace(lines[0][0]) ? ": |2-\n" : ": |-\n");
        foreach (var line in lines) text.Append(line.Length == 0 ? "\n" : $"  {line}\n");
    }

    static WorkstreamFileSettings Defaults() => new(DesktopSizes.Default, AppCatalog.Defaults, null, [], null);

    static WorkstreamFileRead Failed(string error) => new(null, [error]);

    static string? Scalar(YamlNode node) => node is YamlScalarNode scalar ? scalar.Value?.Trim() : null;

    static string? Text(YamlNode node, string key, int max, long line, List<string> errors)
    {
        if (node is not YamlScalarNode scalar)
        {
            errors.Add($"Line {line}: {key} is text. Write it after {key}: |, one line under another.");
            return null;
        }
        var value = scalar.Value?.Trim();
        if (string.IsNullOrEmpty(value) || value is "~" or "null") return null;
        if (value.Length > max)
        {
            errors.Add($"Line {line}: {key} can be at most {max} characters.");
            return null;
        }
        return scalar.Value!.TrimEnd();
    }

    static List<string>? List(YamlNode node, string key, long line, List<string> errors)
    {
        if (node is YamlScalarNode { Value: null or "" or "~" or "null" }) return [];
        if (node is not YamlSequenceNode sequence || sequence.Children.Any(x => x is not YamlScalarNode { Value.Length: > 0 }))
        {
            errors.Add($"Line {line}: {key} is a list, such as {key}: [{(key == "apps" ? "chrome, vscode, terminal" : "DATABASE_URL")}].");
            return null;
        }
        return [.. sequence.Children.Select(x => ((YamlScalarNode)x).Value!.Trim())];
    }

    static bool IsVariableName(string name)
    {
        try
        {
            Rules.VariableName(name);
            return true;
        }
        catch (DomainRuleException)
        {
            return false;
        }
    }

    /// <summary>YamlDotNet's message without its own position, which the line already says.</summary>
    static string Plain(YamlException error)
    {
        var message = error.InnerException?.Message ?? error.Message;
        var at = message.IndexOf("): ", StringComparison.Ordinal);
        return (at >= 0 && message.StartsWith("(Line", StringComparison.Ordinal) ? message[(at + 3)..] : message).Trim();
    }
}
