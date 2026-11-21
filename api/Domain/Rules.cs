using System.Text.RegularExpressions;

namespace Workstream.Api.Domain;

/// <summary>A rule of the domain was broken. The message is what the person is told (400).</summary>
public sealed class DomainRuleException(string message) : Exception(message);

/// <summary>The small checks every aggregate shares. Each throws <see cref="DomainRuleException"/>.</summary>
internal static partial class Rules
{
    /// <summary>
    /// An id the client made: a UUID in its canonical form. That keeps every resource id short
    /// (SqlOS reads at most 128 characters) and free of separators.
    /// </summary>
    public static string ClientId(string? id, string what)
    {
        if (id is null || !Guid.TryParseExact(id, "D", out _))
        {
            throw new DomainRuleException($"{what} needs an id made by the client: a UUID.");
        }
        return id;
    }

    /// <summary>Required text, trimmed, at most <paramref name="max"/> characters.</summary>
    public static string Text(string? value, string what, int max)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) throw new DomainRuleException($"{what} is required.");
        if (text.Length > max) throw new DomainRuleException($"{what} can be at most {max} characters.");
        return text;
    }

    /// <summary>Optional text: blank becomes null.</summary>
    public static string? OptionalText(string? value, string what, int max)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Length > max) throw new DomainRuleException($"{what} can be at most {max} characters.");
        return text;
    }

    /// <summary>A principal id: whoever did something.</summary>
    public static string Principal(string? id, string what)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 450) throw new DomainRuleException($"{what} is not a principal.");
        return id;
    }

    /// <summary>A variable's name as a shell reads it: letters, digits and underscores, not starting with a digit.</summary>
    public static string VariableName(string? name)
    {
        if (name is null || name.Length > 100 || !VariableNamePattern().IsMatch(name))
        {
            throw new DomainRuleException("A variable's name uses letters, digits and underscores, and does not start with a digit.");
        }
        return name;
    }

    /// <summary>A repository to clone: an https address. Blank becomes null.</summary>
    public static string? RepositoryUrl(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Length > 500 || !Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new DomainRuleException("A repository is an https address, such as https://github.com/acme/app. Put a token in a variable, not in the address.");
        }
        return text;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex VariableNamePattern();
}
