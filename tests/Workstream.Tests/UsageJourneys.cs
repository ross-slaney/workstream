using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Workstream.Api;
using Workstream.Api.Desktops;
using Workstream.Api.Domain;

namespace Workstream.Tests;

/// <summary>
/// A team's usage in the units a bill would use: desktop hours by size, and storage in GB-months by kind, from
/// rows a job keeps in step with what the team keeps. The job writes for every team at once, so these run alone.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class UsageJourneys
{
    const long Gigabyte = 1024L * 1024 * 1024;

    static async Task<(Person Person, TeamDto Team, EnvironmentDto Environment)> TeamAsync(string name = "Ada Lovelace")
    {
        var (person, team) = await Person.WithTeamAsync(name);
        var environment = (await person.GetAsync<ListPage<EnvironmentDto>>($"/api/teams/{team.Id}/environments")).Data.Single();
        return (person, team, environment);
    }

    static Task<TaskDto> StartAsync(Person person, string teamId, string environmentId) =>
        person.PostAsync<TaskDto>($"/api/teams/{teamId}/tasks", new StartTask(Guid.NewGuid().ToString("D"), environmentId, null, "Check the build.", Guid.NewGuid().ToString("D")));

    static Task<TeamUsage> UsageAsync(Person person, string teamId) => person.GetAsync<TeamUsage>($"/api/teams/{teamId}/usage");

    static Task ReconcileAsync() => Stack.Storage.ReconcileAsync(CancellationToken.None);

    /// <summary>The same job where a standard desktop's disk is another size.</summary>
    static StorageJobs ReconcileWith(int standardDiskGb)
    {
        var options = new DesktopOptions { Host = "none" };
        options.Azure.DiskSizesGb[DesktopSizes.Standard] = standardDiskGb;
        return new StorageJobs(Stack.Services.GetRequiredService<IServiceScopeFactory>(), options, TimeProvider.System, Stack.Services.GetRequiredService<ILogger<StorageJobs>>());
    }

    static async Task<List<StorageRun>> RowsAsync(string teamId)
    {
        using var scope = Stack.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>().StorageRuns.AsNoTracking().Where(x => x.TeamId == teamId).OrderBy(x => x.StartedAt).ToListAsync();
    }

    static async Task ChangeAsync(Func<WorkstreamDbContext, Task> change)
    {
        using var scope = Stack.Services.CreateScope();
        await change(scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>());
    }

    /// <summary>Hours ago, as far as this month goes back: usage counts the calendar month.</summary>
    static double HoursThisMonth(double hoursAgo)
    {
        var now = DateTimeOffset.UtcNow;
        return Math.Min(hoursAgo, (now - Operations.MonthStart(now)).TotalHours);
    }

    [TestMethod]
    public async Task Everyone_on_the_team_reads_its_usage_and_nobody_else_does()
    {
        var (ada, team, environment) = await TeamAsync();
        var address = Person.NewEmail();
        var invitation = await ada.PostAsync<PendingInvitation>($"/api/teams/{team.Id}/invitations", new Invite(address));
        using var lin = await Person.SignUpAsync("Lin Clark", address, Answers.TokenOf(invitation));
        var task = await StartAsync(ada, team.Id, environment.Id);
        using var desktop = await Desktop.StartAsync(task.Id);
        await ReconcileAsync();

        // The administrator and a member read the same numbers.
        foreach (var person in new[] { ada, lin })
        {
            var usage = await UsageAsync(person, team.Id);
            Assert.AreEqual(Operations.MonthStart(DateTimeOffset.UtcNow), usage.Since);
            Assert.AreEqual(team.ModelAllowanceUsd, usage.ModelAllowanceUsd);
            Assert.AreEqual(1, usage.RunningDesktops);
            Assert.AreEqual(team.MaxRunningDesktops, usage.MaxRunningDesktops);
            Assert.AreEqual(DesktopSizes.Standard, usage.DesktopHours.Single().Size);
            Assert.AreEqual(StorageKind.Disk, usage.Storage.Single().Kind);
        }

        // A stranger, the system administrator (who is on no team), a desktop with its key, and nobody.
        using var stranger = await Person.SignUpAsync("Grace Hopper");
        await stranger.GetAsync($"/api/teams/{team.Id}/usage").IsAsync(HttpStatusCode.NotFound);
        using var administrator = await Person.SignInAsync(Stack.AdministratorEmail, Stack.AdministratorPassword);
        await administrator.GetAsync($"/api/teams/{team.Id}/usage").IsAsync(HttpStatusCode.NotFound);
        await desktop.GetAsync($"/api/teams/{team.Id}/usage").IsAsync(HttpStatusCode.Unauthorized);
        using var anonymous = new HttpClient { BaseAddress = Stack.Origin };
        await anonymous.GetAsync($"/api/teams/{team.Id}/usage").IsAsync(HttpStatusCode.Unauthorized);

        // Someone removed from the team reads it no longer.
        await ada.DeleteAsync($"/api/teams/{team.Id}/people/{lin.UserId}").IsAsync(HttpStatusCode.NoContent);
        using var linAgain = await Person.SignInAsync(address);
        await linAgain.GetAsync($"/api/teams/{team.Id}/usage").IsAsync(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task Desktop_hours_are_counted_for_each_size_the_team_used()
    {
        var (ada, team, standard) = await TeamAsync();
        var large = await ada.PostAsync<EnvironmentDto>($"/api/teams/{team.Id}/environments",
            new AddEnvironment(Guid.NewGuid().ToString("D"), "Whole project", ["chrome", "terminal"], DesktopSizes.Large, null, null, null, null, null));
        // Nothing has run yet: no size is listed.
        Assert.AreEqual(0, (await UsageAsync(ada, team.Id)).DesktopHours.Count);

        var first = await StartAsync(ada, team.Id, standard.Id);
        var big = await StartAsync(ada, team.Id, large.Id);
        using var firstDesktop = await Desktop.StartAsync(first.Id);
        using var bigDesktop = await Desktop.StartAsync(big.Id);
        // The large desktop ran for three hours and was stopped an hour ago; the standard one has run for two and still is.
        var now = DateTimeOffset.UtcNow;
        await ada.PostAsync($"/api/tasks/{big.Id}/stop").IsAsync(HttpStatusCode.OK);
        await Stack.Jobs.StopAsync(big.Id, CancellationToken.None);
        await ChangeAsync(async db =>
        {
            await db.DesktopRuns.Where(x => x.TaskId == big.Id).ExecuteUpdateAsync(run => run.SetProperty(x => x.StartedAt, now.AddHours(-4)).SetProperty(x => x.EndedAt, now.AddHours(-1)));
            await db.DesktopRuns.Where(x => x.TaskId == first.Id).ExecuteUpdateAsync(run => run.SetProperty(x => x.StartedAt, now.AddHours(-2)));
        });
        // A second standard desktop, an hour old, is added to the first one's time.
        var second = await StartAsync(ada, team.Id, standard.Id);
        using var secondDesktop = await Desktop.StartAsync(second.Id);
        await ChangeAsync(db => db.DesktopRuns.Where(x => x.TaskId == second.Id).ExecuteUpdateAsync(run => run.SetProperty(x => x.StartedAt, now.AddHours(-1))));

        var usage = await UsageAsync(ada, team.Id);
        // From the smallest size up, and only the sizes used.
        CollectionAssert.AreEqual(new[] { DesktopSizes.Standard, DesktopSizes.Large }, usage.DesktopHours.Select(x => x.Size).ToArray());
        Assert.AreEqual(HoursThisMonth(2) + HoursThisMonth(1), usage.DesktopHours[0].Hours, 0.05);
        Assert.AreEqual(HoursThisMonth(4) - HoursThisMonth(1), usage.DesktopHours[1].Hours, 0.05);
        Assert.AreEqual(2, usage.RunningDesktops);

        // The system administrator sees each size as a line of its own, at its own price.
        using var administrator = await Person.SignInAsync(Stack.AdministratorEmail, Stack.AdministratorPassword);
        var machines = (await administrator.GetAsync<AdminOverview>("/api/admin/overview")).Machines.ThisMonth.Where(x => x.Purpose == MachinePurpose.Task).ToList();
        var prices = Stack.Services.GetRequiredService<DesktopOptions>().Prices;
        foreach (var size in new[] { DesktopSizes.Standard, DesktopSizes.Large })
        {
            var line = machines.Single(x => x.Size == size);
            Assert.IsTrue(line.Hours >= usage.DesktopHours.Single(x => x.Size == size).Hours - 0.05);
            Assert.AreEqual((double)(prices.HourOf(size) * (decimal)line.Hours), (double)line.Usd, 0.011);
        }
        CollectionAssert.AreEqual(machines.OrderBy(x => DesktopSizes.Keys.ToList().IndexOf(x.Size)).ToList(), machines);
    }

    [TestMethod]
    public async Task The_job_opens_a_row_for_a_desktops_disk_keeps_it_while_stopped_and_closes_it_when_the_task_is_deleted()
    {
        var (ada, team, environment) = await TeamAsync();
        var task = await StartAsync(ada, team.Id, environment.Id);
        // No machine yet, so nothing is kept.
        await ReconcileAsync();
        Assert.AreEqual(0, (await RowsAsync(team.Id)).Count);

        using var desktop = await Desktop.StartAsync(task.Id);
        await ReconcileAsync();
        var disk = (await RowsAsync(team.Id)).Single();
        Assert.AreEqual(StorageKind.Disk, disk.Kind);
        Assert.AreEqual(task.Id, disk.SourceId);
        // A standard desktop's disk, at its full size.
        Assert.AreEqual(64m, disk.Gb);
        Assert.IsNull(disk.EndedAt);

        // Run again, and again after the desktop is stopped: the disk is still kept, and it is the same row.
        await ReconcileAsync();
        await ada.PostAsync($"/api/tasks/{task.Id}/stop").IsAsync(HttpStatusCode.OK);
        await Stack.Jobs.StopAsync(task.Id, CancellationToken.None);
        await ReconcileAsync();
        var still = (await RowsAsync(team.Id)).Single();
        Assert.AreEqual(disk.Id, still.Id);
        Assert.AreEqual(disk.StartedAt, still.StartedAt);
        Assert.IsNull(still.EndedAt);

        // The team reads it: 64 GB kept now, and the month so far in GB-months. Kept for 73 hours, a tenth of a month, it is 6.4.
        var now = DateTimeOffset.UtcNow;
        await ChangeAsync(db => db.StorageRuns.Where(x => x.Id == disk.Id).ExecuteUpdateAsync(run => run.SetProperty(x => x.StartedAt, now.AddHours(-73))));
        var kept = (await UsageAsync(ada, team.Id)).Storage.Single();
        Assert.AreEqual(StorageKind.Disk, kept.Kind);
        Assert.AreEqual(64m, kept.Gb);
        Assert.AreEqual(64 * HoursThisMonth(73) / 730, kept.GbMonths, 0.01);

        // Deleted, the task keeps nothing: the row is closed, and stays closed.
        await ada.DeleteAsync($"/api/tasks/{task.Id}").IsAsync(HttpStatusCode.NoContent);
        await ReconcileAsync();
        var closed = (await RowsAsync(team.Id)).Single();
        Assert.IsNotNull(closed.EndedAt);
        await Stack.Jobs.DeleteAsync(task.Id, CancellationToken.None);
        await ReconcileAsync();
        Assert.AreEqual(closed.EndedAt, (await RowsAsync(team.Id)).Single().EndedAt);
        // The month still counts what was kept, and nothing is kept now.
        var after = (await UsageAsync(ada, team.Id)).Storage.Single();
        Assert.AreEqual(0m, after.Gb);
        Assert.IsTrue(after.GbMonths > 0 || HoursThisMonth(73) < 1);
    }

    [TestMethod]
    public async Task A_size_that_changed_closes_the_row_and_opens_another_at_the_same_moment()
    {
        var (ada, team, environment) = await TeamAsync();
        var task = await StartAsync(ada, team.Id, environment.Id);
        using var desktop = await Desktop.StartAsync(task.Id);
        await ReconcileAsync();

        // Standard desktops' disks are made larger: the 64 GB row ends where the 96 GB one begins.
        await ReconcileWith(standardDiskGb: 96).ReconcileAsync(CancellationToken.None);
        var rows = await RowsAsync(team.Id);
        Assert.AreEqual(2, rows.Count);
        Assert.AreEqual(64m, rows[0].Gb);
        Assert.AreEqual(96m, rows[1].Gb);
        Assert.AreEqual(rows[0].EndedAt, rows[1].StartedAt);
        Assert.IsNull(rows[1].EndedAt);
        // Nothing changed since: nothing is written.
        await ReconcileWith(standardDiskGb: 96).ReconcileAsync(CancellationToken.None);
        Assert.AreEqual(2, (await RowsAsync(team.Id)).Count);
        Assert.AreEqual(96m, (await UsageAsync(ada, team.Id)).Storage.Single().Gb);
        // Back to the settings every other journey uses.
        await ReconcileAsync();
        Assert.AreEqual(64m, (await RowsAsync(team.Id)).Single(x => x.EndedAt is null).Gb);
    }

    [TestMethod]
    public async Task An_archived_task_is_counted_at_what_is_on_its_archive_and_its_disk_no_longer()
    {
        var (ada, team, environment) = await TeamAsync();
        var task = await StartAsync(ada, team.Id, environment.Id);
        using var desktop = await Desktop.StartAsync(task.Id);
        await ada.PostAsync($"/api/tasks/{task.Id}/stop").IsAsync(HttpStatusCode.OK);
        await Stack.Jobs.StopAsync(task.Id, CancellationToken.None);
        await ReconcileAsync();

        // Archived: the machine and its disk are gone, and 12.5 GB of it is kept.
        await ChangeAsync(db => db.Tasks.Where(x => x.Id == task.Id).ExecuteUpdateAsync(row => row
            .SetProperty(x => x.Host, (string?)null).SetProperty(x => x.Archive, "wsa-journey").SetProperty(x => x.ArchiveBytes, (long?)(Gigabyte * 25 / 2))));
        await ReconcileAsync();
        var rows = await RowsAsync(team.Id);
        var disk = rows.Single(x => x.Kind == StorageKind.Disk);
        var archive = rows.Single(x => x.Kind == StorageKind.Archive);
        Assert.IsNotNull(disk.EndedAt);
        Assert.AreEqual(task.Id, archive.SourceId);
        Assert.AreEqual(12.5m, archive.Gb);
        Assert.IsNull(archive.EndedAt);
        // The team's usage lists both kinds this month, the disk first, with only the archive kept now.
        var usage = await UsageAsync(ada, team.Id);
        CollectionAssert.AreEqual(new[] { StorageKind.Disk, StorageKind.Archive }, usage.Storage.Select(x => x.Kind).ToArray());
        Assert.AreEqual(0m, usage.Storage[0].Gb);
        Assert.AreEqual(12.5m, usage.Storage[1].Gb);

        // An archive whose size is not known yet is kept at nothing, and counted once it is.
        await ChangeAsync(db => db.Tasks.Where(x => x.Id == task.Id).ExecuteUpdateAsync(row => row.SetProperty(x => x.ArchiveBytes, (long?)null)));
        await ReconcileAsync();
        Assert.AreEqual(0m, (await RowsAsync(team.Id)).Single(x => x.Kind == StorageKind.Archive && x.EndedAt is null).Gb);

        // Brought back: the archive is gone and the desktop has a disk again.
        await ChangeAsync(db => db.Tasks.Where(x => x.Id == task.Id).ExecuteUpdateAsync(row => row
            .SetProperty(x => x.Host, desktop.Host).SetProperty(x => x.Archive, (string?)null).SetProperty(x => x.ArchiveBytes, (long?)null)));
        await ReconcileAsync();
        var open = (await RowsAsync(team.Id)).Where(x => x.EndedAt is null).ToList();
        Assert.AreEqual(StorageKind.Disk, open.Single().Kind);
        Assert.AreNotEqual(disk.Id, open.Single().Id);
    }

    [TestMethod]
    public async Task A_saved_setup_is_counted_at_what_is_on_it_until_its_environment_is_deleted()
    {
        var (ada, team, _) = await TeamAsync();
        var added = await ada.PostAsync<EnvironmentDto>($"/api/teams/{team.Id}/environments",
            new AddEnvironment(Guid.NewGuid().ToString("D"), "Web app", ["chrome", "terminal"], DesktopSizes.Small, "https://github.com/acme/app", "main", "npm ci", null, null));
        await ReconcileAsync();
        Assert.AreEqual(0, (await RowsAsync(team.Id)).Count);

        // Prepared, with 10 GB on the saved disk.
        await PrepareAsync(added.Id, 10 * Gigabyte);
        await ReconcileAsync();
        var setup = (await RowsAsync(team.Id)).Single();
        Assert.AreEqual(StorageKind.Setup, setup.Kind);
        Assert.AreEqual(added.Id, setup.SourceId);
        Assert.AreEqual(10m, setup.Gb);
        Assert.AreEqual(StorageKind.Setup, (await UsageAsync(ada, team.Id)).Storage.Single().Kind);

        // Prepared again with more on it: the 10 GB row ends and a 14 GB one begins.
        await ada.PostAsync($"/api/environments/{added.Id}/setup").IsAsync(HttpStatusCode.OK);
        await PrepareAsync(added.Id, 14 * Gigabyte);
        await ReconcileAsync();
        var rows = await RowsAsync(team.Id);
        Assert.AreEqual(2, rows.Count);
        Assert.AreEqual(rows[0].EndedAt, rows[1].StartedAt);
        Assert.AreEqual(14m, rows[1].Gb);

        // The system administrator sees saved setups as a line of storage, at what they cost.
        using var administrator = await Person.SignInAsync(Stack.AdministratorEmail, Stack.AdministratorPassword);
        var line = (await administrator.GetAsync<AdminOverview>("/api/admin/overview")).Machines.Storage.Single(x => x.Kind == StorageKind.Setup);
        Assert.IsTrue(line is { Kept: >= 1, Gb: >= 14m, GbMonths: >= 0, Usd: >= 0 });

        // The environment is deleted, and its save with it: nothing is kept.
        await ada.DeleteAsync($"/api/environments/{added.Id}").IsAsync(HttpStatusCode.NoContent);
        await ReconcileAsync();
        Assert.IsTrue((await RowsAsync(team.Id)).All(x => x.EndedAt is not null));
    }

    [TestMethod]
    public async Task The_system_administrator_sees_what_storage_costs_by_kind_and_a_team_sees_no_dollars_for_it()
    {
        var (ada, team, environment) = await TeamAsync();
        var task = await StartAsync(ada, team.Id, environment.Id);
        using var desktop = await Desktop.StartAsync(task.Id);
        await ReconcileAsync();
        var now = DateTimeOffset.UtcNow;
        await ChangeAsync(db => db.StorageRuns.Where(x => x.TeamId == team.Id).ExecuteUpdateAsync(run => run.SetProperty(x => x.StartedAt, now.AddHours(-73))));

        using var administrator = await Person.SignInAsync(Stack.AdministratorEmail, Stack.AdministratorPassword);
        var disks = (await administrator.GetAsync<AdminOverview>("/api/admin/overview")).Machines.Storage.Single(x => x.Kind == StorageKind.Disk);
        // Every team's disks, this one's among them, at $0.14 a gigabyte a month.
        Assert.IsTrue(disks.Kept >= 1);
        Assert.IsTrue(disks.Gb >= 64m);
        Assert.IsTrue(disks.GbMonths >= 64 * HoursThisMonth(73) / 730 - 0.01);
        Assert.AreEqual(disks.GbMonths * 0.14, (double)disks.Usd, 0.006);

        // The team's own page has units only: the answer names no price for desktops or storage.
        var body = await (await ada.GetAsync($"/api/teams/{team.Id}/usage")).Content.ReadAsStringAsync();
        StringAssert.Contains(body, "\"gbMonths\"");
        // The only dollars in it are the models' two: what was spent and the allowance.
        Assert.AreEqual(2, System.Text.RegularExpressions.Regex.Matches(body, "usd", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count, body);

        // Nobody else reads the administrator's page.
        await ada.GetAsync("/api/admin/overview").IsAsync(HttpStatusCode.Forbidden);
    }

    /// <summary>The operator prepares an environment, and the preparing machine says the setup ended well with this much on its disk.</summary>
    static async Task PrepareAsync(string environmentId, long bytes)
    {
        await Stack.Jobs.PrepareAsync(environmentId, CancellationToken.None);
        using var preparing = Desktop.Preparing(environmentId);
        await preparing.ReportAsync("done", exit: 0, output: "added 312 packages", commit: "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678", bytes: bytes).IsAsync(HttpStatusCode.NoContent);
        string runId;
        using (var scope = Stack.Services.CreateScope())
        {
            runId = (await scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>().Setups.AsNoTracking().SingleAsync(x => x.EnvironmentId == environmentId)).RunId!;
        }
        await Stack.Jobs.SavePreparedAsync(environmentId, runId, CancellationToken.None);
    }
}

/// <summary>The arithmetic of storage, without a database: GB-months inside a window, and what a reconcile begins and ends.</summary>
[TestClass]
public sealed class StorageTests
{
    static readonly DateTimeOffset MonthStart = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Now = MonthStart.AddDays(20);

    static KeptStorage Disk(string task, decimal gb = 64, string team = "team-1") => new(team, StorageKind.Disk, task, gb);

    [TestMethod]
    public void A_gigabyte_kept_for_730_hours_is_one_gb_month()
    {
        Assert.AreEqual(1d, StorageRun.GbMonths([(1m, MonthStart, MonthStart.AddHours(730))], MonthStart, MonthStart.AddHours(730)), 1e-9);
        // 64 GB for a tenth of a month, and 128 GB for a quarter of one.
        Assert.AreEqual(6.4, StorageRun.GbMonths([(64m, Now.AddHours(-73), Now)], MonthStart, Now), 1e-9);
        Assert.AreEqual(6.4 + 32, StorageRun.GbMonths([(64m, Now.AddHours(-73), Now), (128m, Now.AddHours(-182.5), null)], MonthStart, Now), 1e-9);
        Assert.AreEqual(0d, StorageRun.GbMonths([], MonthStart, Now));
    }

    [TestMethod]
    public void Only_the_part_of_a_stretch_inside_the_window_counts()
    {
        // Began last month and still kept: counted from the month's start to now.
        Assert.AreEqual(100 * 480 / 730d, StorageRun.GbMonths([(100m, MonthStart.AddDays(-10), null)], MonthStart, Now), 1e-9);
        // Began last month and ended three days into this one.
        Assert.AreEqual(100 * 72 / 730d, StorageRun.GbMonths([(100m, MonthStart.AddDays(-10), MonthStart.AddDays(3))], MonthStart, Now), 1e-9);
        // Ended before the window, or begins after it: nothing.
        Assert.AreEqual(0d, StorageRun.GbMonths([(100m, MonthStart.AddDays(-10), MonthStart.AddDays(-1)), (100m, Now.AddDays(1), null)], MonthStart, Now));
    }

    [TestMethod]
    public void Bytes_are_gigabytes_to_a_thousandth()
    {
        Assert.AreEqual(10m, StorageRun.GbOf(10L * 1024 * 1024 * 1024));
        Assert.AreEqual(0.5m, StorageRun.GbOf(512L * 1024 * 1024));
        Assert.AreEqual(0.001m, StorageRun.GbOf(1024 * 1024));
        Assert.AreEqual(0m, StorageRun.GbOf(-5));
    }

    [TestMethod]
    public void A_reconcile_begins_what_is_kept_ends_what_is_gone_and_does_nothing_the_second_time()
    {
        var began = StorageRun.Reconcile([], [Disk("a"), Disk("b", 128)], Now);
        Assert.AreEqual(2, began.Count);
        Assert.IsTrue(began.All(x => x.StartedAt == Now && x.EndedAt is null && x.TeamId == "team-1"));

        // Nothing changed: nothing begins, nothing ends.
        Assert.AreEqual(0, StorageRun.Reconcile(began, [Disk("a"), Disk("b", 128)], Now.AddMinutes(15)).Count);
        Assert.IsTrue(began.All(x => x.EndedAt is null));

        // b is gone, and c is new.
        var later = Now.AddMinutes(30);
        var next = StorageRun.Reconcile(began, [Disk("a"), Disk("c")], later);
        Assert.AreEqual("c", next.Single().SourceId);
        Assert.IsNull(began.Single(x => x.SourceId == "a").EndedAt);
        Assert.AreEqual(later, began.Single(x => x.SourceId == "b").EndedAt);
    }

    [TestMethod]
    public void A_changed_size_ends_the_row_and_begins_another_at_the_same_moment()
    {
        var open = StorageRun.Reconcile([], [Disk("a", 64)], Now);
        var later = Now.AddHours(2);
        var next = StorageRun.Reconcile(open, [Disk("a", 128)], later);
        Assert.AreEqual(later, open.Single().EndedAt);
        Assert.AreEqual(128m, next.Single().Gb);
        Assert.AreEqual(later, next.Single().StartedAt);
        // Two hours at 64 GB and then one at 128: nothing counted twice, nothing lost.
        var rows = open.Concat(next).Select(x => (x.Gb, x.StartedAt, x.EndedAt));
        Assert.AreEqual((64 * 2 + 128 * 1) / 730d, StorageRun.GbMonths(rows, MonthStart, later.AddHours(1)), 1e-9);
    }

    [TestMethod]
    public void The_same_task_keeps_a_disk_and_an_archive_as_two_things_and_two_open_rows_for_one_become_one()
    {
        var kept = new[] { Disk("a"), new KeptStorage("team-1", StorageKind.Archive, "a", 3.2m) };
        var began = StorageRun.Reconcile([], kept, Now);
        CollectionAssert.AreEquivalent(new[] { StorageKind.Disk, StorageKind.Archive }, began.Select(x => x.Kind).ToArray());

        // Two rows open for one disk, which the job never writes: the older stays and the other is ended.
        var first = StorageRun.Begin(Disk("z"), Now.AddHours(-2));
        var second = StorageRun.Begin(Disk("z"), Now.AddHours(-1));
        Assert.AreEqual(0, StorageRun.Reconcile([second, first], [Disk("z")], Now).Count);
        Assert.IsNull(first.EndedAt);
        Assert.AreEqual(Now, second.EndedAt);
    }

    [TestMethod]
    public void Storage_is_priced_by_the_gigabyte_a_month_at_what_azure_charges_for_it()
    {
        var prices = new DesktopOptions().Prices;
        var options = new DesktopOptions();
        Assert.AreEqual(64, options.DiskGbOf(DesktopSizes.Small));
        Assert.AreEqual(64, options.DiskGbOf(DesktopSizes.Standard));
        Assert.AreEqual(128, options.DiskGbOf(DesktopSizes.Large));
        Assert.AreEqual(64, options.DiskGbOf("unknown"));
        // A disk is counted at what Azure charges for the 128 GiB one, $17.92 a month: $0.14 a gigabyte.
        Assert.AreEqual(8.96m, options.DiskGbOf(DesktopSizes.Standard) * prices.GbMonthOf(StorageKind.Disk));
        Assert.AreEqual(17.92m, options.DiskGbOf(DesktopSizes.Large) * prices.GbMonthOf(StorageKind.Disk));
        Assert.AreEqual(0.50m, prices.SaveMonthlyOf(10L * 1024 * 1024 * 1024));
        Assert.AreEqual(0m, prices.GbMonthOf("unknown"));
    }
}
