using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Workstream.Api.Desktops;
using Workstream.Api.Domain;

namespace Workstream.Tests;

/// <summary>
/// A saved setup on Azure itself: the part of it that is the Azure host's (<see cref="AzureDesktopHost"/>),
/// run against a real resource group. Nothing else proves that a disk can be snapshotted and a machine made
/// from the snapshot the way the host asks for them (Trusted Launch, the specialized image, each size's disk).
///
/// It runs only when it is told where: a resource group of its own to make machines in, a subnet for them,
/// and the gallery image version desktops are made from, which it only reads. Without those it is skipped.
///
///   WORKSTREAM_LIVE_AZURE_SUBSCRIPTION   the subscription's id
///   WORKSTREAM_LIVE_AZURE_GROUP          a resource group for this check alone, in the image's region
///   WORKSTREAM_LIVE_AZURE_SUBNET         the id of a subnet its machines join
///   WORKSTREAM_LIVE_AZURE_IMAGE          the id of the gallery image version, as Workstream:Desktops:Azure:ImageId
///   WORKSTREAM_LIVE_AZURE_LOCATION       the region (westus3 when not said)
///   WORKSTREAM_LIVE_AZURE_SIZE           small, standard or large (standard when not said)
///   WORKSTREAM_LIVE_AZURE_FILL_GB        gigabytes the "setup" writes besides its one file, as a real install does (none when not said)
///
/// It signs in as whoever `az login` is. It makes a machine from the image, writes a file on its disk as a
/// setup script would, stops it and saves its disk, makes a second machine from the save and reads the file
/// there, then removes the save and both machines, and says how long each step took. The file is written and
/// read with Azure's run command, since nothing but the API reaches a desktop's network.
/// </summary>
[TestClass]
public sealed class AzureSavedSetupLive
{
    const string ComputeApi = "2024-11-01";
    static readonly string[] Scopes = ["https://management.azure.com/.default"];

    public TestContext TestContext { get; set; } = null!;

    static string? Setting(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    [TestMethod]
    [Timeout(45 * 60_000)]
    public async Task A_machine_made_from_a_saved_setup_has_what_the_setup_left_on_the_disk()
    {
        var subscription = Setting("WORKSTREAM_LIVE_AZURE_SUBSCRIPTION");
        var group = Setting("WORKSTREAM_LIVE_AZURE_GROUP");
        var subnet = Setting("WORKSTREAM_LIVE_AZURE_SUBNET");
        var image = Setting("WORKSTREAM_LIVE_AZURE_IMAGE");
        if (subscription is null || group is null || subnet is null || image is null)
        {
            Assert.Inconclusive("Not told where to run: set WORKSTREAM_LIVE_AZURE_SUBSCRIPTION, _GROUP, _SUBNET and _IMAGE.");
            return;
        }
        var size = Setting("WORKSTREAM_LIVE_AZURE_SIZE") ?? DesktopSizes.Standard;
        var fillGb = int.TryParse(Setting("WORKSTREAM_LIVE_AZURE_FILL_GB"), out var gigabytes) ? Math.Max(0, gigabytes) : 0;
        var options = new DesktopOptions
        {
            Host = "azure",
            Azure = { SubscriptionId = subscription, ResourceGroup = group, Location = Setting("WORKSTREAM_LIVE_AZURE_LOCATION") ?? "westus3", SubnetId = subnet, ImageId = image },
        };
        var credential = new AzureCliCredential();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
        var host = new AzureDesktopHost(http, credential, options, NullLogger<AzureDesktopHost>.Instance);
        var arm = new Arm(http, credential, subscription, group);
        var ct = TestContext.CancellationTokenSource.Token;

        // Named as the jobs name them: a preparing machine after its run, a save after the same run, a task's machine after the task.
        var runId = Guid.NewGuid().ToString("D");
        var taskId = Guid.NewGuid().ToString("D");
        var preparing = new DesktopSpec(DesktopHosting.NameFor(runId), EnvironmentSetup.DesktopIdFor("live-check"), "live-check", size, "https://workstream.invalid", "live-check", MachinePurpose.Prepare);
        var save = EnvironmentSetup.SaveNameFor(runId);
        var task = new DesktopSpec(DesktopHosting.NameFor(taskId), taskId, "live-check", size, "https://workstream.invalid", "live-check", Save: save);
        var marker = Guid.NewGuid().ToString("N");
        var diskGb = options.Azure.DiskSizesGb[size];
        var times = new List<(string Step, TimeSpan Took)>();

        async Task<T> TimedAsync<T>(string step, Func<Task<T>> work)
        {
            var watch = Stopwatch.StartNew();
            var result = await work();
            times.Add((step, watch.Elapsed));
            Say($"{watch.Elapsed.TotalSeconds,7:0.0} s  {step}");
            return result;
        }
        Task DoAsync(string step, Func<Task> work) => TimedAsync(step, async () =>
        {
            await work();
            return true;
        });

        try
        {
            Say($"A {size} machine ({options.Azure.Sizes[size]}, {diskGb} GB) in {group}, from {image.Split('/')[^1]}.");
            await DoAsync("make a machine from the image (the host has taken the request, and it has an address)", () => host.CreateAsync(preparing, ct));
            await DoAsync("the machine is on", () => OnAsync(host, preparing.Name, ct));
            // What a setup script leaves: a file in the person's work folder, written to the disk. The answer
            // also says the disk grew to this size's disk and the desktop's own service came up.
            var wrote = await TimedAsync("its guest answers, and the file is written as a setup would", () => arm.RunAsync(preparing.Name,
                $"sudo -u work bash -c 'mkdir -p ~/work/project && echo {marker} > ~/work/project/marker' && sync && df -BG --output=size / | tail -1 && systemctl is-active workstream-desktop.service", ct));
            StringAssert.Contains(wrote, "active");
            Assert.IsTrue(DiskGbOf(wrote) >= diskGb - 3, $"The disk did not grow to {diskGb} GB: {wrote}");
            // A real setup leaves gigabytes: packages, images, a build. Written here as data that does not
            // compress, so the save and the disk made from it carry all of it.
            if (fillGb > 0)
            {
                var filled = await TimedAsync($"the setup writes {fillGb} GB more", () => arm.RunAsync(preparing.Name,
                    $"sudo -u work bash -c 'head -c {fillGb}G /dev/urandom > ~/work/project/fill' && sync && sha256sum /home/work/work/project/fill | cut -c1-64 > /home/work/work/project/fill.sum && cat /home/work/work/project/fill.sum && df -BG --output=used / | tail -1", ct));
                Say($"           on its disk afterwards: {filled.Split('\n', StringSplitOptions.TrimEntries).LastOrDefault(line => line.EndsWith('G'))}");
            }

            await DoAsync("stop the machine (deallocated)", () => host.StopAsync(preparing.Name, ct));
            await DoAsync("save its disk (a full snapshot)", () => host.SaveAsync(preparing.Name, save, ct));
            CollectionAssert.Contains((await host.ListSavesAsync(ct)).Select(x => x.Name).ToList(), save);
            // Saving again under the same name, as a job tried twice would, changes nothing.
            await DoAsync("save it again under the same name", () => host.SaveAsync(preparing.Name, save, ct));

            await DoAsync("make a second machine from the save (a disk from the snapshot, then the machine, and it has an address)", () => host.CreateAsync(task, ct));
            await DoAsync("the second machine is on", () => OnAsync(host, task.Name, ct));
            var read = await TimedAsync("its guest answers, and the file is there", () => arm.RunAsync(task.Name,
                "cat /home/work/work/project/marker && stat -c %U /home/work/work/project/marker && df -BG --output=size / | tail -1 && systemctl is-active workstream-desktop.service", ct));
            StringAssert.Contains(read, marker, "The second machine's disk does not have what the first one wrote.");
            StringAssert.Contains(read, "work");
            StringAssert.Contains(read, "active");
            Assert.IsTrue(DiskGbOf(read) >= diskGb - 3, $"The disk made from the save is not {diskGb} GB: {read}");
            // All of it is there and reads back the same. How long the first read takes is how fast a disk
            // made from a save hands over what is on it.
            if (fillGb > 0)
            {
                var same = await TimedAsync($"the second machine reads those {fillGb} GB for the first time, and they are the same", () => arm.RunAsync(task.Name,
                    "cd /home/work/work/project && [ \"$(sha256sum fill | cut -c1-64)\" = \"$(cat fill.sum)\" ] && echo the-same", ct));
                StringAssert.Contains(same, "the-same");
            }
            var names = (await host.ListAsync(ct)).Select(x => x.Name).ToList();
            CollectionAssert.Contains(names, preparing.Name);
            CollectionAssert.Contains(names, task.Name);

            // The machine made from the save does not hold on to it: the save can go while the machine runs.
            await DoAsync("remove the save", () => host.DeleteSaveAsync(save, ct));
            CollectionAssert.DoesNotContain((await host.ListSavesAsync(ct)).Select(x => x.Name).ToList(), save);
            await DoAsync("delete the second machine", () => host.DeleteAsync(task.Name, ct));
            await DoAsync("delete the first machine", () => host.DeleteAsync(preparing.Name, ct));

            // Nothing of either is left: no machine, no disk, no network interface, no snapshot.
            var left = await LeftAsync(arm, [preparing.Name, task.Name, save], ct);
            Assert.AreEqual(0, left.Count, $"Left behind: {string.Join(", ", left)}");
        }
        finally
        {
            // Whatever happened above, nothing is left running. Each of these is safe to ask again.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            foreach (var remove in new Func<Task>[] { () => host.DeleteSaveAsync(save, cleanup.Token), () => host.DeleteAsync(task.Name, cleanup.Token), () => host.DeleteAsync(preparing.Name, cleanup.Token) })
            {
                try
                {
                    await remove();
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    Say($"Cleaning up failed: {error.Message}");
                }
            }
            Say($"{times.Sum(x => x.Took.TotalSeconds),7:0.0} s  in all");
        }
    }

    void Say(string line) => TestContext.WriteLine(line);

    /// <summary>The size of the root file system in a `df -BG --output=size /` answer: the one line that is a number and a G.</summary>
    static int DiskGbOf(string output) =>
        output.Split('\n', StringSplitOptions.TrimEntries).Select(line => line.EndsWith('G') && int.TryParse(line[..^1], out var gb) ? gb : 0).Max();

    static async Task OnAsync(IDesktopHost host, string machine, CancellationToken ct)
    {
        var until = DateTimeOffset.UtcNow.AddMinutes(10);
        while (DateTimeOffset.UtcNow < until)
        {
            var state = await host.StateAsync(machine, ct);
            if (state.Failure is not null) Assert.Fail($"The machine {machine} could not be started: {state.Failure}");
            if (state.On) return;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        Assert.Fail($"The machine {machine} was not on after ten minutes.");
    }

    /// <summary>What is still in the group with any of these names in its own, asked a few times: Azure removes a machine's disk and interface a moment after the machine.</summary>
    static async Task<List<string>> LeftAsync(Arm arm, string[] names, CancellationToken ct)
    {
        var left = new List<string>();
        for (var attempt = 0; attempt < 12; attempt++)
        {
            left = [.. (await arm.ResourceNamesAsync(ct)).Where(resource => names.Any(resource.Contains))];
            if (left.Count == 0) break;
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
        return left;
    }

    /// <summary>The two things this check asks Azure itself, apart from the host: to run a command on a machine, and what a group holds.</summary>
    sealed class Arm(HttpClient http, TokenCredential credential, string subscription, string group)
    {
        string Group => $"https://management.azure.com/subscriptions/{subscription}/resourceGroups/{group}";

        async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, JsonNode? body, CancellationToken ct)
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext(Scopes), ct);
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            return await http.SendAsync(request, ct);
        }

        /// <summary>
        /// Run a shell script on the machine as root and answer what it wrote. Asked again until the machine's
        /// guest agent is there to take it, which is a little after the machine is on.
        /// </summary>
        public async Task<string> RunAsync(string machine, string script, CancellationToken ct)
        {
            var until = DateTimeOffset.UtcNow.AddMinutes(10);
            var last = "";
            while (DateTimeOffset.UtcNow < until)
            {
                using var asked = await SendAsync(HttpMethod.Post, $"{Group}/providers/Microsoft.Compute/virtualMachines/{machine}/runCommand?api-version={ComputeApi}",
                    new JsonObject { ["commandId"] = "RunShellScript", ["script"] = new JsonArray(script) }, ct);
                if (asked.StatusCode == HttpStatusCode.Accepted && (asked.Headers.Location ?? AsyncOperationOf(asked)) is { } operation)
                {
                    while (DateTimeOffset.UtcNow < until)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(3), ct);
                        using var answer = await SendAsync(HttpMethod.Get, operation.ToString(), null, ct);
                        if (answer.StatusCode == HttpStatusCode.Accepted) continue;
                        last = await answer.Content.ReadAsStringAsync(ct);
                        // The answer itself, or the operation's record with the answer inside it, by which address Azure gave.
                        var result = JsonNode.Parse(last);
                        var message = (result?["value"] ?? result?["properties"]?["output"]?["value"])?[0]?["message"]?.GetValue<string>();
                        if (answer.IsSuccessStatusCode && message is not null) return message;
                        if (result?["status"]?.GetValue<string>() is "InProgress" or "Running") continue;
                        break;
                    }
                }
                else
                {
                    last = await asked.Content.ReadAsStringAsync(ct);
                }
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
            throw new TimeoutException($"The machine {machine} did not run the command in ten minutes. Azure last said: {last}");
        }

        static Uri? AsyncOperationOf(HttpResponseMessage response) =>
            response.Headers.TryGetValues("Azure-AsyncOperation", out var values) && Uri.TryCreate(values.FirstOrDefault(), UriKind.Absolute, out var uri) ? uri : null;

        public async Task<List<string>> ResourceNamesAsync(CancellationToken ct)
        {
            using var response = await SendAsync(HttpMethod.Get, $"{Group}/resources?api-version=2021-04-01", null, ct);
            response.EnsureSuccessStatusCode();
            return [.. JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!["value"]!.AsArray().Select(resource => $"{resource!["type"]!.GetValue<string>()}/{resource["name"]!.GetValue<string>()}")];
        }
    }
}
