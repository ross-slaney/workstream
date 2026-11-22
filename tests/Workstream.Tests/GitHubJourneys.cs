using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workstream.Api;
using Workstream.Api.Domain;
using Workstream.Api.GitHub;

namespace Workstream.Tests;

/// <summary>
/// GitHub as the people who signed in with it reach it: a task's desktop gets the token of the person who
/// started the task, and nobody else's; the environment form lists a person's repositories and branches, reads
/// a repository's workstream.yml, and asks for a suggested setup; the environment is saved back to the
/// repository as workstream.yml, by a commit or, where the branch is protected, a pull request; and a task's
/// start reads the file again.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class GitHubJourneys
{
    /// <summary>A person who signed in with GitHub: SqlOS keeps the token GitHub gave them.</summary>
    static string SignedInWithGitHub(Person person)
    {
        var token = Stack.GitHub.NewAccount();
        Stack.GitHubTokens.Give(person.UserId, token);
        return token;
    }

    static async Task<Person> MemberAsync(Person admin, string teamId)
    {
        var invitation = await admin.PostAsync<PendingInvitation>($"/api/teams/{teamId}/invitations", new Invite(Person.NewEmail()));
        return await Person.SignUpAsync("Lin Clark", invitation.Email, Answers.TokenOf(invitation));
    }

    static async Task<string> EnvironmentAsync(Person person, string teamId, string? repositoryUrl, string? setup = null, IReadOnlyList<VariableChange>? variables = null,
        bool fromFile = false, string? branch = null)
    {
        var id = Guid.NewGuid().ToString("D");
        await person.PostAsync($"/api/teams/{teamId}/environments",
            new AddEnvironment(id, "Project " + id[..6], ["chrome", "vscode", "terminal"], "small", repositoryUrl, branch, setup, null, variables, fromFile)).IsAsync(HttpStatusCode.Created);
        return id;
    }

    /// <summary>Room for every desktop a journey starts: the system administrator raises the team's limit.</summary>
    static async Task RoomAsync(string teamId)
    {
        using var administrator = await Person.SignInAsync(Stack.AdministratorEmail, Stack.AdministratorPassword);
        await administrator.PutAsync($"/api/admin/teams/{teamId}/limits", new SetLimits(20m, 10)).IsAsync(HttpStatusCode.OK);
    }

    static Task<TaskDto> StartAsync(Person person, string teamId, string environmentId) =>
        person.PostAsync<TaskDto>($"/api/teams/{teamId}/tasks", new StartTask(Guid.NewGuid().ToString("D"), environmentId, null, "Fix the build.", Guid.NewGuid().ToString("D")));

    static async Task<WorkEnvironment> LoadAsync(string id)
    {
        using var scope = Stack.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>().Environments.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    // A desktop's token.

    [TestMethod]
    public async Task A_desktop_gets_the_token_of_the_person_who_started_its_task()
    {
        var (ada, team) = await Person.WithTeamAsync("Ada Lovelace");
        await RoomAsync(team.Id);
        var adaToken = SignedInWithGitHub(ada);
        var repository = Stack.GitHub.Give(adaToken);
        var environment = await EnvironmentAsync(ada, team.Id, repository.Url);
        var task = await StartAsync(ada, team.Id, environment);
        using var desktop = await Desktop.StartAsync(task.Id);

        var token = await Person.ReadAsync<DesktopGitHubToken>(await desktop.GetAsync("/api/desktop/github/token"));
        Assert.AreEqual(adaToken, token.Token);
        Assert.AreEqual(repository.FullName, token.Repository);

        // Someone else on the team starts a task from the same environment: their desktop pushes as them.
        var lin = await MemberAsync(ada, team.Id);
        var linToken = SignedInWithGitHub(lin);
        var theirs = await StartAsync(lin, team.Id, environment);
        using var linDesktop = await Desktop.StartAsync(theirs.Id);
        Assert.AreEqual(linToken, (await Person.ReadAsync<DesktopGitHubToken>(await linDesktop.GetAsync("/api/desktop/github/token"))).Token);

        // A desktop with no repository still gets it: the agent may clone another of the person's repositories.
        var plain = await StartAsync(ada, team.Id, await EnvironmentAsync(ada, team.Id, null));
        using var plainDesktop = await Desktop.StartAsync(plain.Id);
        var bare = await Person.ReadAsync<DesktopGitHubToken>(await plainDesktop.GetAsync("/api/desktop/github/token"));
        Assert.AreEqual(adaToken, bare.Token);
        Assert.IsNull(bare.Repository);
    }

    [TestMethod]
    public async Task A_desktops_commits_are_by_the_person_who_started_its_task_at_their_GitHub_noreply_address()
    {
        var (ada, team) = await Person.WithTeamAsync("Ada Lovelace");
        await RoomAsync(team.Id);
        var adaToken = SignedInWithGitHub(ada);
        var environment = await EnvironmentAsync(ada, team.Id, Stack.GitHub.Give(adaToken).Url);
        using var desktop = await Desktop.StartAsync((await StartAsync(ada, team.Id, environment)).Id);
        var account = Stack.GitHub.UserOf(adaToken);
        var author = await Person.ReadAsync<DesktopGitAuthor>(await desktop.GetAsync("/api/desktop/github/author"));
        Assert.AreEqual("Ada Lovelace", author.Name);
        Assert.AreEqual($"{account.Id}+{account.Login}@users.noreply.github.com", author.Email);

        // An account with no name shown is its login.
        var lin = await MemberAsync(ada, team.Id);
        var linToken = Stack.GitHub.NewAccount(name: null);
        Stack.GitHubTokens.Give(lin.UserId, linToken);
        using var linDesktop = await Desktop.StartAsync((await StartAsync(lin, team.Id, environment)).Id);
        Assert.AreEqual(Stack.GitHub.UserOf(linToken).Login, (await Person.ReadAsync<DesktopGitAuthor>(await linDesktop.GetAsync("/api/desktop/github/author"))).Name);

        // Without GitHub, the desktop is told who has not signed in, and keeps the agent's own name.
        var (grace, graceTeam) = await Person.WithTeamAsync("Grace Hopper");
        var graceEnvironment = await EnvironmentAsync(grace, graceTeam.Id, "https://github.com/acme/app");
        using var graceDesktop = await Desktop.StartAsync((await StartAsync(grace, graceTeam.Id, graceEnvironment)).Id);
        await graceDesktop.GetAsync("/api/desktop/github/author").IsAsync(HttpStatusCode.NotFound, "Grace Hopper has not signed in to Workstream with GitHub");

        // A token GitHub no longer takes gives no author either; nobody without a key gets one.
        Stack.GitHub.Revoke(adaToken);
        await desktop.GetAsync("/api/desktop/github/author").IsAsync(HttpStatusCode.NotFound, "no longer accepts");
        using var nobody = new HttpClient { BaseAddress = Stack.Origin };
        await nobody.GetAsync("/api/desktop/github/author").IsAsync(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task A_desktop_whose_starter_did_not_sign_in_with_GitHub_is_told_so_in_words_the_agent_passes_on()
    {
        var (grace, team) = await Person.WithTeamAsync("Grace Hopper");
        var task = await StartAsync(grace, team.Id, await EnvironmentAsync(grace, team.Id, "https://github.com/acme/app"));
        using var desktop = await Desktop.StartAsync(task.Id);
        await desktop.GetAsync("/api/desktop/github/token").IsAsync(HttpStatusCode.NotFound, "Grace Hopper has not signed in to Workstream with GitHub");

        // The task's page says the same, to the person who started it and to the rest of the team.
        var mine = await grace.GetAsync<TaskGitHub>($"/api/tasks/{task.Id}/github");
        Assert.IsTrue(mine.SignInNeeded);
        Assert.IsTrue(mine.StartedByYou);
        var lin = await MemberAsync(grace, team.Id);
        var theirs = await lin.GetAsync<TaskGitHub>($"/api/tasks/{task.Id}/github");
        Assert.IsTrue(theirs.SignInNeeded);
        Assert.IsFalse(theirs.StartedByYou);
        Assert.AreEqual("Grace Hopper", theirs.StartedByName);

        // Once she signs in with GitHub, the next ask works and the page says nothing.
        var token = SignedInWithGitHub(grace);
        Assert.AreEqual(token, (await Person.ReadAsync<DesktopGitHubToken>(await desktop.GetAsync("/api/desktop/github/token"))).Token);
        Assert.IsFalse((await grace.GetAsync<TaskGitHub>($"/api/tasks/{task.Id}/github")).SignInNeeded);

        // Someone outside the team sees nothing of it.
        using var stranger = await Person.SignUpAsync("Mallory");
        await stranger.GetAsync($"/api/tasks/{task.Id}/github").IsAsync(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task A_token_in_the_environments_variables_wins_and_the_page_asks_nobody_to_sign_in()
    {
        var (ada, team) = await Person.WithTeamAsync();
        var environment = await EnvironmentAsync(ada, team.Id, "https://github.com/acme/app", variables: [new VariableChange("GITHUB_TOKEN", "ghp_pasted")]);
        var task = await StartAsync(ada, team.Id, environment);
        Assert.IsFalse((await ada.GetAsync<TaskGitHub>($"/api/tasks/{task.Id}/github")).SignInNeeded);
        using var desktop = await Desktop.StartAsync(task.Id);
        // The desktop has it among its variables, which the agent's git and gh use before asking (desktop/agent/github.mjs).
        Assert.AreEqual("ghp_pasted", (await desktop.SetupAsync()).Variables["GITHUB_TOKEN"]);
        // A repository that is not on GitHub asks nobody to sign in with GitHub either.
        var elsewhere = await StartAsync(ada, team.Id, await EnvironmentAsync(ada, team.Id, "https://gitlab.com/acme/app"));
        Assert.IsFalse((await ada.GetAsync<TaskGitHub>($"/api/tasks/{elsewhere.Id}/github")).SignInNeeded);
    }

    [TestMethod]
    public async Task Only_a_desktop_with_its_key_gets_a_token_and_never_once_its_task_is_gone()
    {
        var (ada, team) = await Person.WithTeamAsync();
        SignedInWithGitHub(ada);
        var task = await StartAsync(ada, team.Id, await EnvironmentAsync(ada, team.Id, "https://github.com/acme/app"));
        using var desktop = await Desktop.StartAsync(task.Id);

        // Nobody, and a person with their own token, are not a desktop.
        using var nobody = new HttpClient { BaseAddress = Stack.Origin };
        await nobody.GetAsync("/api/desktop/github/token").IsAsync(HttpStatusCode.Unauthorized);
        await ada.GetAsync("/api/desktop/github/token").IsAsync(HttpStatusCode.Unauthorized);

        await ada.DeleteAsync($"/api/tasks/{task.Id}").IsAsync(HttpStatusCode.NoContent);
        await Stack.Jobs.DeleteAsync(task.Id, CancellationToken.None);
        using var gone = await desktop.GetAsync("/api/desktop/github/token");
        Assert.IsTrue(gone.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound, $"The desktop of a deleted task was answered {(int)gone.StatusCode}.");
    }

    [TestMethod]
    public async Task A_machine_preparing_an_environment_gets_the_token_of_whoever_last_saved_it()
    {
        var (ada, team) = await Person.WithTeamAsync();
        var adaToken = SignedInWithGitHub(ada);
        var repository = Stack.GitHub.Give(adaToken);
        var environment = await EnvironmentAsync(ada, team.Id, repository.Url, setup: "npm ci");
        await Stack.Jobs.PrepareAsync(environment, CancellationToken.None);
        using var preparing = Desktop.Preparing(environment);
        Assert.AreEqual(adaToken, (await Person.ReadAsync<DesktopGitHubToken>(await preparing.GetAsync("/api/desktop/github/token"))).Token);
    }

    // The person's repositories.

    [TestMethod]
    public async Task A_person_lists_their_own_repositories_most_recently_pushed_first_and_their_branches()
    {
        using var ada = await Person.SignUpAsync();
        var token = SignedInWithGitHub(ada);
        var older = Stack.GitHub.Give(token, "site");
        older.PushedAt = DateTimeOffset.UtcNow.AddDays(-3);
        var newer = Stack.GitHub.Give(token, "app");
        newer.Branches["feature/login"] = new();
        newer.Branches["develop"] = new();

        var listed = await ada.GetAsync<GitHubRepositories>("/api/github/repositories");
        Assert.IsTrue(listed.Available);
        Assert.IsTrue(listed.SignedIn);
        Assert.IsFalse(listed.More);
        CollectionAssert.AreEqual(new[] { newer.FullName, older.FullName }, listed.Repositories.Select(x => x.FullName).ToArray());
        Assert.AreEqual(newer.Url, listed.Repositories[0].Url);
        Assert.AreEqual("main", listed.Repositories[0].DefaultBranch);

        var branches = await ada.GetAsync<GitHubBranches>($"/api/github/repositories/{newer.Owner}/{newer.Name}/branches");
        Assert.AreEqual("main", branches.DefaultBranch);
        CollectionAssert.AreEqual(new[] { "main", "develop", "feature/login" }, branches.Branches.ToArray());

        // Someone else's repository is not theirs to list.
        using var lin = await Person.SignUpAsync("Lin Clark");
        SignedInWithGitHub(lin);
        await lin.GetAsync($"/api/github/repositories/{newer.Owner}/{newer.Name}/branches").IsAsync(HttpStatusCode.NotFound);
        Assert.AreEqual(0, (await lin.GetAsync<GitHubRepositories>("/api/github/repositories")).Repositories.Count);
    }

    [TestMethod]
    public async Task Someone_who_did_not_sign_in_with_GitHub_is_told_to_and_nobody_gets_anything()
    {
        using var grace = await Person.SignUpAsync("Grace Hopper");
        var listed = await grace.GetAsync<GitHubRepositories>("/api/github/repositories");
        Assert.IsTrue(listed.Available);
        Assert.IsFalse(listed.SignedIn);
        Assert.AreEqual(0, listed.Repositories.Count);
        await grace.GetAsync("/api/github/repositories/acme/app/branches").IsAsync(HttpStatusCode.Forbidden, "Sign in with GitHub");
        await grace.GetAsync("/api/github/repositories/acme/app/workstream-file").IsAsync(HttpStatusCode.Forbidden, "Sign in with GitHub");

        // A token GitHub no longer takes is the same as none.
        var token = SignedInWithGitHub(grace);
        Stack.GitHub.Revoke(token);
        Assert.IsFalse((await grace.GetAsync<GitHubRepositories>("/api/github/repositories")).SignedIn);
        await grace.GetAsync("/api/github/repositories/acme/app/branches").IsAsync(HttpStatusCode.Forbidden, "no longer accepts");

        using var nobody = new HttpClient { BaseAddress = Stack.Origin };
        await nobody.GetAsync("/api/github/repositories").IsAsync(HttpStatusCode.Unauthorized);
    }

    // workstream.yml.

    [TestMethod]
    public async Task A_repositorys_workstream_yml_is_read_on_its_branch_and_named_wrong_where_it_is()
    {
        using var ada = await Person.SignUpAsync();
        var token = SignedInWithGitHub(ada);
        var repository = Stack.GitHub.Give(token)
            .With("workstream.yml", "size: large\napps: [chrome, terminal]\nvariables: [DATABASE_URL]\nsetup: |\n  npm ci\n  npm run build\n", branch: "develop")
            .With("workstream.yml", "size: huge\napps: [chrome, photoshop]\ncolour: blue\n");
        var path = $"/api/github/repositories/{repository.Owner}/{repository.Name}/workstream-file";

        var good = await ada.GetAsync<RepositoryFile>($"{path}?branch=develop");
        Assert.IsTrue(good.Found);
        Assert.AreEqual("large", good.Settings!.Size);
        CollectionAssert.AreEqual(new[] { "chrome", "terminal" }, good.Settings.Apps.ToArray());
        CollectionAssert.AreEqual(new[] { "DATABASE_URL" }, good.Settings.Variables.ToArray());
        Assert.AreEqual("npm ci\nnpm run build", good.Settings.Setup);

        var bad = await ada.GetAsync<RepositoryFile>($"{path}?branch=main");
        Assert.IsTrue(bad.Found);
        Assert.IsNull(bad.Settings);
        Assert.AreEqual(3, bad.Errors.Count);
        StringAssert.StartsWith(bad.Errors[0], "Line 1: size is small, standard or large, not huge.");
        StringAssert.Contains(bad.Errors[1], "photoshop is not an app");
        StringAssert.Contains(bad.Errors[2], "Line 3: colour is not a setting of workstream.yml");

        repository.Branches["empty"] = new();
        Assert.IsFalse((await ada.GetAsync<RepositoryFile>($"{path}?branch=empty")).Found);
    }

    [TestMethod]
    public async Task An_environment_is_saved_to_its_repository_as_workstream_yml_by_a_commit_as_the_person()
    {
        var (ada, team) = await Person.WithTeamAsync();
        var token = SignedInWithGitHub(ada);
        var repository = Stack.GitHub.Give(token);
        var environment = await EnvironmentAsync(ada, team.Id, repository.Url, setup: "npm ci\nnpm test", variables: [new VariableChange("DATABASE_URL", "postgres://secret")]);

        var saved = await ada.PostAsync<SavedRepositoryFile>($"/api/environments/{environment}/workstream-file");
        Assert.AreEqual("commit", saved.Kind);
        Assert.AreEqual("main", saved.Branch);
        StringAssert.StartsWith(saved.Url, $"https://github.com/{repository.FullName}/commit/");
        var (branch, path, message) = repository.Commits.Single();
        Assert.AreEqual(("main", "workstream.yml", "Add workstream.yml"), (branch, path, message));

        // The file says what the environment does, with the variable's name and never its value.
        var text = repository.Branches["main"]["workstream.yml"];
        Assert.IsFalse(text.Contains("postgres://secret"));
        var read = WorkstreamFile.Read(text);
        Assert.AreEqual("small", read.Settings!.Size);
        Assert.AreEqual("npm ci\nnpm test", read.Settings.Setup);
        CollectionAssert.AreEqual(new[] { "DATABASE_URL" }, read.Settings.Variables.ToArray());
        Assert.IsTrue((await LoadAsync(environment)).FromFile);
        Assert.IsTrue((await ada.GetAsync<EnvironmentDto>($"/api/environments/{environment}")).FromFile);

        // Saving again replaces it.
        Assert.AreEqual("commit", (await ada.PostAsync<SavedRepositoryFile>($"/api/environments/{environment}/workstream-file")).Kind);
        Assert.AreEqual("Update workstream.yml", repository.Commits.Last().Message);
    }

    [TestMethod]
    public async Task A_protected_branch_gets_the_file_through_a_pull_request()
    {
        var (ada, team) = await Person.WithTeamAsync();
        var token = SignedInWithGitHub(ada);
        var repository = Stack.GitHub.Give(token);
        repository.Protected.Add("main");
        var environment = await EnvironmentAsync(ada, team.Id, repository.Url, setup: "make deps");

        var saved = await ada.PostAsync<SavedRepositoryFile>($"/api/environments/{environment}/workstream-file");
        Assert.AreEqual("pull_request", saved.Kind);
        StringAssert.StartsWith(saved.Branch, "workstream-yml-");
        Assert.AreEqual($"https://github.com/{repository.FullName}/pull/1", saved.Url);
        var pull = repository.PullRequests.Single();
        Assert.AreEqual((saved.Branch, "main", "Add workstream.yml"), pull);
        Assert.IsTrue(repository.Branches[saved.Branch].ContainsKey("workstream.yml"));
        Assert.IsFalse(repository.Branches["main"].ContainsKey("workstream.yml"));
    }

    [TestMethod]
    public async Task Saving_workstream_yml_needs_the_environment_a_GitHub_repository_and_a_person_who_can_push()
    {
        var (ada, team) = await Person.WithTeamAsync();
        var token = SignedInWithGitHub(ada);
        var readOnly = Stack.GitHub.Give(token, canPush: false);
        var environment = await EnvironmentAsync(ada, team.Id, readOnly.Url);
        await ada.PostAsync($"/api/environments/{environment}/workstream-file").IsAsync(HttpStatusCode.Forbidden, "GitHub did not let you write to");

        var elsewhere = await EnvironmentAsync(ada, team.Id, "https://gitlab.com/acme/app");
        await ada.PostAsync($"/api/environments/{elsewhere}/workstream-file").IsAsync(HttpStatusCode.BadRequest, "not on GitHub");

        // Someone on the team may (ENVIRONMENT_EDIT), as themselves; without GitHub they are told to sign in.
        var writable = Stack.GitHub.Give(token);
        var shared = await EnvironmentAsync(ada, team.Id, writable.Url);
        var lin = await MemberAsync(ada, team.Id);
        await lin.PostAsync($"/api/environments/{shared}/workstream-file").IsAsync(HttpStatusCode.Forbidden, "Sign in with GitHub");

        using var stranger = await Person.SignUpAsync("Mallory");
        SignedInWithGitHub(stranger);
        await stranger.PostAsync($"/api/environments/{shared}/workstream-file").IsAsync(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task A_task_start_reads_workstream_yml_again_and_starts_from_what_the_environment_holds_when_it_cannot()
    {
        var (ada, team) = await Person.WithTeamAsync();
        await RoomAsync(team.Id);
        var token = SignedInWithGitHub(ada);
        var repository = Stack.GitHub.Give(token).With("workstream.yml", "size: small\napps: [chrome, vscode, terminal]\nsetup: npm ci\n");
        var environment = await EnvironmentAsync(ada, team.Id, repository.Url, setup: "npm ci", fromFile: true);
        Assert.IsTrue((await LoadAsync(environment)).FromFile);

        // Someone changes the file in the repository: the next task is set up from what it says now.
        repository.With("workstream.yml", "size: large\napps: [chrome, terminal]\nsetup: |\n  pnpm install\ninstructions: Run pnpm test.\n");
        var task = await StartAsync(ada, team.Id, environment);
        Assert.AreEqual("large", task.Size);
        using (var desktop = await Desktop.StartAsync(task.Id))
        {
            var setup = await desktop.SetupAsync();
            Assert.AreEqual("pnpm install", setup.SetupScript);
            CollectionAssert.AreEqual(new[] { "chrome", "terminal" }, setup.Apps.ToArray());
        }
        var changed = await LoadAsync(environment);
        Assert.AreEqual("Run pnpm test.", changed.Instructions);
        Assert.IsTrue(changed.FromFile);

        // A file that is not valid now, or GitHub refusing the person, leaves the environment as it is.
        repository.With("workstream.yml", "size: enormous\n");
        Assert.AreEqual("large", (await StartAsync(ada, team.Id, environment)).Size);
        Stack.GitHub.Revoke(token);
        repository.With("workstream.yml", "size: small\n");
        Assert.AreEqual("large", (await StartAsync(ada, team.Id, environment)).Size);
        Assert.AreEqual("pnpm install", (await LoadAsync(environment)).SetupScript);
    }

    // A suggestion.

    /// <summary>The model's answer, as Foundry gives it for a request with a JSON schema output format.</summary>
    static string Suggested(object input) => new JsonObject
    {
        ["id"] = "msg_1", ["type"] = "message", ["role"] = "assistant", ["model"] = "claude-sonnet-5-5",
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = System.Text.Json.JsonSerializer.Serialize(input) }),
        ["stop_reason"] = "end_turn",
        ["usage"] = new JsonObject { ["input_tokens"] = 20_000, ["output_tokens"] = 600 },
    }.ToJsonString();

    [TestMethod]
    public async Task A_suggestion_reads_the_repository_through_GitHub_asks_the_model_and_is_charged_to_the_team()
    {
        var (ada, team) = await Person.WithTeamAsync();
        var token = SignedInWithGitHub(ada);
        var repository = Stack.GitHub.Give(token)
            .With("package.json", """{ "name": "app", "packageManager": "pnpm@9.1.0", "scripts": { "test": "vitest" } }""")
            .With("pnpm-lock.yaml", "lockfileVersion: '9.0'\n")
            .With(".nvmrc", "20\n")
            .With("README.md", "# App\nRun pnpm dev.\n")
            .With(".github/workflows/ci.yml", "jobs:\n  test:\n    steps:\n      - run: pnpm test\n")
            .With("docker-compose.yml", "services:\n  db:\n    image: postgres:16\n")
            .With("src/index.ts", "export {};\n")
            .With("node_modules/left-pad/package.json", "{}");
        var answered = Stack.Model.Answer;
        Stack.Model.Answer = () => FakeModel.Json(Suggested(new
        {
            setup_script = "corepack enable\npnpm install --frozen-lockfile", size = "huge", apps = new[] { "chrome", "vscode", "terminal", "photoshop" },
            variables = new[] { "DATABASE_URL", "not a name" }, instructions = "Start the database with docker compose up -d first.", why = "A pnpm app with Postgres in Docker Compose.",
        }));
        var calls = Stack.Model.Calls.Count;
        try
        {
            var suggestion = await ada.PostAsync<EnvironmentSuggestion>($"/api/teams/{team.Id}/environment-suggestions", new SuggestEnvironment(repository.Url, null));
            Assert.AreEqual("corepack enable\npnpm install --frozen-lockfile", suggestion.SetupScript);
            // What a desktop cannot have is dropped: a size it does not come in, an app it does not have, a name a shell cannot read.
            Assert.AreEqual("standard", suggestion.Size);
            CollectionAssert.AreEqual(new[] { "chrome", "vscode", "terminal" }, suggestion.Apps.ToArray());
            CollectionAssert.AreEqual(new[] { "DATABASE_URL" }, suggestion.Variables.ToArray());
            Assert.AreEqual("A pnpm app with Postgres in Docker Compose.", suggestion.Why);

            // The model was asked once, with the files that say how the repository is built and not what it vendors.
            Assert.AreEqual(calls + 1, Stack.Model.Calls.Count);
            var call = Stack.Model.Calls.Last();
            Assert.AreEqual("/anthropic/v1/messages", call.Uri.AbsolutePath);
            Assert.AreEqual(Stack.ModelKey, call.Headers["x-api-key"]);
            var asked = JsonNode.Parse(call.Body)!;
            Assert.AreEqual("json_schema", asked["output_config"]!["format"]!["type"]!.GetValue<string>());
            Assert.IsNull(asked["tool_choice"]);
            var prompt = asked["messages"]![0]!["content"]!.GetValue<string>();
            foreach (var file in new[] { "package.json", "pnpm-lock.yaml", ".nvmrc", "README.md", ".github/workflows/ci.yml", "docker-compose.yml" })
            {
                StringAssert.Contains(prompt, $"<file path=\"{file}\"");
            }
            Assert.IsFalse(prompt.Contains("<file path=\"src/index.ts\""));
            Assert.IsFalse(prompt.Contains("left-pad"));

            // It is the team's model spending, like an agent's.
            var usage = await ada.GetAsync<TeamUsage>($"/api/teams/{team.Id}/usage");
            Assert.IsTrue(usage.ModelSpentUsd > 0);
        }
        finally
        {
            Stack.Model.Answer = answered;
        }
    }

    [TestMethod]
    public async Task A_suggestion_is_for_someone_who_may_add_an_environment_with_GitHub_and_allowance_left()
    {
        var (ada, team) = await Person.WithTeamAsync();
        var token = SignedInWithGitHub(ada);
        var repository = Stack.GitHub.Give(token).With("package.json", "{}");
        var path = $"/api/teams/{team.Id}/environment-suggestions";

        using var stranger = await Person.SignUpAsync("Mallory");
        await stranger.PostAsync(path, new SuggestEnvironment(repository.Url, null)).IsAsync(HttpStatusCode.NotFound);
        var lin = await MemberAsync(ada, team.Id);
        await lin.PostAsync(path, new SuggestEnvironment(repository.Url, null)).IsAsync(HttpStatusCode.Forbidden, "Sign in with GitHub");
        await ada.PostAsync(path, new SuggestEnvironment("https://gitlab.com/acme/app", null)).IsAsync(HttpStatusCode.BadRequest);
        await ada.PostAsync(path, new SuggestEnvironment(repository.Url, "no-such-branch")).IsAsync(HttpStatusCode.BadRequest, "has no branch no-such-branch");

        var answered = Stack.Model.Answer;
        try
        {
            // An answer that does not use the tool is said plainly.
            Stack.Model.Answer = () => FakeModel.Json(FakeModel.Message("claude-sonnet-5-5", input: 10, output: 10));
            await ada.PostAsync(path, new SuggestEnvironment(repository.Url, null)).IsAsync(HttpStatusCode.ServiceUnavailable, "could not be read");
            Stack.Model.Answer = () => FakeModel.Json("""{"type":"error"}""", HttpStatusCode.InternalServerError);
            await ada.PostAsync(path, new SuggestEnvironment(repository.Url, null)).IsAsync(HttpStatusCode.ServiceUnavailable, "did not answer");
        }
        finally
        {
            Stack.Model.Answer = answered;
        }

        // A team that used its allowance gets no suggestion.
        using var administrator = await Person.SignInAsync(Stack.AdministratorEmail, Stack.AdministratorPassword);
        await administrator.PutAsync($"/api/admin/teams/{team.Id}/limits", new SetLimits(0m, 2)).IsAsync(HttpStatusCode.OK);
        Stack.Services.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>().Remove(Workstream.Api.Model.ModelGateway.AllowanceKey(team.Id));
        await ada.PostAsync(path, new SuggestEnvironment(repository.Url, null)).IsAsync(HttpStatusCode.PaymentRequired, "allowance");
    }
}
