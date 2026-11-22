using System.Text.Json.Nodes;
using Workstream.Api.Domain;

namespace Workstream.Tests;

/// <summary>workstream.yml read strictly, and written so that reading it gives the same settings back.</summary>
[TestClass]
public sealed class WorkstreamFileTests
{
    [TestMethod]
    public void A_whole_file_is_every_setting()
    {
        var read = WorkstreamFile.Read("""
            size: large                 # small | standard | large
            setup: |
              npm ci
              npx playwright install --with-deps
            apps: [chrome, vscode, terminal]
            variables: [DATABASE_URL, STRIPE_KEY]
            instructions: |
              Run npm test before you say you are done.
            """);
        Assert.IsTrue(read.IsValid, string.Join("\n", read.Errors));
        var settings = read.Settings!;
        Assert.AreEqual("large", settings.Size);
        Assert.AreEqual("npm ci\nnpx playwright install --with-deps", settings.Setup);
        CollectionAssert.AreEqual(new[] { "chrome", "vscode", "terminal" }, settings.Apps.ToArray());
        CollectionAssert.AreEqual(new[] { "DATABASE_URL", "STRIPE_KEY" }, settings.Variables.ToArray());
        Assert.AreEqual("Run npm test before you say you are done.", settings.Instructions);
    }

    [TestMethod]
    public void What_the_file_leaves_out_is_what_a_new_environment_gets()
    {
        foreach (var text in new[] { "", "# nothing yet\n", "size: small\n" })
        {
            var settings = WorkstreamFile.Read(text).Settings!;
            CollectionAssert.AreEqual(AppCatalog.Defaults.ToArray(), settings.Apps.ToArray());
            Assert.IsNull(settings.Setup);
            Assert.AreEqual(0, settings.Variables.Count);
            Assert.IsNull(settings.Instructions);
        }
        Assert.AreEqual("standard", WorkstreamFile.Read("").Settings!.Size);
        Assert.AreEqual("small", WorkstreamFile.Read("size: small").Settings!.Size);
        // Block style lists read the same as flow style ones.
        CollectionAssert.AreEqual(new[] { "chrome", "files" }, WorkstreamFile.Read("apps:\n  - chrome\n  - files\n").Settings!.Apps.ToArray());
    }

    [TestMethod]
    public void Each_mistake_is_named_with_its_line()
    {
        var read = WorkstreamFile.Read("size: huge\napps: [chrome, photoshop, gimp]\nvariables: [GOOD, 1BAD]\nsetup: [npm ci]\nimage: debian\n");
        Assert.IsFalse(read.IsValid);
        CollectionAssert.AreEqual(new[]
        {
            "Line 1: size is small, standard or large, not huge.",
            $"Line 2: photoshop, gimp are not apps a desktop can come with. The apps are {string.Join(", ", AppCatalog.All.Select(x => x.Id))}.",
            "Line 3: 1BAD: a variable's name uses letters, digits and underscores, and does not start with a digit.",
            "Line 4: setup is text. Write it after setup: |, one line under another.",
            "Line 5: image is not a setting of workstream.yml. The settings are size, setup, apps, variables, instructions.",
        }, read.Errors.ToArray());
        StringAssert.StartsWith(WorkstreamFile.Read("apps: chrome\n").Errors.Single(), "Line 1: apps is a list, such as apps: [chrome, vscode, terminal].");
    }

    [TestMethod]
    public void YAML_that_does_not_parse_and_a_file_that_is_not_settings_are_refused()
    {
        StringAssert.StartsWith(WorkstreamFile.Read("size: [small\n").Errors.Single(), "Line ");
        StringAssert.Contains(WorkstreamFile.Read("size: small\nsize: large\n").Errors.Single(), "not valid YAML");
        Assert.AreEqual("Line 1: workstream.yml is a list of settings, such as size: standard.", WorkstreamFile.Read("- size: small\n").Errors.Single());
        Assert.AreEqual("workstream.yml holds more than one YAML document. Keep one.", WorkstreamFile.Read("size: small\n---\nsize: large\n").Errors.Single());
        StringAssert.Contains(WorkstreamFile.Read(new string('#', WorkstreamFile.MaxBytes + 1)).Errors.Single(), "larger than 64 KB");
    }

    [TestMethod]
    public void A_written_file_reads_back_as_the_same_settings()
    {
        var cases = new[]
        {
            new WorkstreamFileSettings("large", ["chrome", "terminal"], "npm ci\n\nnpm run build -- --watch=false", ["DATABASE_URL"], "Run the tests.\nThen say so."),
            new WorkstreamFileSettings("standard", AppCatalog.Defaults, null, [], null),
            new WorkstreamFileSettings("small", ["vscode"], "  indented: first line\nsecond: line # not a comment", [], "key: value"),
        };
        foreach (var settings in cases)
        {
            var text = WorkstreamFile.Write(settings);
            var read = WorkstreamFile.Read(text);
            Assert.IsTrue(read.IsValid, $"{text}\n{string.Join("\n", read.Errors)}");
            Assert.AreEqual(settings.Size, read.Settings!.Size);
            CollectionAssert.AreEqual(settings.Apps.ToArray(), read.Settings.Apps.ToArray());
            Assert.AreEqual(settings.Setup, read.Settings.Setup, text);
            CollectionAssert.AreEqual(settings.Variables.ToArray(), read.Settings.Variables.ToArray());
            Assert.AreEqual(settings.Instructions, read.Settings.Instructions, text);
        }
    }
}

/// <summary>Suggesting an environment: which files are read, what the model is asked, and reading its answer.</summary>
[TestClass]
public sealed class RepositoryAnalysisTests
{
    [TestMethod]
    public void The_files_that_say_how_a_repository_is_built_are_picked_and_its_code_and_vendored_files_are_not()
    {
        var picked = RepositoryAnalysis.Pick(new (string, long)[]
        {
            ("src/index.ts", 100), ("package.json", 400), ("pnpm-lock.yaml", 900_000), ("README.md", 2000), (".nvmrc", 3),
            (".github/workflows/ci.yml", 800), (".github/ISSUE_TEMPLATE/bug.yml", 100), (".devcontainer/devcontainer.json", 300),
            ("docker-compose.yml", 500), ("api/Api.csproj", 1000), ("api/Dockerfile", 300), ("node_modules/x/package.json", 10),
            ("packages/web/package.json", 300), ("a/b/c/package.json", 10), ("docs/README.md", 100), ("huge.json", 5_000_000), ("global.json", 50),
        });
        CollectionAssert.AreEquivalent(new[]
        {
            "package.json", ".nvmrc", "global.json", "README.md", "docker-compose.yml", "api/Api.csproj", "api/Dockerfile", "packages/web/package.json",
            ".devcontainer/devcontainer.json", ".github/workflows/ci.yml", "pnpm-lock.yaml",
        }, picked.ToArray());
        // The manifests at the root come first, the lockfiles last.
        Assert.AreEqual(".nvmrc", picked[0]);
        Assert.AreEqual("pnpm-lock.yaml", picked[^1]);
    }

    [TestMethod]
    public void A_lockfile_is_sent_for_its_first_lines_and_a_long_file_is_cut()
    {
        var lockfile = RepositoryAnalysis.Cut("pnpm-lock.yaml", new string('x', 10_000));
        Assert.IsTrue(lockfile.Cut);
        Assert.AreEqual(1500, lockfile.Text.Length);
        Assert.IsFalse(RepositoryAnalysis.Cut("package.json", "{}").Cut);
        Assert.AreEqual(12 * 1024, RepositoryAnalysis.Cut("README.md", new string('x', 50_000)).Text.Length);
    }

    [TestMethod]
    public void The_model_is_asked_with_the_files_the_apps_and_the_JSON_it_must_answer()
    {
        var request = RepositoryAnalysis.Request("claude-sonnet-5-5", "acme/app", "main", ["package.json", "src/index.ts"], false,
            [new AnalysisFile("package.json", "{\"name\":\"app\"}", false)]);
        Assert.AreEqual("claude-sonnet-5-5", request["model"]!.GetValue<string>());
        Assert.IsNull(request["tool_choice"]);
        var prompt = request["messages"]![0]!["content"]!.GetValue<string>();
        StringAssert.Contains(prompt, "Repository: acme/app, branch main.");
        StringAssert.Contains(prompt, "src/index.ts\n");
        StringAssert.Contains(prompt, "<file path=\"package.json\">\n{\"name\":\"app\"}\n</file>");
        var system = request["system"]!.GetValue<string>();
        foreach (var app in AppCatalog.All) StringAssert.Contains(system, $"{app.Id}: {app.Name}");
        var schema = request["output_config"]!["format"]!["schema"]!;
        Assert.IsFalse(schema["additionalProperties"]!.GetValue<bool>());
        CollectionAssert.AreEqual(new[] { "small", "standard", "large" }, schema["properties"]!["size"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray());
    }

    static string Answer(JsonObject input) => new JsonObject
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = input.ToJsonString() }),
    }.ToJsonString();

    [TestMethod]
    public void The_answer_becomes_settings_a_desktop_can_have()
    {
        var suggestion = RepositoryAnalysis.Read(Answer(new JsonObject
        {
            ["setup_script"] = "  pip install -r requirements.txt  ", ["size"] = "large", ["apps"] = new JsonArray("chrome", "terminal", "chrome", "excel"),
            ["variables"] = new JsonArray("DATABASE_URL", "bad name", "DATABASE_URL"), ["instructions"] = "", ["why"] = "A Django app.",
        }))!;
        Assert.AreEqual("pip install -r requirements.txt", suggestion.SetupScript);
        Assert.AreEqual("large", suggestion.Size);
        CollectionAssert.AreEqual(new[] { "chrome", "terminal" }, suggestion.Apps.ToArray());
        CollectionAssert.AreEqual(new[] { "DATABASE_URL" }, suggestion.Variables.ToArray());
        Assert.IsNull(suggestion.Instructions);
        Assert.AreEqual("A Django app.", suggestion.Why);

        // Nothing usable for the apps is the usual apps; a size it does not come in is the standard one.
        var empty = RepositoryAnalysis.Read(Answer(new JsonObject { ["size"] = "xl", ["apps"] = new JsonArray() }))!;
        Assert.AreEqual("standard", empty.Size);
        CollectionAssert.AreEqual(AppCatalog.Defaults.ToArray(), empty.Apps.ToArray());
        Assert.IsNull(empty.SetupScript);
    }

    [TestMethod]
    public void An_answer_that_is_not_the_JSON_is_none()
    {
        Assert.IsNull(RepositoryAnalysis.Read("""{"content":[{"type":"text","text":"I think npm ci."}]}"""));
        Assert.IsNull(RepositoryAnalysis.Read("not json"));
        // Cut off at max_tokens: the JSON does not close.
        Assert.IsNull(RepositoryAnalysis.Read("""{"content":[{"type":"text","text":"{\"size\":\"large\",\"setup_script\":\"npm"}]}"""));
    }
}
