using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Workstream.Api.Desktops;
using Workstream.Api.Domain;

namespace Workstream.Tests;

/// <summary>
/// The Azure host against real Azure, with no deployed API: one machine made, asked about, stopped, started
/// again, archived, brought back from its archive and deleted, by <see cref="AzureDesktopHost"/> itself, in a
/// resource group made for trying things.
/// It runs only when it is told where, and is skipped otherwise:
///
/// <code>
/// WORKSTREAM_LIVE_AZURE_GROUP=workstream-scratch \
/// WORKSTREAM_LIVE_AZURE_SUBNET=/subscriptions/…/virtualNetworks/net/subnets/test \
/// WORKSTREAM_LIVE_AZURE_IMAGE=/subscriptions/…/galleries/…/images/…/versions/1.2.3 \
/// WORKSTREAM_LIVE_AZURE_HIBERNATE=true WORKSTREAM_LIVE_AZURE_SIZE=Standard_D4as_v6 \
/// dotnet test tests/Workstream.Tests --filter AzureDesktopHostLive
/// </code>
///
/// It signs in as <c>az login</c> does, in the subnet's subscription; the location is West US 3 unless
/// <c>WORKSTREAM_LIVE_AZURE_LOCATION</c> names another. The machine has no public address, so its display is
/// not asked from here. It costs a few cents and leaves nothing behind: the machine is deleted whether or not
/// a step failed, and so is its archive.
///
/// The machine is told an API nothing answers at, unless <c>WORKSTREAM_LIVE_AZURE_API</c> names one its
/// network reaches (a stand-in that logs the agent's exchanges, say). <c>WORKSTREAM_LIVE_AZURE_HOLD</c> is a
/// number of seconds to leave the machine brought back from its archive running before it is deleted, for
/// someone inside its network to look at it.
///
/// Each step is one line in <see cref="Steps"/>, timed and written to the test's output; a step for another
/// call on the host goes in the same list.
/// </summary>
[TestClass]
public sealed class AzureDesktopHostLive
{
    public TestContext TestContext { get; set; } = null!;

    static string? Told(string name) => Environment.GetEnvironmentVariable("WORKSTREAM_LIVE_AZURE_" + name) is { Length: > 0 } value ? value : null;

    [TestMethod]
    [Timeout(30 * 60 * 1000)]
    public async Task A_machine_is_made_stopped_started_again_archived_brought_back_and_deleted()
    {
        var group = Told("GROUP");
        if (group is null) Assert.Inconclusive("Not told where: set WORKSTREAM_LIVE_AZURE_GROUP, _SUBNET and _IMAGE to run this against Azure.");
        var credential = new DefaultAzureCredential();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
        var subnet = Told("SUBNET") ?? throw new InvalidOperationException("WORKSTREAM_LIVE_AZURE_SUBNET is not set.");
        var options = new DesktopOptions
        {
            Azure =
            {
                // The machine is made in the subnet's subscription: /subscriptions/<id>/resourceGroups/…
                SubscriptionId = subnet.Split('/', StringSplitOptions.RemoveEmptyEntries)[1],
                ResourceGroup = group!,
                Location = Told("LOCATION") ?? "westus3",
                SubnetId = subnet,
                ImageId = Told("IMAGE") ?? throw new InvalidOperationException("WORKSTREAM_LIVE_AZURE_IMAGE is not set."),
                Hibernate = bool.TryParse(Told("HIBERNATE"), out var hibernate) && hibernate,
            },
        };
        if (Told("SIZE") is { } size) options.Azure.Sizes[DesktopSizes.Standard] = size;
        using var logs = LoggerFactory.Create(builder => builder.AddSimpleConsole());
        var host = new AzureDesktopHost(http, credential, options, logs.CreateLogger<AzureDesktopHost>());

        var id = Guid.NewGuid().ToString();
        var spec = new DesktopSpec(DesktopHosting.NameFor(id), id, "live-check", DesktopSizes.Standard, Told("API") ?? "http://127.0.0.1:9", "live-check");
        var archive = DesktopHosting.NewArchiveNameFor(id);
        TestContext.WriteLine($"{spec.Name}: {options.Azure.Sizes[DesktopSizes.Standard]} in {group}, hibernate {options.Azure.Hibernate}.");
        try
        {
            foreach (var (what, step) in Steps(host, spec, archive, options, credential, http))
            {
                var watch = Stopwatch.StartNew();
                await step();
                TestContext.WriteLine($"{watch.Elapsed.TotalSeconds,6:0.0}s  {what}");
            }
        }
        finally
        {
            await host.DeleteAsync(spec.Name, CancellationToken.None);
            await host.DeleteArchiveAsync(archive, CancellationToken.None);
        }
        Assert.IsFalse((await host.ListAsync(CancellationToken.None)).Any(machine => machine.Name == spec.Name), "The machine is still there after it was deleted.");
        Assert.IsFalse((await host.ListArchivesAsync(CancellationToken.None)).Any(kept => kept.Name == archive), "The archive is still there after it was deleted.");
    }

    /// <summary>What is done to the machine, in order. A step that fails ends the run, and the machine is deleted.</summary>
    static IEnumerable<(string What, Func<Task> Step)> Steps(AzureDesktopHost host, DesktopSpec spec, string archive, DesktopOptions options, TokenCredential credential, HttpClient http)
    {
        var ct = CancellationToken.None;
        yield return ("made: Azure took the request and the machine has an address", async () =>
        {
            var place = await host.CreateAsync(spec, ct);
            Assert.AreEqual(spec.Name, place.Host);
            StringAssert.EndsWith(place.Address, ":" + options.DisplayPort);
        });
        yield return ("on", () => OnAsync(host, spec.Name));
        yield return ("listed", async () => Assert.IsTrue((await host.ListAsync(ct)).Any(machine => machine.Name == spec.Name)));
        yield return (options.Azure.Hibernate ? "stopped: hibernated" : "stopped: released", async () =>
        {
            // On a machine with no display a person could have used, a moment for it to finish booting first.
            await Task.Delay(TimeSpan.FromSeconds(45), ct);
            await host.StopAsync(spec.Name, ct);
            Assert.IsFalse((await host.StateAsync(spec.Name, ct)).On);
            var statuses = await StatusesAsync(options, spec.Name, credential, http);
            CollectionAssert.Contains(statuses, "PowerState/deallocated");
            // Where the deployment hibernates, the memory must really be on the disk: a plain stop here means the fallback ran.
            if (options.Azure.Hibernate) CollectionAssert.Contains(statuses, "HibernationState/Hibernated", "Azure released the machine without hibernating it: " + string.Join(", ", statuses));
        });
        yield return ("stopped again: nothing to do", () => host.StopAsync(spec.Name, ct));
        yield return ("started again: Azure took the request", async () => Assert.AreEqual(spec.Name, (await host.StartAsync(spec.Name, ct)).Host));
        yield return ("on again", () => OnAsync(host, spec.Name));

        // Archived as the sweep does it (DesktopJobs.ArchiveAsync): stopped, its disk copied, then the machine and
        // its disk deleted. Where the deployment hibernates, the copy holds the machine's memory.
        yield return ("stopped, to be archived", async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(45), ct);
            await host.StopAsync(spec.Name, ct);
        });
        yield return ("archived: its disk copied", async () =>
        {
            var bytes = await host.ArchiveAsync(spec.Name, archive, ct);
            Assert.IsTrue(bytes > 0, "Azure did not say how large the archive is.");
            Assert.IsTrue((await host.ListArchivesAsync(ct)).Any(kept => kept.Name == archive));
        });
        yield return ("its machine and disk deleted", () => host.DeleteAsync(spec.Name, ct));
        // Brought back as a start does it (DesktopJobs.StartAsync): a new machine of the same name, on a disk made from the archive.
        yield return ("brought back: Azure took the request for a machine on a copy of the archive", async () =>
            Assert.AreEqual(spec.Name, (await host.CreateAsync(spec with { Archive = archive }, ct)).Host));
        yield return ("on, from the archive", () => OnAsync(host, spec.Name));
        if (int.TryParse(Told("HOLD"), out var hold) && hold > 0) yield return ($"left running {hold} seconds to be looked at", () => Task.Delay(TimeSpan.FromSeconds(hold), ct));
    }

    /// <summary>Ask the host every two seconds, as the job does, until the machine is on.</summary>
    static async Task OnAsync(AzureDesktopHost host, string name)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(10);
        while (true)
        {
            var state = await host.StateAsync(name, CancellationToken.None);
            if (state.Failure is not null) Assert.Fail(state.Failure);
            if (state.On) return;
            if (DateTimeOffset.UtcNow > deadline) Assert.Fail("The machine was not on after ten minutes.");
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>The machine's status codes, read from Azure and not through the host: what the host's stop is checked against.</summary>
    static async Task<string[]> StatusesAsync(DesktopOptions options, string name, TokenCredential credential, HttpClient http)
    {
        var url = $"https://management.azure.com/subscriptions/{options.Azure.SubscriptionId}/resourceGroups/{options.Azure.ResourceGroup}/providers/Microsoft.Compute/virtualMachines/{name}/instanceView?api-version=2024-11-01";
        var view = await GetAsync(url, credential, http);
        return view["statuses"]!.AsArray().Select(status => status!["code"]!.GetValue<string>()).ToArray();
    }

    static async Task<JsonNode> GetAsync(string url, TokenCredential credential, HttpClient http)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]), CancellationToken.None);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }
}
