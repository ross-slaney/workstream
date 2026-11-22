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
