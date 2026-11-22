using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Workstream.Api.Domain;

/// <summary>A file of the repository the model is shown: its path and as much of its text as is sent.</summary>
public sealed record AnalysisFile(string Path, string Text, bool Cut);

/// <summary>
/// What Workstream suggests for a repository's environment. The person sees it in the form and changes what
/// they want before saving. <see cref="Why"/> is one line on what it was based on.
/// </summary>
public sealed record EnvironmentSuggestion(string Size, IReadOnlyList<string> Apps, string? SetupScript, IReadOnlyList<string> Variables, string? Instructions, string Why);

/// <summary>
/// Suggesting an environment from a repository: which of its files say how it is built, what the model is
/// asked, and reading what it answers into settings a desktop can have. The reading of GitHub and the call
/// to the model are the operation's (Operations/GitHub.cs); this is the part that needs neither.
/// </summary>
public static partial class RepositoryAnalysis
{
    /// <summary>The tool the model answers with, so its answer is fields and not prose.</summary>
    public const string ToolName = "suggest_environment";
    const int MaxFiles = 30;
    const int MaxFileBytes = 12 * 1024;
    const int MaxLockfileBytes = 1500;
    const int MaxTotalBytes = 160 * 1024;
    const int MaxTreeLines = 400;
    const int MaxWhy = 300;

    static readonly HashSet<string> Manifests = new(StringComparer.OrdinalIgnoreCase)
    {
        "package.json", "pnpm-workspace.yaml", "turbo.json", "nx.json", "lerna.json", "deno.json", "deno.jsonc", "bunfig.toml",
        "requirements.txt", "requirements-dev.txt", "pyproject.toml", "Pipfile", "setup.py", "setup.cfg", "environment.yml",
        "go.mod", "Cargo.toml", "Gemfile", "composer.json", "pom.xml", "build.gradle", "build.gradle.kts", "settings.gradle",
        "settings.gradle.kts", "global.json", "Directory.Build.props", "Directory.Packages.props", "mix.exs", "pubspec.yaml",
        "Package.swift", "CMakeLists.txt", "Makefile", "justfile", "Justfile", "Taskfile.yml", "Procfile", "app.json",
        ".tool-versions", ".nvmrc", ".node-version", ".python-version", ".ruby-version", ".java-version", "rust-toolchain.toml",
        "rust-toolchain", "devcontainer.json", ".env.example", ".env.sample", ".env.template", "AGENTS.md", "CLAUDE.md", "CONTRIBUTING.md",
    };

    static readonly HashSet<string> Lockfiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "package-lock.json", "yarn.lock", "pnpm-lock.yaml", "bun.lock", "poetry.lock", "uv.lock", "Pipfile.lock", "Cargo.lock",
        "Gemfile.lock", "composer.lock", "packages.lock.json", "mix.lock", "pubspec.lock",
    };

    static readonly string[] Skipped = ["node_modules/", "vendor/", "dist/", "build/", ".git/", "third_party/", "Pods/", ".venv/", "target/"];

    /// <summary>
    /// The files worth reading, most telling first: at the root, then a level or two down; the CI workflows;
    /// a dev container. Lockfiles are read only for their first lines, which say the tool and its version.
    /// </summary>
    public static IReadOnlyList<string> Pick(IEnumerable<(string Path, long Size)> tree)
    {
        var candidates = new List<(string Path, int Rank)>();
        foreach (var (path, size) in tree)
        {
            if (Skipped.Any(skip => path.StartsWith(skip, StringComparison.Ordinal) || path.Contains("/" + skip, StringComparison.Ordinal))) continue;
            var name = path[(path.LastIndexOf('/') + 1)..];
            var depth = path.Count(c => c == '/');
            int? rank =
                path.StartsWith(".github/workflows/", StringComparison.Ordinal) && depth == 2 && (name.EndsWith(".yml") || name.EndsWith(".yaml")) ? 3
                : path.StartsWith(".devcontainer/", StringComparison.Ordinal) && depth <= 2 && (name.EndsWith(".json") || name.StartsWith("Dockerfile")) ? 2
                : depth > 2 ? null
                : depth == 0 && name.StartsWith("README", StringComparison.OrdinalIgnoreCase) ? 1
                : Manifests.Contains(name) || name.EndsWith(".csproj") || name.EndsWith(".sln") || name.EndsWith(".slnx") || name.EndsWith(".fsproj") ? 0
                : Lockfiles.Contains(name) ? 4
                : name.StartsWith("Dockerfile", StringComparison.Ordinal) || name.EndsWith(".dockerfile", StringComparison.OrdinalIgnoreCase) ? 1
                : ComposePattern().IsMatch(name) ? 1
                : null;
            if (rank is null || size > 1024 * 1024 && rank != 4) continue;
            candidates.Add((path, rank.Value * 10 + depth));
        }
        return [.. candidates.OrderBy(x => x.Rank).ThenBy(x => x.Path, StringComparer.Ordinal).Take(MaxFiles).Select(x => x.Path)];
    }

    /// <summary>A file's text as it is sent: a lockfile's first lines, anything else up to its cap.</summary>
    public static AnalysisFile Cut(string path, string text)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var cap = Lockfiles.Contains(name) ? MaxLockfileBytes : MaxFileBytes;
        return text.Length <= cap ? new AnalysisFile(path, text, false) : new AnalysisFile(path, text[..cap], true);
    }

    /// <summary>
    /// The request to the model, in Anthropic's Messages format: what a desktop is, the apps and sizes it can
    /// have, the repository's files, and the one tool it must answer with.
    /// </summary>
    public static JsonObject Request(string model, string repository, string branch, IReadOnlyList<string> tree, bool treeCut, IReadOnlyList<AnalysisFile> files)
    {
        var prompt = new StringBuilder();
        prompt.Append($"Repository: {repository}, branch {branch}.\n\n");
        prompt.Append("Its files").Append(treeCut || tree.Count > MaxTreeLines ? " (not all of them)" : "").Append(":\n");
        foreach (var path in tree.Take(MaxTreeLines)) prompt.Append(path).Append('\n');
        var total = 0;
        foreach (var file in files)
        {
            if (total + file.Text.Length > MaxTotalBytes) break;
            total += file.Text.Length;
            prompt.Append($"\n<file path=\"{file.Path}\"{(file.Cut ? " cut=\"true\"" : "")}>\n{file.Text}\n</file>\n");
        }
        prompt.Append($"\nSuggest the environment for this repository with the {ToolName} tool.");

        var system = $"""
            You set up Workstream environments. An environment is what a cloud desktop comes with when someone starts a task on this repository: an agent then works on the task on that desktop.

            The desktop is Debian Linux with a graphical desktop. It already has git, gh, curl, jq, build-essential, Python 3 with pip and venv, Node.js 22 with npm, the .NET 10 SDK, a JDK 17, and Docker Engine with Compose. The user has passwordless sudo, so apt-get works. The repository is cloned before the setup script runs, and the script runs once, from the repository's root, as that user, with bash. Its output is shown to the person.

            The setup script installs what the project needs and gets it ready to run its tests: the language versions the repository asks for (from .nvmrc, .tool-versions, global.json and the like) when they differ from what is installed, the dependencies (npm ci, pnpm install --frozen-lockfile, pip install -r, dotnet restore, and so on, matching the lockfiles), and anything its README or CI workflow says to do first. Use corepack for pnpm and yarn. Keep it short and safe to run once. Do not start long-running servers, do not run the whole test suite, do not deploy, and do not use secrets beyond the variables it reads. Leave it empty when nothing needs installing.

            Sizes: small (2 cores, 8 GB memory) for light work; standard (4 cores, 16 GB memory) for most projects; large (8 cores, 32 GB memory, 128 GB disk) for big builds, several containers at once, or the Android emulator.

            Apps (by id): {string.Join("; ", AppCatalog.All.Select(app => $"{app.Id}: {app.Name}, {app.Description}"))}. Pick the ones the work needs; chrome, vscode and terminal suit most code.

            Variables are names only: the environment variables the project needs set to build, test or run, such as those in .env.example or read in CI. Never guess values and leave out ones CI provides by itself, such as GITHUB_TOKEN.

            Instructions are a few short lines for the agent about this repository that are not obvious from it: how to run the tests, what to start first. Leave them empty when the README already says it plainly.

            "why" is one plain sentence on what the suggestion is based on, such as "A Next.js app with pnpm and Postgres in Docker Compose."
            """;

        var sizes = new JsonArray([.. DesktopSizes.Keys.Select(x => JsonValue.Create(x))]);
        var apps = new JsonArray([.. AppCatalog.All.Select(x => JsonValue.Create(x.Id))]);
        return new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = 4096,
            ["system"] = system,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt.ToString() }),
            ["tools"] = new JsonArray(new JsonObject
            {
                ["name"] = ToolName,
                ["description"] = "The environment for the repository.",
                ["input_schema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["setup_script"] = new JsonObject { ["type"] = "string", ["description"] = "The bash setup script, or empty." },
                        ["size"] = new JsonObject { ["type"] = "string", ["enum"] = sizes },
                        ["apps"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string", ["enum"] = apps } },
                        ["variables"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                        ["instructions"] = new JsonObject { ["type"] = "string" },
                        ["why"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("setup_script", "size", "apps", "variables", "instructions", "why"),
                },
            }),
            ["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = ToolName },
        };
    }

    /// <summary>
    /// The model's answer as settings a desktop can have: an unknown size becomes the standard one, apps the
    /// catalog does not have and names a shell cannot read are dropped, and text is cut to what an environment
    /// holds. Null when the answer has no use of the tool.
    /// </summary>
    public static EnvironmentSuggestion? Read(string answer)
    {
        JsonNode? input;
        try
        {
            var message = JsonNode.Parse(answer);
            input = message?["content"]?.AsArray().FirstOrDefault(x => x?["type"]?.GetValue<string>() == "tool_use" && x["name"]?.GetValue<string>() == ToolName)?["input"];
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return null;
        }
        if (input is not JsonObject fields) return null;
        string? Text(string name) => fields[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
        IEnumerable<string> List(string name) => fields[name] is JsonArray array
            ? array.OfType<JsonValue>().Select(x => x.TryGetValue<string>(out var text) ? text.Trim() : null).OfType<string>()
            : [];

        var size = Text("size") is { } named && DesktopSizes.Has(named) ? named : DesktopSizes.Default;
        var apps = List("apps").Where(AppCatalog.Has).Distinct(StringComparer.Ordinal).ToList();
        var variables = List("variables").Where(name => VariablePattern().IsMatch(name) && name.Length <= 100).Distinct(StringComparer.Ordinal).Take(WorkEnvironment.MaxVariables).ToList();
        var setup = Text("setup_script");
        var instructions = Text("instructions");
        var why = Text("why") ?? "";
        return new EnvironmentSuggestion(
            size,
            apps.Count > 0 ? apps : AppCatalog.Defaults,
            setup is null ? null : setup[..Math.Min(setup.Length, WorkEnvironment.MaxScriptLength)],
            variables,
            instructions is null ? null : instructions[..Math.Min(instructions.Length, WorkEnvironment.MaxInstructionsLength)],
            why.Length <= MaxWhy ? why : why[..MaxWhy]);
    }

    [GeneratedRegex(@"^(docker-)?compose(\.[\w-]+)?\.ya?ml$", RegexOptions.IgnoreCase)]
    private static partial Regex ComposePattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex VariablePattern();
}
