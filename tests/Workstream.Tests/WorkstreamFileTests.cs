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
            surfaces: [chrome, android]
            apps: [vscode, terminal]
            variables: [DATABASE_URL, STRIPE_KEY]
            instructions: |
              Run npm test before you say you are done.
            """);
        Assert.IsTrue(read.IsValid, string.Join("\n", read.Errors));
        var settings = read.Settings!;
        Assert.AreEqual("large", settings.Size);
        Assert.AreEqual("npm ci\nnpx playwright install --with-deps", settings.Setup);
        // One list, as an environment keeps it: the surfaces, then the applications.
        CollectionAssert.AreEqual(new[] { "chrome", "android", "vscode", "terminal" }, settings.Apps.ToArray());
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
        CollectionAssert.AreEqual(new[] { "android", "files" }, WorkstreamFile.Read("surfaces:\n  - android\napps:\n  - files\n").Settings!.Apps.ToArray());
    }

    static string[] Apps(string text)
    {
        var read = WorkstreamFile.Read(text);
        Assert.IsTrue(read.IsValid, $"{text}\n{string.Join("\n", read.Errors)}");
        return [.. read.Settings!.Apps];
    }

    [TestMethod]
    public void A_list_the_file_leaves_out_is_the_usual_one_and_an_empty_list_is_none()
    {
        var usualSurfaces = AppCatalog.DefaultSurfaces.ToArray();
        var usualApps = AppCatalog.DefaultApplications.ToArray();
        CollectionAssert.AreEqual(new[] { "chrome" }, usualSurfaces);
        // Only surfaces: the usual applications come with them.
        CollectionAssert.AreEqual(new[] { "android", "iphone" }.Concat(usualApps).ToArray(), Apps("surfaces: [android, iphone]\n"));
        // Only apps: the usual surfaces come with them.
        CollectionAssert.AreEqual(usualSurfaces.Append("terminal").ToArray(), Apps("apps: [terminal]\n"));
        // An empty list is none of them, which a project with nothing to look at wants.
        CollectionAssert.AreEqual(usualApps, Apps("surfaces: []\n"));
        CollectionAssert.AreEqual(usualSurfaces, Apps("apps: []\n"));
        CollectionAssert.AreEqual(Array.Empty<string>(), Apps("surfaces: []\napps: []\n"));
        // Each is listed once, however often the file names it.
        CollectionAssert.AreEqual(new[] { "chrome", "vscode" }, Apps("surfaces: [chrome, chrome]\napps: [vscode, vscode]\n"));
    }

    [TestMethod]
    public void A_file_written_before_surfaces_had_a_list_of_their_own_reads_as_it_did()
    {
        // Its surfaces are under apps, where they still count as surfaces.
        CollectionAssert.AreEqual(new[] { "chrome", "vscode", "terminal" }, Apps("apps: [chrome, vscode, terminal]\n"));
        CollectionAssert.AreEqual(new[] { "chrome", "files" }, Apps("apps:\n  - chrome\n  - files\n"));
        // The same desktop, with its surfaces first.
        CollectionAssert.AreEqual(new[] { "chrome", "android", "iphone", "vscode", "terminal" }, Apps("size: large\napps: [chrome, vscode, terminal, android, iphone]\n"));
        // A surface under apps is added to the ones under surfaces.
        CollectionAssert.AreEqual(new[] { "chrome", "android", "vscode" }, Apps("surfaces: [chrome]\napps: [android, vscode]\n"));
    }

    [TestMethod]
    public void An_app_under_surfaces_and_a_surface_nobody_has_are_refused()
    {
        Assert.AreEqual("Line 1: vscode is an app, not a surface. List it under apps.", WorkstreamFile.Read("surfaces: [chrome, vscode]\n").Errors.Single());
        Assert.AreEqual("Line 2: vscode, terminal are apps, not surfaces. List them under apps.", WorkstreamFile.Read("size: small\nsurfaces: [vscode, terminal]\n").Errors.Single());
        Assert.AreEqual("Line 1: watch is not a surface a desktop can come with. The surfaces are chrome, android, iphone.", WorkstreamFile.Read("surfaces: [watch]\n").Errors.Single());
        // Both mistakes in one list are both said.
        CollectionAssert.AreEqual(new[]
        {
            "Line 1: watch, tv are not surfaces a desktop can come with. The surfaces are chrome, android, iphone.",
            "Line 1: files is an app, not a surface. List it under apps.",
        }, WorkstreamFile.Read("surfaces: [watch, files, tv]\n").Errors.ToArray());
        StringAssert.StartsWith(WorkstreamFile.Read("surfaces: chrome\n").Errors.Single(), "Line 1: surfaces is a list, such as surfaces: [chrome, android].");
    }

    [TestMethod]
    public void Each_mistake_is_named_with_its_line()
    {
        var read = WorkstreamFile.Read("size: huge\napps: [chrome, photoshop, gimp]\nvariables: [GOOD, 1BAD]\nsetup: [npm ci]\nimage: debian\n");
        Assert.IsFalse(read.IsValid);
        CollectionAssert.AreEqual(new[]
        {
            "Line 1: size is small, standard or large, not huge.",
            "Line 2: photoshop, gimp are not apps a desktop can come with. The apps are vscode, terminal, files, writer, calc, impress.",
            "Line 3: 1BAD: a variable's name uses letters, digits and underscores, and does not start with a digit.",
            "Line 4: setup is text. Write it after setup: |, one line under another.",
            "Line 5: image is not a setting of workstream.yml. The settings are size, setup, surfaces, apps, variables, instructions.",
        }, read.Errors.ToArray());
        StringAssert.StartsWith(WorkstreamFile.Read("apps: chrome\n").Errors.Single(), "Line 1: apps is a list, such as apps: [vscode, terminal].");
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
            new WorkstreamFileSettings("large", ["chrome", "android", "iphone", "vscode", "terminal"], null, [], null),
            new WorkstreamFileSettings("standard", ["android"], null, [], null),
            new WorkstreamFileSettings("standard", [], null, [], null),
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

        // Each kind goes under its own setting, and both are always written: an empty list says none.
        var written = WorkstreamFile.Write(new WorkstreamFileSettings("standard", ["chrome", "android", "vscode", "terminal"], null, [], null));
        StringAssert.Contains(written, "surfaces: [chrome, android]\n");
        StringAssert.Contains(written, "apps: [vscode, terminal]\n");
        StringAssert.Contains(WorkstreamFile.Write(new WorkstreamFileSettings("standard", ["vscode"], null, [], null)), "surfaces: []\n");
        // A list in another order comes back with its surfaces first.
        var mixed = WorkstreamFile.Read(WorkstreamFile.Write(new WorkstreamFileSettings("standard", ["terminal", "android", "vscode", "chrome"], null, [], null)));
        CollectionAssert.AreEqual(new[] { "android", "chrome", "terminal", "vscode" }, mixed.Settings!.Apps.ToArray());
    }
}
