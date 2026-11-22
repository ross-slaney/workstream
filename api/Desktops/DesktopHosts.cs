using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using Workstream.Api.Domain;

namespace Workstream.Api.Desktops;

/// <summary>
/// What a machine is made with. The key and the API's address are the only two things it is told; it asks the
/// API for the rest. <see cref="DesktopId"/> is the task's id for a machine made for a task and the spare's own
/// for a spare; <see cref="Purpose"/> is one of <see cref="MachinePurpose"/>, and is written on the machine so
/// a person looking at the host can tell them apart. <see cref="Save"/> names the saved setup the machine
/// starts from, when its environment has one; without it the machine starts from the image.
/// <see cref="Archive"/> names the task's own archive, for a machine that brings an archived desktop back: it
/// starts from that and from nothing else.
/// </summary>
public sealed record DesktopSpec(string Name, string DesktopId, string TeamId, string Size, string ApiUrl, string Key, string Purpose = MachinePurpose.Task, string? Save = null, string? Archive = null)
{
    /// <summary>
    /// What the desktop is told, as its agent reads it (desktop/agent/told.mjs): on a machine this is its user
    /// data, which only the machine itself can read and which is never written to its disk.
    /// </summary>
    public string Told => $"WORKSTREAM_API={ApiUrl}\nWORKSTREAM_KEY={Key}\n";
}

/// <summary>Where a desktop is: its name at the host, and where the API reaches its display (<c>host:port</c>).</summary>
public sealed record DesktopPlace(string Host, string Address);

/// <summary>A machine, or a saved setup, the host holds, and when it was made.</summary>
public sealed record HostedMachine(string Name, DateTimeOffset CreatedAt);

/// <summary>How a machine that was asked to start is doing: on yet or not, or failed, and then why in the host's words.</summary>
public sealed record MachineState(bool On, string? Failure = null);

/// <summary>
/// Whatever runs the desktops: Azure virtual machines when deployed, Docker containers in development. The jobs
/// (<see cref="DesktopJobs"/>) are its only caller. Every method is safe to call again.
///
/// Making or starting a machine answers as soon as the host has taken the request and the machine has an
/// address, long before it is on. The job then follows it with <see cref="StateAsync"/> and
/// <see cref="DisplayAnswersAsync"/>, so each step of a start is known when it happens.
/// </summary>
public interface IDesktopHost
{
    Task<DesktopPlace> CreateAsync(DesktopSpec spec, CancellationToken ct);
    /// <summary>Start a stopped machine again. Its address may have changed.</summary>
    Task<DesktopPlace> StartAsync(string host, CancellationToken ct);
    Task<MachineState> StateAsync(string host, CancellationToken ct);
    /// <summary>Whether the machine's display accepts a connection now: the first moment there is a screen to watch.</summary>
    Task<bool> DisplayAnswersAsync(DesktopPlace place, CancellationToken ct);
    /// <summary>Release the machine but keep its disk.</summary>
    Task StopAsync(string host, CancellationToken ct);
    /// <summary>Remove the machine and everything made with it. A machine that is not there is already deleted.</summary>
    Task DeleteAsync(string host, CancellationToken ct);
    /// <summary>Every machine this host holds: what the sweep compares with the tasks to find strays.</summary>
    Task<IReadOnlyList<HostedMachine>> ListAsync(CancellationToken ct);

    /// <summary>
    /// Keep a stopped machine's disk as a saved setup under this name: what a machine made with
    /// <see cref="DesktopSpec.Save"/> starts from. The machine is left as it was.
    /// </summary>
    Task SaveAsync(string host, string name, CancellationToken ct);
    /// <summary>Remove a saved setup. One that is not there is already removed.</summary>
    Task DeleteSaveAsync(string name, CancellationToken ct);
    /// <summary>Every saved setup this host holds: what the sweep compares with the environments to find strays.</summary>
    Task<IReadOnlyList<HostedMachine>> ListSavesAsync(CancellationToken ct);

    /// <summary>
    /// Copy a stopped machine's disk to an archive under this name: what a machine made with
    /// <see cref="DesktopSpec.Archive"/> starts from. Answers how many bytes the host says the archive holds,
    /// when it knows. The machine is left as it was; whoever archives deletes it once the archive is written
    /// down, so a disk is never without an owner.
    /// </summary>
    Task<long?> ArchiveAsync(string host, string name, CancellationToken ct);
    /// <summary>Remove an archive. One that is not there is already removed.</summary>
    Task DeleteArchiveAsync(string name, CancellationToken ct);
    /// <summary>Every archive this host holds: what the sweep compares with the tasks to find strays.</summary>
    Task<IReadOnlyList<HostedMachine>> ListArchivesAsync(CancellationToken ct);
}

/// <summary>A desktop's display, as the API reaches it.</summary>
public static class Display
{
    /// <summary>
    /// Whether a display is there now: it takes the connection and greets, as a remote-desktop server does
    /// before it is asked anything (<c>RFB 003.008</c>). Taking the connection alone proves nothing: Docker's
    /// published port takes one before anything behind it is listening.
    /// </summary>
    public static async Task<bool> AnswersAsync(string address, CancellationToken ct)
    {
        var separator = address.LastIndexOf(':');
        using var client = new TcpClient();
        // Long enough that a display busy starting is heard out rather than hung up on.
        using var trying = CancellationTokenSource.CreateLinkedTokenSource(ct);
        trying.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await client.ConnectAsync(address[..separator], int.Parse(address[(separator + 1)..]), trying.Token);
            var greeting = new byte[4];
            var read = 0;
            while (read < greeting.Length)
            {
                var more = await client.GetStream().ReadAsync(greeting.AsMemory(read), trying.Token);
                if (more == 0) return false;
                read += more;
            }
            return greeting.AsSpan().SequenceEqual("RFB "u8);
        }
        catch (Exception error) when (error is SocketException or IOException || error is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }
}

/// <summary>Settings for the desktops (<c>Workstream:Desktops</c>).</summary>
public sealed class DesktopOptions
{
    /// <summary><c>azure</c>, <c>docker</c> or <c>none</c> (a task then fails to start, with words that say why).</summary>
    public string Host { get; set; } = "none";
    /// <summary>The API's address as a desktop reaches it. PUBLIC_ORIGIN when deployed; the Docker host's name in development.</summary>
    public string ApiUrl { get; set; } = "";
    /// <summary>
    /// What a new team starts with: a desktop nobody has used for this many minutes, whose agent is not
    /// working, is paused. Each team's administrators change their own (<see cref="Team.PauseAfterMinutes"/>).
    /// </summary>
    public int IdleMinutes { get; set; } = Team.DefaultPauseAfterMinutes;
    /// <summary>What a new team starts with: a desktop paused for this many days is archived (<see cref="Team.ArchiveAfterDays"/>).</summary>
    public int ArchiveDays { get; set; } = Team.DefaultArchiveAfterDays;
    /// <summary>A desktop that has not exchanged with the API this long after being asked to start has failed.</summary>
    public int StartTimeoutMinutes { get; set; } = 15;
    /// <summary>The port a desktop's display listens on.</summary>
    public int DisplayPort { get; set; } = 5900;
    public AzureOptions Azure { get; set; } = new();
    public DockerOptions Docker { get; set; } = new();

    public sealed class AzureOptions
    {
        public string SubscriptionId { get; set; } = "";
        /// <summary>The resource group that holds the machines and nothing else, so a stray is easy to see.</summary>
        public string ResourceGroup { get; set; } = "";
        public string Location { get; set; } = "";
        /// <summary>The subnet desktops join. Its rules admit the API and nothing else (infra/desktops.bicep).</summary>
        public string SubnetId { get; set; } = "";
        /// <summary>
        /// The image a desktop is made from: one version of the gallery's image, the one this release was
        /// deployed with (infra/main.bicep). A machine made from it starts as it is, with nothing to provision.
        /// </summary>
        public string ImageId { get; set; } = "";
        public Dictionary<string, string> Sizes { get; set; } = new()
        {
            [DesktopSizes.Small] = "Standard_D2as_v7",
            [DesktopSizes.Standard] = "Standard_D4as_v7",
            [DesktopSizes.Large] = "Standard_D8as_v7",
        };
        /// <summary>
        /// Each size's disk, in GiB: at least the image's 64. A large desktop is for a whole project's stack (its
        /// containers, packages and builds), so it has twice the room. A machine grows its file system to its disk
        /// when it boots (desktop/image/configure.sh). Priced by the gigabyte in <see cref="PriceOptions.StorageGbMonthUsd"/>.
        /// </summary>
        public Dictionary<string, int> DiskSizesGb { get; set; } = new()
        {
            [DesktopSizes.Small] = 64,
            [DesktopSizes.Standard] = 64,
            [DesktopSizes.Large] = 128,
        };
    }

    public sealed class DockerOptions
    {
        public string Image { get; set; } = "workstream-desktop:local";
    }

    public SpareOptions Spares { get; set; } = new();

    public SaveOptions Saves { get; set; } = new();

    /// <summary>
    /// Saved setups: an environment's setup done once on a machine of its own, whose disk tasks then start
    /// from (<see cref="EnvironmentSetup"/>). A preparing machine runs for nobody's task and a save is kept on
    /// storage, so every number here is a limit on what those can cost.
    /// </summary>
    public sealed class SaveOptions
    {
        public bool Enabled { get; set; } = true;
        /// <summary>Machines preparing at once, over every team.</summary>
        public int AtOnce { get; set; } = 1;
        /// <summary>A setup that has not ended this long after its machine was asked for has failed.</summary>
        public int MaxMinutes { get; set; } = 45;
        /// <summary>A save older than this is made again when a task next starts from it. The task itself uses the old one.</summary>
        public int RefreshDays { get; set; } = 7;
        /// <summary>A save nothing has started from for this long is removed, and made again on next use.</summary>
        public int UnusedDays { get; set; } = 30;
        /// <summary>Hours of preparing in any 24 hours, over every team. Past it, what is wanted waits.</summary>
        public double HoursPerDay { get; set; } = 4;
    }

    /// <summary>
    /// What a saved setup is only good for: desktops of this version of the image. On Azure it is the version
    /// this deployment was made with; locally it is the image's name, which does not change when the image is
    /// built again, so a local save is prepared again by hand.
    /// </summary>
    public string ImageVersion => Host.Equals("azure", StringComparison.OrdinalIgnoreCase) ? Azure.ImageId.Split('/')[^1] : Docker.Image;

    /// <summary>
    /// Spares: a desktop kept running for a team while someone is in its workspace, so their next task starts
    /// at once (<see cref="Spare"/>). Each is a running machine nobody is using yet, so every number here is a
    /// limit on what that can cost.
    /// </summary>
    public sealed class SpareOptions
    {
        /// <summary>Off unless a deployment turns it on: no spare machine is ever made by default.</summary>
        public bool Enabled { get; set; }
        /// <summary>Spares running at once, over every team. A team has one at most.</summary>
        public int Max { get; set; } = 2;
        /// <summary>A spare nobody on the team has wanted for this long is removed.</summary>
        public int IdleMinutes { get; set; } = 15;
        /// <summary>Hours of spare time in any 24 hours, over every team. Past it no spare is made until some has aged out.</summary>
        public double HoursPerDay { get; set; } = 6;
    }

    /// <summary>The disk a desktop of this size is made with, in gigabytes: what its storage is counted at.</summary>
    public int DiskGbOf(string size) => Azure.DiskSizesGb.GetValueOrDefault(size, Azure.DiskSizesGb.GetValueOrDefault(DesktopSizes.Standard, 64));

    /// <summary>
    /// What Workstream pays for the desktops: Azure's list prices in West US 3, in dollars, in one place. A
    /// running machine by the hour and by size, and storage by the gigabyte a month and by kind. What the
    /// desktops cost is the runs' hours (<see cref="DesktopRun"/>) and the storage's GB-months
    /// (<see cref="StorageRun"/>) times these. They are costs, not what a customer is charged: nothing here
    /// is shown to a team.
    /// </summary>
    public PriceOptions Prices { get; set; } = new();

    public sealed class PriceOptions
    {
        public Dictionary<string, decimal> HourlyUsd { get; set; } = new()
        {
            [DesktopSizes.Small] = 0.091m,
            [DesktopSizes.Standard] = 0.182m,
            [DesktopSizes.Large] = 0.364m,
        };
        /// <summary>
        /// A gigabyte kept for a month, by <see cref="StorageKind"/>. A desktop's disk is a Standard SSD, $4.80
        /// a month for 64 GiB (E6) and $9.60 for 128 GiB (E10), which is $0.075 a gigabyte whichever size it is.
        /// An archived task and a saved setup are full snapshots on standard storage.
        /// </summary>
        public Dictionary<string, decimal> StorageGbMonthUsd { get; set; } = new()
        {
            [StorageKind.Disk] = 0.075m,
            [StorageKind.Archive] = 0.05m,
            [StorageKind.Setup] = 0.05m,
        };

        public decimal HourOf(string size) => HourlyUsd.GetValueOrDefault(size, HourlyUsd.GetValueOrDefault(DesktopSizes.Standard));

        public decimal GbMonthOf(string kind) => StorageGbMonthUsd.GetValueOrDefault(kind);

        /// <summary>What a saved setup with this much on it costs to keep for a month.</summary>
        public decimal SaveMonthlyOf(long bytes) => Math.Round(StorageRun.GbOf(bytes) * GbMonthOf(StorageKind.Setup), 2);
    }
}

public static class DesktopHosting
{
    public static WebApplicationBuilder AddDesktops(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection("Workstream:Desktops").Get<DesktopOptions>() ?? new DesktopOptions();
        if (string.IsNullOrWhiteSpace(options.ApiUrl)) options.ApiUrl = WorkstreamApp.From(builder.Configuration).Origin;
        builder.Services.AddSingleton(options);
        switch (options.Host.ToLowerInvariant())
        {
            case "azure":
                builder.Services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
                builder.Services.AddHttpClient<IDesktopHost, AzureDesktopHost>(client => client.Timeout = TimeSpan.FromSeconds(100));
                break;
            case "docker":
                builder.Services.AddSingleton<IDesktopHost, DockerDesktopHost>();
                break;
            default:
                builder.Services.AddSingleton<IDesktopHost, NoDesktopHost>();
                break;
        }
        return builder;
    }

    /// <summary>
    /// The machine's name for a desktop, from the task's id or the spare's: short, and the same every time, so
    /// a retry finds what an earlier try made.
    /// </summary>
    public static string NameFor(string desktopId) => "ws-" + Guid.Parse(desktopId).ToString("N")[..16];

    /// <summary>What every archive's name starts with, which tells an archive from a saved setup at the host.</summary>
    public const string ArchivePrefix = "wsa-";

    /// <summary>
    /// A name for a new archive of a task's disk: the task's, and different each time, so an archive that was
    /// made and never written down is nobody's, and the sweep removes it.
    /// </summary>
    public static string NewArchiveNameFor(string taskId) => $"{ArchivePrefix}{Guid.Parse(taskId).ToString("N")[..16]}-{Guid.NewGuid().ToString("N")[..8]}";
}

/// <summary>An environment with no way to run desktops: the tests' default, and a deployment that forgot to configure one.</summary>
public sealed class NoDesktopHost : IDesktopHost
{
    const string Words = "This environment has no desktop host. Set Workstream:Desktops:Host to azure or docker.";
    public Task<DesktopPlace> CreateAsync(DesktopSpec spec, CancellationToken ct) => throw new InvalidOperationException(Words);
    public Task<DesktopPlace> StartAsync(string host, CancellationToken ct) => throw new InvalidOperationException(Words);
    public Task<MachineState> StateAsync(string host, CancellationToken ct) => Task.FromResult(new MachineState(false, Words));
    public Task<bool> DisplayAnswersAsync(DesktopPlace place, CancellationToken ct) => Task.FromResult(false);
    public Task StopAsync(string host, CancellationToken ct) => Task.CompletedTask;
    public Task DeleteAsync(string host, CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<HostedMachine>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<HostedMachine>>([]);
    public Task SaveAsync(string host, string name, CancellationToken ct) => throw new InvalidOperationException(Words);
    public Task DeleteSaveAsync(string name, CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<HostedMachine>> ListSavesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<HostedMachine>>([]);
    public Task<long?> ArchiveAsync(string host, string name, CancellationToken ct) => throw new InvalidOperationException(Words);
    public Task DeleteArchiveAsync(string name, CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<HostedMachine>> ListArchivesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<HostedMachine>>([]);
}

/// <summary>
/// Desktops as Azure virtual machines, through Resource Manager's REST API as this app's managed identity.
/// A machine is one request to make and one to delete: its network interface and disk are declared inside it
/// with <c>deleteOption: Delete</c>, so nothing outlives it. It has no public address; it reaches the internet
/// through the subnet's NAT gateway and is reached only by the API, on the private network.
///
/// A machine is made from a specialized image (desktop/image/seal.sh): it has no operating-system profile and
/// nothing provisions it, so it boots straight into the desktop. What it is told is its user data.
///
/// Nothing here waits on Azure's own record of an operation. Azure allows few questions about operations
/// across the whole subscription, and it calls a start finished long after the machine is up. A machine is
/// asked about instead (<see cref="StateAsync"/>), which is counted per machine.
/// </summary>
public sealed class AzureDesktopHost(HttpClient http, TokenCredential credential, DesktopOptions options, ILogger<AzureDesktopHost> log) : IDesktopHost
{
    const string ComputeApi = "2024-11-01";
    const string DiskApi = "2024-03-02";
    const string NetworkApi = "2024-05-01";
    const string DiskType = "StandardSSD_LRS";
    static readonly string[] Scopes = ["https://management.azure.com/.default"];

    DesktopOptions.AzureOptions Azure => options.Azure;
    string Group => $"https://management.azure.com/subscriptions/{Azure.SubscriptionId}/resourceGroups/{Azure.ResourceGroup}";
    string Machine(string name) => $"{Group}/providers/Microsoft.Compute/virtualMachines/{name}";
    string Snapshot(string name) => $"{Group}/providers/Microsoft.Compute/snapshots/{name}";
    /// <summary>The disk made for a machine that starts from a saved setup. A machine from the image is given its disk by Azure.</summary>
    string DiskOf(string machine) => $"{Group}/providers/Microsoft.Compute/disks/{machine}-os";
    static string IdOf(string url) => url["https://management.azure.com".Length..];

    public async Task<DesktopPlace> CreateAsync(DesktopSpec spec, CancellationToken ct)
    {
        if (!Azure.Sizes.TryGetValue(spec.Size, out var vmSize)) throw new InvalidOperationException($"No machine size is configured for '{spec.Size}'.");
        // The snapshot the machine's disk is made from: the task's own archive, or its environment's saved setup.
        var source = spec.Archive ?? spec.Save;
        var body = new JsonObject
        {
            ["location"] = Azure.Location,
            ["tags"] = new JsonObject { ["project"] = "workstream", ["desktop"] = spec.DesktopId, ["team"] = spec.TeamId, ["purpose"] = spec.Purpose },
            ["properties"] = new JsonObject
            {
                ["hardwareProfile"] = new JsonObject { ["vmSize"] = vmSize },
                ["storageProfile"] = source is null
                    ? new JsonObject
                    {
                        ["imageReference"] = new JsonObject { ["id"] = Azure.ImageId },
                        ["osDisk"] = new JsonObject
                        {
                            ["createOption"] = "FromImage",
                            ["deleteOption"] = "Delete",
                            // Each size's own disk (DiskSizesGb), at least the image's; the machine grows into it (desktop/image/grow-disk.sh).
                            ["diskSizeGB"] = Azure.DiskSizesGb.GetValueOrDefault(spec.Size, 64),
                            ["managedDisk"] = new JsonObject { ["storageAccountType"] = DiskType },
                        },
                    }
                    // From a saved setup or the task's archive: a disk made from it first (about two seconds), which the machine takes as its own.
                    : new JsonObject
                    {
                        ["osDisk"] = new JsonObject
                        {
                            ["createOption"] = "Attach",
                            ["osType"] = "Linux",
                            ["deleteOption"] = "Delete",
                            ["managedDisk"] = new JsonObject { ["id"] = IdOf(DiskOf(spec.Name)) },
                        },
                    },
                // The only two things a desktop is told. It reads the rest from the API with the key (desktop/agent/agent.mjs).
                ["userData"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(spec.Told)),
                ["securityProfile"] = new JsonObject
                {
                    ["securityType"] = "TrustedLaunch",
                    ["uefiSettings"] = new JsonObject { ["secureBootEnabled"] = true, ["vTpmEnabled"] = true },
                },
                ["networkProfile"] = new JsonObject
                {
                    ["networkApiVersion"] = "2022-11-01",
                    ["networkInterfaceConfigurations"] = new JsonArray(new JsonObject
                    {
                        ["name"] = NicName(spec.Name),
                        ["properties"] = new JsonObject
                        {
                            ["primary"] = true,
                            ["deleteOption"] = "Delete",
                            ["enableAcceleratedNetworking"] = true,
                            ["ipConfigurations"] = new JsonArray(new JsonObject
                            {
                                ["name"] = "ip",
                                ["properties"] = new JsonObject { ["subnet"] = new JsonObject { ["id"] = Azure.SubnetId } },
                            }),
                        },
                    }),
                },
            },
        };
        if (source is not null) await DiskFromSaveAsync(spec, source, ct);
        try
        {
            (await SendAsync(HttpMethod.Put, $"{Machine(spec.Name)}?api-version={ComputeApi}", body, ct)).Dispose();
        }
        catch when (source is not null)
        {
            // Azure refused the machine: the disk made for it would be nobody's.
            (await SendAsync(HttpMethod.Delete, $"{DiskOf(spec.Name)}?api-version={DiskApi}", null, CancellationToken.None, missingIsDone: true)).Dispose();
            throw;
        }
        return new DesktopPlace(spec.Name, await AddressAsync(spec.Name, ct));
    }

    /// <summary>
    /// A disk made from a saved setup or an archive, for the machine that is about to take it. It carries the
    /// snapshot's security type with it, and is the snapshot's size, which for an archive is the size the
    /// task's disk was.
    /// </summary>
    async Task DiskFromSaveAsync(DesktopSpec spec, string snapshot, CancellationToken ct)
    {
        var disk = new JsonObject
        {
            ["location"] = Azure.Location,
            ["tags"] = new JsonObject { ["project"] = "workstream", ["desktop"] = spec.DesktopId },
            ["sku"] = new JsonObject { ["name"] = DiskType },
            ["properties"] = new JsonObject
            {
                ["creationData"] = new JsonObject { ["createOption"] = "Copy", ["sourceResourceId"] = IdOf(Snapshot(snapshot)) },
            },
        };
        (await SendAsync(HttpMethod.Put, $"{DiskOf(spec.Name)}?api-version={DiskApi}", disk, ct)).Dispose();
        await MadeAsync($"{DiskOf(spec.Name)}?api-version={DiskApi}", $"The disk for the machine {spec.Name}", ct);
    }

    /// <summary>Ask about something Azure is making until it is made. A question Azure refuses is asked again.</summary>
    async Task MadeAsync(string url, string what, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await SendAsync(HttpMethod.Get, url, null, ct);
                switch (JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!["properties"]?["provisioningState"]?.GetValue<string>())
                {
                    case "Succeeded":
                        return;
                    case "Failed":
                        throw new InvalidOperationException($"{what} could not be made.");
                }
            }
            catch (HttpRequestException)
            {
                // Asked too often, or the connection dropped: the next question answers.
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
        throw new TimeoutException($"{what} was not made in ten minutes.");
    }

    public async Task<DesktopPlace> StartAsync(string host, CancellationToken ct)
    {
        (await SendAsync(HttpMethod.Post, $"{Machine(host)}/start?api-version={ComputeApi}", null, ct)).Dispose();
        return new DesktopPlace(host, await AddressAsync(host, ct));
    }

    public async Task<MachineState> StateAsync(string host, CancellationToken ct)
    {
        var machine = await ReadAsync(host, ct);
        if (machine is null) return new MachineState(false, "The machine is gone.");
        var statuses = machine["properties"]?["instanceView"]?["statuses"]?.AsArray() ?? [];
        if (machine["properties"]?["provisioningState"]?.GetValue<string>() == "Failed")
        {
            var why = statuses.FirstOrDefault(status => status?["level"]?.GetValue<string>() == "Error")?["message"]?.GetValue<string>();
            log.LogWarning("Azure could not start the machine {Machine}: {Why}", host, why);
            return new MachineState(false, why ?? "Azure could not start the machine.");
        }
        return new MachineState(Has(statuses, "PowerState/running"));
    }

    public Task<bool> DisplayAnswersAsync(DesktopPlace place, CancellationToken ct) => Display.AnswersAsync(place.Address, ct);

    public async Task StopAsync(string host, CancellationToken ct)
    {
        (await SendAsync(HttpMethod.Post, $"{Machine(host)}/deallocate?api-version={ComputeApi}", null, ct, missingIsDone: true)).Dispose();
        await UntilAsync(host, machine => machine is null || Has(machine["properties"]?["instanceView"]?["statuses"]?.AsArray() ?? [], "PowerState/deallocated"), "stop", ct);
    }

    public async Task DeleteAsync(string host, CancellationToken ct)
    {
        (await SendAsync(HttpMethod.Delete, $"{Machine(host)}?forceDeletion=true&api-version={ComputeApi}", null, ct, missingIsDone: true)).Dispose();
        await UntilAsync(host, machine => machine is null, "be deleted", ct);
        // A machine takes its disk with it. A disk made from a saved setup for a machine that was then never
        // made is still there, and goes here.
        (await SendAsync(HttpMethod.Delete, $"{DiskOf(host)}?api-version={DiskApi}", null, ct, missingIsDone: true)).Dispose();
    }

    public Task SaveAsync(string host, string name, CancellationToken ct) => SnapshotAsync(host, name, "saved-setup", $"The saved setup {name}", ct);

    /// <summary>
    /// An archive is a snapshot like a saved setup, told apart by its name and its tag. Azure does not say how
    /// much of a snapshot is used, so what is answered is the disk's whole size: the most it can hold.
    /// </summary>
    public async Task<long?> ArchiveAsync(string host, string name, CancellationToken ct)
    {
        await SnapshotAsync(host, name, "archive", $"The archive {name}", ct);
        using var response = await SendAsync(HttpMethod.Get, $"{Snapshot(name)}?api-version={DiskApi}", null, ct);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!["properties"]?["diskSizeBytes"]?.GetValue<long>();
    }

    public Task DeleteArchiveAsync(string name, CancellationToken ct) => DeleteSaveAsync(name, ct);

    public async Task<IReadOnlyList<HostedMachine>> ListArchivesAsync(CancellationToken ct) =>
        [.. (await ListSavesAsync(ct)).Where(snapshot => snapshot.Name.StartsWith(DesktopHosting.ArchivePrefix, StringComparison.Ordinal))];

    /// <summary>A full copy of a stopped machine's disk, as a snapshot under this name.</summary>
    async Task SnapshotAsync(string host, string name, string purpose, string what, CancellationToken ct)
    {
        var machine = await ReadAsync(host, ct) ?? throw new InvalidOperationException($"The machine {host} is gone.");
        var disk = machine["properties"]?["storageProfile"]?["osDisk"]?["managedDisk"]?["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"The machine {host} has no disk.");
        // A full copy on the cheapest storage: it is read once for each machine made from it, and billed for what is on it.
        var body = new JsonObject
        {
            ["location"] = Azure.Location,
            ["tags"] = new JsonObject { ["project"] = "workstream", ["purpose"] = purpose },
            ["sku"] = new JsonObject { ["name"] = "Standard_LRS" },
            ["properties"] = new JsonObject
            {
                ["creationData"] = new JsonObject { ["createOption"] = "Copy", ["sourceResourceId"] = disk },
                ["incremental"] = false,
            },
        };
        (await SendAsync(HttpMethod.Put, $"{Snapshot(name)}?api-version={DiskApi}", body, ct)).Dispose();
        await MadeAsync($"{Snapshot(name)}?api-version={DiskApi}", what, ct);
    }

    public async Task DeleteSaveAsync(string name, CancellationToken ct) =>
        (await SendAsync(HttpMethod.Delete, $"{Snapshot(name)}?api-version={DiskApi}", null, ct, missingIsDone: true)).Dispose();

    public async Task<IReadOnlyList<HostedMachine>> ListSavesAsync(CancellationToken ct)
    {
        var saves = new List<HostedMachine>();
        string? next = $"{Group}/providers/Microsoft.Compute/snapshots?api-version={DiskApi}";
        while (next is not null)
        {
            using var response = await SendAsync(HttpMethod.Get, next, null, ct);
            var page = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
            saves.AddRange(page["value"]!.AsArray().Select(save => new HostedMachine(
                save!["name"]!.GetValue<string>(),
                DateTimeOffset.TryParse(save["properties"]?["timeCreated"]?.GetValue<string>(), out var created) ? created : DateTimeOffset.UtcNow)));
            next = page["nextLink"]?.GetValue<string>();
        }
        return saves;
    }

    public async Task<IReadOnlyList<HostedMachine>> ListAsync(CancellationToken ct)
    {
        var machines = new List<HostedMachine>();
        string? next = $"{Group}/providers/Microsoft.Compute/virtualMachines?api-version={ComputeApi}";
        while (next is not null)
        {
            using var response = await SendAsync(HttpMethod.Get, next, null, ct);
            var page = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
            // A machine Azure gives no date for is taken as new, so nothing here ever calls it a stray too early.
            machines.AddRange(page["value"]!.AsArray().Select(machine => new HostedMachine(
                machine!["name"]!.GetValue<string>(),
                DateTimeOffset.TryParse(machine["properties"]?["timeCreated"]?.GetValue<string>(), out var created) ? created : DateTimeOffset.UtcNow)));
            next = page["nextLink"]?.GetValue<string>();
        }
        return machines;
    }

    static string NicName(string machine) => $"{machine}-nic";

    static bool Has(JsonArray statuses, string code) => statuses.Any(status => status?["code"]?.GetValue<string>() == code);

    /// <summary>The machine as Azure describes it now, with its power state. Null when it is not there.</summary>
    async Task<JsonNode?> ReadAsync(string machine, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"{Machine(machine)}?$expand=instanceView&api-version={ComputeApi}", null, ct, missingIsDone: true);
        return response.StatusCode == System.Net.HttpStatusCode.NotFound ? null : JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Ask about the machine every few seconds until it is as wanted. A question Azure refuses is asked again.</summary>
    async Task UntilAsync(string machine, Func<JsonNode?, bool> done, string what, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            try
            {
                var found = await ReadAsync(machine, ct);
                if (done(found)) return;
                if (found?["properties"]?["provisioningState"]?.GetValue<string>() == "Failed") throw new InvalidOperationException($"Azure could not make the machine {machine} {what}.");
            }
            catch (HttpRequestException)
            {
                // Asked too often, or the connection dropped: the next question answers.
            }
        }
        throw new TimeoutException($"The machine {machine} did not {what} in 20 minutes.");
    }

    /// <summary>
    /// The machine's private address, read from Azure and never from the machine itself. Azure makes the
    /// network interface a moment after it takes the request for the machine, and names it itself starting
    /// from the name asked for, so the interface is found through the machine, asking until it is there.
    /// </summary>
    async Task<string> AddressAsync(string machine, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        while (true)
        {
            try
            {
                using var found = await SendAsync(HttpMethod.Get, $"{Machine(machine)}?api-version={ComputeApi}", null, ct);
                var description = JsonNode.Parse(await found.Content.ReadAsStringAsync(ct))!;
                if (description["properties"]?["provisioningState"]?.GetValue<string>() == "Failed") throw new InvalidOperationException($"Azure could not make the machine {machine}.");
                var interfaceId = description["properties"]?["networkProfile"]?["networkInterfaces"]?[0]?["id"]?.GetValue<string>();
                if (interfaceId is not null)
                {
                    using var response = await SendAsync(HttpMethod.Get, $"https://management.azure.com{interfaceId}?api-version={NetworkApi}", null, ct, missingIsDone: true);
                    var address = response.StatusCode == System.Net.HttpStatusCode.NotFound
                        ? null
                        : JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!["properties"]?["ipConfigurations"]?[0]?["properties"]?["privateIPAddress"]?.GetValue<string>();
                    if (address is not null) return $"{address}:{options.DisplayPort}";
                }
            }
            catch (HttpRequestException)
            {
                // Asked too often, or the connection dropped: the next question answers.
            }
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException($"The machine {machine} had no address after three minutes.");
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    /// <summary>
    /// One request to Azure. A refusal with words is an <see cref="InvalidOperationException"/> carrying them;
    /// being asked to slow down is an <see cref="HttpRequestException"/>, which callers that ask in a loop wait out.
    /// </summary>
    async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, JsonNode? body, CancellationToken ct, bool missingIsDone = false)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext(Scopes), ct);
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        var response = await http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode || missingIsDone && response.StatusCode == System.Net.HttpStatusCode.NotFound) return response;
        var text = await response.Content.ReadAsStringAsync(ct);
        var status = response.StatusCode;
        response.Dispose();
        if (status == System.Net.HttpStatusCode.TooManyRequests) throw new HttpRequestException("Azure asked for fewer requests.", null, status);
        throw new InvalidOperationException(AzureWords(text) ?? $"Azure answered {(int)status}.");
    }

    string? AzureWords(string text)
    {
        try
        {
            var error = JsonNode.Parse(text)?["error"];
            log.LogWarning("Azure refused a desktop request: {Code} {Message}", error?["code"]?.GetValue<string>(), error?["message"]?.GetValue<string>());
            return error?["message"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Desktops as Docker containers on this computer, for development: the same image the machines are baked from
/// (desktop/Dockerfile), reached on a port Docker publishes on localhost.
/// </summary>
public sealed class DockerDesktopHost(DesktopOptions options) : IDesktopHost
{
    public async Task<DesktopPlace> CreateAsync(DesktopSpec spec, CancellationToken ct)
    {
        await RunAsync(["rm", "-f", spec.Name], ct, tolerate: true);
        await RunAsync(
        [
            "run", "-d", "--name", spec.Name, "--hostname", "desktop", "--shm-size", "1g",
            "--label", "workstream.desktop=" + spec.DesktopId, "--label", "workstream.purpose=" + spec.Purpose,
            "-p", $"127.0.0.1::{options.DisplayPort}",
            "-e", "WORKSTREAM_API=" + spec.ApiUrl, "-e", "WORKSTREAM_KEY=" + spec.Key,
            spec.Archive is not null ? ArchiveImage(spec.Archive) : spec.Save is null ? options.Docker.Image : SaveImage(spec.Save),
        ], ct);
        return new DesktopPlace(spec.Name, await AddressAsync(spec.Name, ct));
    }

    /// <summary>A saved setup, locally: the stopped container kept as an image, without the two things that container was told.</summary>
    const string SaveRepository = "workstream-save";
    static string SaveImage(string name) => $"{SaveRepository}:{name}";

    public Task SaveAsync(string host, string name, CancellationToken ct) =>
        RunAsync(["commit", "--change", "ENV WORKSTREAM_API=", "--change", "ENV WORKSTREAM_KEY=", host, SaveImage(name)], ct);

    public Task DeleteSaveAsync(string name, CancellationToken ct) => RunAsync(["rmi", "-f", SaveImage(name)], ct, tolerate: true);

    public async Task<IReadOnlyList<HostedMachine>> ListSavesAsync(CancellationToken ct) =>
        (await RunAsync(["images", SaveRepository, "--format", "{{.Tag}}"], ct, tolerate: true))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            // Docker says when in words; a save it lists is taken as old enough to be judged.
            .Select(tag => new HostedMachine(tag, DateTimeOffset.MinValue)).ToList();

    /// <summary>An archive, locally: the stopped container kept as an image, like a saved setup, in a repository of its own.</summary>
    const string ArchiveRepository = "workstream-archive";
    static string ArchiveImage(string name) => $"{ArchiveRepository}:{name}";

    public async Task<long?> ArchiveAsync(string host, string name, CancellationToken ct)
    {
        await RunAsync(["commit", "--change", "ENV WORKSTREAM_API=", "--change", "ENV WORKSTREAM_KEY=", host, ArchiveImage(name)], ct);
        return long.TryParse((await RunAsync(["image", "inspect", "-f", "{{.Size}}", ArchiveImage(name)], ct, tolerate: true)).Trim(), out var bytes) ? bytes : null;
    }

    public Task DeleteArchiveAsync(string name, CancellationToken ct) => RunAsync(["rmi", "-f", ArchiveImage(name)], ct, tolerate: true);

    public async Task<IReadOnlyList<HostedMachine>> ListArchivesAsync(CancellationToken ct)
    {
        var tags = (await RunAsync(["images", ArchiveRepository, "--format", "{{.Tag}}"], ct, tolerate: true))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var archives = new List<HostedMachine>();
        foreach (var tag in tags)
        {
            var created = (await RunAsync(["image", "inspect", "-f", "{{.Created}}", ArchiveImage(tag)], ct, tolerate: true)).Trim();
            // One Docker gives no date for is taken as new, so nothing here calls it a stray too early.
            archives.Add(new HostedMachine(tag, DateTimeOffset.TryParse(created, out var at) ? at : DateTimeOffset.UtcNow));
        }
        return archives;
    }

    public async Task<DesktopPlace> StartAsync(string host, CancellationToken ct)
    {
        await RunAsync(["start", host], ct);
        return new DesktopPlace(host, await AddressAsync(host, ct));
    }

    /// <summary>A container is on as soon as Docker has started it.</summary>
    public async Task<MachineState> StateAsync(string host, CancellationToken ct) =>
        (await RunAsync(["inspect", "-f", "{{.State.Running}}", host], ct, tolerate: true)).Trim() == "true"
            ? new MachineState(true)
            : new MachineState(false, "The container is not running.");

    public Task<bool> DisplayAnswersAsync(DesktopPlace place, CancellationToken ct) => Display.AnswersAsync(place.Address, ct);

    public Task StopAsync(string host, CancellationToken ct) => RunAsync(["stop", "-t", "5", host], ct, tolerate: true);

    public Task DeleteAsync(string host, CancellationToken ct) => RunAsync(["rm", "-f", host], ct, tolerate: true);

    public async Task<IReadOnlyList<HostedMachine>> ListAsync(CancellationToken ct)
    {
        var names = (await RunAsync(["ps", "-a", "--filter", "label=workstream.desktop", "--format", "{{.Names}}"], ct))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var machines = new List<HostedMachine>();
        foreach (var name in names)
        {
            var created = (await RunAsync(["inspect", "-f", "{{.Created}}", name], ct, tolerate: true)).Trim();
            machines.Add(new HostedMachine(name, DateTimeOffset.TryParse(created, out var at) ? at : DateTimeOffset.UtcNow));
        }
        return machines;
    }

    /// <summary>Docker picks the published port again each time the container starts.</summary>
    async Task<string> AddressAsync(string name, CancellationToken ct)
    {
        var published = (await RunAsync(["port", name, $"{options.DisplayPort}/tcp"], ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? throw new InvalidOperationException($"The container {name} publishes no display port.");
        return $"127.0.0.1:{published[(published.LastIndexOf(':') + 1)..].Trim()}";
    }

    static async Task<string> RunAsync(string[] arguments, CancellationToken ct, bool tolerate = false)
    {
        using var process = Process.Start(new ProcessStartInfo("docker", arguments) { RedirectStandardOutput = true, RedirectStandardError = true })
            ?? throw new InvalidOperationException("Docker did not start.");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0 && !tolerate) throw new InvalidOperationException($"docker {arguments[0]} failed: {(await error).Trim()}");
        return await output;
    }
}
