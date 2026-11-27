using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;

namespace Workstream.Tests;

/// <summary>
/// Azure's Resource Manager, played, for the one host that talks to it (<c>AzureDesktopHost</c>): it keeps
/// the machines and disks it was asked for, answers the questions the host asks about them, and remembers
/// every request. A machine is on as soon as it is made or started and released as soon as it is asked to
/// stop, unless a test says Azure refuses or fails to hibernate.
/// </summary>
public sealed class FakeAzure : HttpMessageHandler
{
    public sealed class PlayedMachine
    {
        public required JsonObject Body { get; init; }
        public string Power { get; set; } = "running";
        public bool Hibernated { get; set; }
        public string Provisioning { get; set; } = "Succeeded";
    }

    public ConcurrentDictionary<string, PlayedMachine> Machines { get; } = new();
    public ConcurrentDictionary<string, JsonObject> Disks { get; } = new();
    /// <summary>Saved setups and archives Azure holds, by name, and whether each is of a disk made with Trusted Launch.</summary>
    public ConcurrentDictionary<string, bool> Snapshots { get; } = new();
    /// <summary>Every request, as <c>METHOD path?query</c> without the API's version.</summary>
    public ConcurrentQueue<string> Asked { get; } = new();
    /// <summary>Azure refuses to hibernate at once, with these words.</summary>
    public string? RefuseHibernate { get; set; }
    /// <summary>Azure takes the request to hibernate and the machine then does not manage it: it stays on, and Azure calls the operation failed.</summary>
    public bool HibernateFails { get; set; }

    public TokenCredential Credential { get; } = new PlayedCredential();

    sealed class PlayedCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => new("played", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => new(GetToken(requestContext, cancellationToken));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = string.Join('&', request.RequestUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Where(part => !part.StartsWith("api-version=")));
        Asked.Enqueue($"{request.Method} {path}{(query.Length > 0 ? "?" + query : "")}");
        var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct))!.AsObject();
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var kind = parts.Length > 6 ? parts[6] : "";
        var name = parts.Length > 7 ? parts[7] : "";
        var action = parts.Length > 8 ? parts[8] : "";

        if (kind == "disks")
        {
            if (request.Method == HttpMethod.Put) Disks[name] = body!;
            if (request.Method == HttpMethod.Delete) return Disks.TryRemove(name, out _) ? Json(new JsonObject()) : Missing();
            if (!Disks.TryGetValue(name, out var disk)) return Missing();
            // A disk says the security type it was made with, as a snapshot does.
            var says = new JsonObject { ["provisioningState"] = "Succeeded" };
            if (disk["properties"]?["securityProfile"] is { } security) says["securityProfile"] = security.DeepClone();
            return Json(new JsonObject { ["properties"] = says, ["made"] = disk.DeepClone() });
        }
        if (kind == "snapshots")
        {
            if (!Snapshots.TryGetValue(name, out var trusted)) return Missing();
            var properties = new JsonObject { ["provisioningState"] = "Succeeded" };
            if (trusted) properties["securityProfile"] = new JsonObject { ["securityType"] = "TrustedLaunch" };
            return Json(new JsonObject { ["name"] = name, ["properties"] = properties });
        }
        if (kind == "networkInterfaces")
        {
            return Json(new JsonObject { ["properties"] = new JsonObject { ["ipConfigurations"] = new JsonArray(new JsonObject { ["properties"] = new JsonObject { ["privateIPAddress"] = "10.60.4.7" } }) } });
        }
        if (kind != "virtualMachines") return Missing();

        if (request.Method == HttpMethod.Put)
        {
            Machines[name] = new PlayedMachine { Body = body! };
            // A machine made from an image gets a disk of its own, of the machine's security type.
            if (body!["properties"]?["storageProfile"]?["osDisk"]?["createOption"]?.GetValue<string>() == "FromImage")
            {
                var made = new JsonObject();
                if (body["properties"]?["securityProfile"]?["securityType"] is { } type) made["securityProfile"] = new JsonObject { ["securityType"] = type.GetValue<string>() };
                Disks[name + "-os"] = new JsonObject { ["properties"] = made };
            }
            return Json(new JsonObject());
        }
        if (!Machines.TryGetValue(name, out var machine)) return Missing();
        if (request.Method == HttpMethod.Patch)
        {
            // The one change a desktop host makes to a machine: whether its disk goes with it.
            if (body!["properties"]?["storageProfile"]?["osDisk"]?["deleteOption"] is { } option)
                machine.Body["properties"]!["storageProfile"]!["osDisk"]!["deleteOption"] = option.GetValue<string>();
            return Json(new JsonObject());
        }
        if (request.Method == HttpMethod.Delete)
        {
            Machines.TryRemove(name, out _);
            // A machine takes its disk with it unless it was told to leave it.
            if (machine.Body["properties"]?["storageProfile"]?["osDisk"]?["deleteOption"]?.GetValue<string>() != "Detach") Disks.TryRemove(name + "-os", out _);
            return Json(new JsonObject());
        }
        if (request.Method == HttpMethod.Post && action == "deallocate")
        {
            if (query.Contains("hibernate=true"))
            {
                if (RefuseHibernate is not null) return Refused(RefuseHibernate);
                if (HibernateFails)
                {
                    machine.Provisioning = "Failed";
                    return Json(new JsonObject());
                }
                machine.Hibernated = true;
            }
            machine.Power = "deallocated";
            machine.Provisioning = "Succeeded";
            return Json(new JsonObject());
        }
        if (request.Method == HttpMethod.Post && action == "start")
        {
            machine.Power = "running";
            machine.Hibernated = false;
            machine.Provisioning = "Succeeded";
            return Json(new JsonObject());
        }

        var statuses = new JsonArray(new JsonObject { ["code"] = "PowerState/" + machine.Power });
        if (machine.Hibernated) statuses.Add(new JsonObject { ["code"] = "HibernationState/Hibernated" });
        var described = machine.Body["properties"]!.DeepClone().AsObject();
        described["provisioningState"] = machine.Provisioning;
        described["instanceView"] = new JsonObject { ["statuses"] = statuses };
        described["networkProfile"] = new JsonObject
        {
            ["networkInterfaces"] = new JsonArray(new JsonObject { ["id"] = $"/subscriptions/played/resourceGroups/played/providers/Microsoft.Network/networkInterfaces/{name}-nic" }),
        };
        return Json(new JsonObject { ["name"] = name, ["properties"] = described });
    }

    static HttpResponseMessage Json(JsonNode node) => new(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };

    static HttpResponseMessage Missing() => new(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":{"code":"ResourceNotFound","message":"Not found."}}""", Encoding.UTF8, "application/json") };

    static HttpResponseMessage Refused(string words) => new(HttpStatusCode.Conflict)
    {
        Content = new StringContent(new JsonObject { ["error"] = new JsonObject { ["code"] = "OperationNotAllowed", ["message"] = words } }.ToJsonString(), Encoding.UTF8, "application/json"),
    };
}
