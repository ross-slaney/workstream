using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using Workstream.Api.Domain;

namespace Workstream.Api.Desktops;

/// <summary>What a machine is made with. The key and the API's address are the only two things it is told; it asks the API for the rest.</summary>
public sealed record DesktopSpec(string Name, string TaskId, string TeamId, string Size, string ApiUrl, string Key);

/// <summary>Where a desktop is: its name at the host, and where the API reaches its display (<c>host:port</c>).</summary>
public sealed record DesktopPlace(string Host, string Address);

/// <summary>
/// Whatever runs the desktops: Azure virtual machines when deployed, Docker containers in development. The jobs
/// (<see cref="DesktopJobs"/>) are its only caller. Every method is safe to call again.
/// </summary>
public interface IDesktopHost
{
    Task<DesktopPlace> CreateAsync(DesktopSpec spec, CancellationToken ct);
    /// <summary>Start a stopped machine again. Its address may have changed.</summary>
    Task<DesktopPlace> StartAsync(string host, CancellationToken ct);
    /// <summary>Release the machine but keep its disk.</summary>
    Task StopAsync(string host, CancellationToken ct);
    /// <summary>Remove the machine and everything made with it. A machine that is not there is already deleted.</summary>
    Task DeleteAsync(string host, CancellationToken ct);
    /// <summary>Every machine this host holds, by name: what the sweep compares with the tasks to find strays.</summary>
    Task<IReadOnlyList<string>> ListAsync(CancellationToken ct);
}

/// <summary>Settings for the desktops (<c>Workstream:Desktops</c>).</summary>
public sealed class DesktopOptions
{
    /// <summary><c>azure</c>, <c>docker</c> or <c>none</c> (a task then fails to start, with words that say why).</summary>
    public string Host { get; set; } = "none";
    /// <summary>The API's address as a desktop reaches it. PUBLIC_ORIGIN when deployed; the Docker host's name in development.</summary>
    public string ApiUrl { get; set; } = "";
    /// <summary>A ready desktop nobody has watched or written to for this long, whose agent is idle, is stopped.</summary>
    public int IdleMinutes { get; set; } = 30;
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
        /// <summary>The image a desktop is made from: the gallery image definition, whose latest version is used.</summary>
        public string ImageId { get; set; } = "";
        public Dictionary<string, string> Sizes { get; set; } = new()
        {
            [DesktopSizes.Small] = "Standard_D2as_v7",
            [DesktopSizes.Standard] = "Standard_D4as_v7",
            [DesktopSizes.Large] = "Standard_D8as_v7",
        };
    }

    public sealed class DockerOptions
    {
        public string Image { get; set; } = "workstream-desktop:local";
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

    /// <summary>The machine's name for a task: short, and the same every time, so a retry finds what an earlier try made.</summary>
    public static string NameFor(string taskId) => "ws-" + Guid.Parse(taskId).ToString("N")[..16];
}

/// <summary>An environment with no way to run desktops: the tests' default, and a deployment that forgot to configure one.</summary>
public sealed class NoDesktopHost : IDesktopHost
{
    const string Words = "This environment has no desktop host. Set Workstream:Desktops:Host to azure or docker.";
    public Task<DesktopPlace> CreateAsync(DesktopSpec spec, CancellationToken ct) => throw new InvalidOperationException(Words);
    public Task<DesktopPlace> StartAsync(string host, CancellationToken ct) => throw new InvalidOperationException(Words);
    public Task StopAsync(string host, CancellationToken ct) => Task.CompletedTask;
    public Task DeleteAsync(string host, CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
}

/// <summary>
/// Desktops as Azure virtual machines, through Resource Manager's REST API as this app's managed identity.
/// A machine is one request to make and one to delete: its network interface and disk are declared inside it
/// with <c>deleteOption: Delete</c>, so nothing outlives it. It has no public address; it reaches the internet
/// through the subnet's NAT gateway and is reached only by the API, on the private network.
/// </summary>
public sealed class AzureDesktopHost(HttpClient http, TokenCredential credential, DesktopOptions options, ILogger<AzureDesktopHost> log) : IDesktopHost
{
    const string ComputeApi = "2024-11-01";
    const string NetworkApi = "2024-05-01";
    static readonly string[] Scopes = ["https://management.azure.com/.default"];

    DesktopOptions.AzureOptions Azure => options.Azure;
    string Group => $"https://management.azure.com/subscriptions/{Azure.SubscriptionId}/resourceGroups/{Azure.ResourceGroup}";
    string Machine(string name) => $"{Group}/providers/Microsoft.Compute/virtualMachines/{name}";

    public async Task<DesktopPlace> CreateAsync(DesktopSpec spec, CancellationToken ct)
    {
        if (!Azure.Sizes.TryGetValue(spec.Size, out var vmSize)) throw new InvalidOperationException($"No machine size is configured for '{spec.Size}'.");
        // The only two things a desktop is told. It reads the rest from the API with the key (desktop/agent/agent.mjs).
        var cloudConfig = $"""
            #cloud-config
            write_files:
              - path: /etc/workstream/desktop.env
                permissions: '0640'
                owner: root:work
                content: |
                  WORKSTREAM_API={spec.ApiUrl}
                  WORKSTREAM_KEY={spec.Key}
            """;
        var body = new JsonObject
        {
            ["location"] = Azure.Location,
            ["tags"] = new JsonObject { ["project"] = "workstream", ["task"] = spec.TaskId, ["team"] = spec.TeamId },
            ["properties"] = new JsonObject
            {
                ["hardwareProfile"] = new JsonObject { ["vmSize"] = vmSize },
                ["storageProfile"] = new JsonObject
                {
                    ["imageReference"] = new JsonObject { ["id"] = Azure.ImageId },
                    ["osDisk"] = new JsonObject
                    {
                        ["createOption"] = "FromImage",
                        ["deleteOption"] = "Delete",
                        ["managedDisk"] = new JsonObject { ["storageAccountType"] = "StandardSSD_LRS" },
                    },
                },
                ["osProfile"] = new JsonObject
                {
                    ["computerName"] = spec.Name,
                    // Azure requires an administrator. Nobody signs in as it: the password is random and kept nowhere,
                    // and the subnet admits no SSH.
                    ["adminUsername"] = "wsadmin",
                    ["adminPassword"] = $"Ws!{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24))}a",
                    ["customData"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(cloudConfig)),
                    ["linuxConfiguration"] = new JsonObject { ["disablePasswordAuthentication"] = false },
                },
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
        await CompleteAsync(await SendAsync(HttpMethod.Put, $"{Machine(spec.Name)}?api-version={ComputeApi}", body, ct), ct);
        return new DesktopPlace(spec.Name, await AddressAsync(spec.Name, ct));
    }

    public async Task<DesktopPlace> StartAsync(string host, CancellationToken ct)
    {
        await CompleteAsync(await SendAsync(HttpMethod.Post, $"{Machine(host)}/start?api-version={ComputeApi}", null, ct), ct);
        return new DesktopPlace(host, await AddressAsync(host, ct));
    }

    public async Task StopAsync(string host, CancellationToken ct) =>
        await CompleteAsync(await SendAsync(HttpMethod.Post, $"{Machine(host)}/deallocate?api-version={ComputeApi}", null, ct, missingIsDone: true), ct);

    public async Task DeleteAsync(string host, CancellationToken ct) =>
        await CompleteAsync(await SendAsync(HttpMethod.Delete, $"{Machine(host)}?forceDeletion=true&api-version={ComputeApi}", null, ct, missingIsDone: true), ct);

    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken ct)
    {
        var names = new List<string>();
        string? next = $"{Group}/providers/Microsoft.Compute/virtualMachines?api-version={ComputeApi}";
        while (next is not null)
        {
            using var response = await SendAsync(HttpMethod.Get, next, null, ct);
            var page = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
            names.AddRange(page["value"]!.AsArray().Select(machine => machine!["name"]!.GetValue<string>()));
            next = page["nextLink"]?.GetValue<string>();
        }
        return names;
    }

    static string NicName(string machine) => $"{machine}-nic";

    /// <summary>
    /// The machine's private address, read from Azure and never from the machine itself. Azure names the
    /// network interface it makes with a machine itself, starting from the name asked for, so the interface is
    /// found through the machine.
    /// </summary>
    async Task<string> AddressAsync(string machine, CancellationToken ct)
    {
        using var found = await SendAsync(HttpMethod.Get, $"{Machine(machine)}?api-version={ComputeApi}", null, ct);
        var interfaceId = JsonNode.Parse(await found.Content.ReadAsStringAsync(ct))!["properties"]?["networkProfile"]?["networkInterfaces"]?[0]?["id"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"The machine {machine} has no network interface.");
        using var response = await SendAsync(HttpMethod.Get, $"https://management.azure.com{interfaceId}?api-version={NetworkApi}", null, ct);
        var nic = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
        var address = nic["properties"]?["ipConfigurations"]?[0]?["properties"]?["privateIPAddress"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"The machine {machine} has no private address yet.");
        return $"{address}:{options.DisplayPort}";
    }

    async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, JsonNode? body, CancellationToken ct, bool missingIsDone = false)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext(Scopes), ct);
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        var response = await http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode || missingIsDone && response.StatusCode == System.Net.HttpStatusCode.NotFound) return response;
        var text = await response.Content.ReadAsStringAsync(ct);
        response.Dispose();
        throw new InvalidOperationException(AzureWords(text) ?? $"Azure answered {(int)response.StatusCode}.");
    }

    /// <summary>Wait for a long-running request: Azure answers at once and gives an address to ask until it is done.</summary>
    async Task CompleteAsync(HttpResponseMessage accepted, CancellationToken ct)
    {
        using (accepted)
        {
            if (accepted.StatusCode == System.Net.HttpStatusCode.NotFound) return;
            var status = accepted.Headers.TryGetValues("Azure-AsyncOperation", out var values) ? values.FirstOrDefault() : null;
            if (status is null) return;
            var deadline = DateTimeOffset.UtcNow.AddMinutes(20);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(4), ct);
                using var response = await SendAsync(HttpMethod.Get, status, null, ct);
                var operation = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
                switch (operation["status"]?.GetValue<string>())
                {
                    case "Succeeded":
                        return;
                    case "Failed" or "Canceled":
                        throw new InvalidOperationException(operation["error"]?["message"]?.GetValue<string>() ?? "Azure could not finish the request.");
                }
            }
            throw new TimeoutException("Azure did not finish the request in 20 minutes.");
        }
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
            "--label", "workstream.task=" + spec.TaskId,
            "-p", $"127.0.0.1::{options.DisplayPort}",
            "-e", "WORKSTREAM_API=" + spec.ApiUrl, "-e", "WORKSTREAM_KEY=" + spec.Key,
            options.Docker.Image,
        ], ct);
        return new DesktopPlace(spec.Name, await AddressAsync(spec.Name, ct));
    }

    public async Task<DesktopPlace> StartAsync(string host, CancellationToken ct)
    {
        await RunAsync(["start", host], ct);
        return new DesktopPlace(host, await AddressAsync(host, ct));
    }

    public Task StopAsync(string host, CancellationToken ct) => RunAsync(["stop", "-t", "5", host], ct, tolerate: true);

    public Task DeleteAsync(string host, CancellationToken ct) => RunAsync(["rm", "-f", host], ct, tolerate: true);

    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken ct) =>
        (await RunAsync(["ps", "-a", "--filter", "label=workstream.task", "--format", "{{.Names}}"], ct))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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
