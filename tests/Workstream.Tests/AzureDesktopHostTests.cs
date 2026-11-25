using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Workstream.Api.Desktops;
using Workstream.Api.Domain;

namespace Workstream.Tests;

/// <summary>
/// What the Azure host asks Azure for, with Azure played (<see cref="FakeAzure"/>): how a machine is made and
/// stopped where the deployment hibernates its desktops (<c>Workstream:Desktops:Azure:Hibernate</c>) and
/// where it does not. The same steps against real Azure are <see cref="AzureDesktopHostLive"/>.
/// </summary>
[TestClass]
public sealed class AzureDesktopHostTests
{
    static readonly string Id = Guid.NewGuid().ToString();
    static readonly string Name = DesktopHosting.NameFor(Id);

    static (AzureDesktopHost Host, FakeAzure Azure) Played(bool hibernate, Action<DesktopOptions>? change = null)
    {
        var azure = new FakeAzure();
        var options = new DesktopOptions
        {
            Azure = { SubscriptionId = "played", ResourceGroup = "played", Location = "westus3", SubnetId = "/subnets/desktops", ImageId = "/images/machine/versions/1.0.0", Hibernate = hibernate },
        };
        change?.Invoke(options);
        return (new AzureDesktopHost(new HttpClient(azure), azure.Credential, options, NullLogger<AzureDesktopHost>.Instance), azure);
    }

    static DesktopSpec Spec(string purpose = MachinePurpose.Task, string? save = null) =>
        new(Name, Id, Guid.NewGuid().ToString(), DesktopSizes.Standard, "https://api.test", "key", purpose, save);

    static JsonNode Made(FakeAzure azure) => azure.Machines[Name].Body["properties"]!;

    static string[] Posts(FakeAzure azure) => azure.Asked.Where(asked => asked.StartsWith("POST ")).Select(asked => asked[(asked.LastIndexOf('/') + 1)..]).ToArray();

    [TestMethod]
    public async Task Without_hibernation_a_machine_is_made_with_Trusted_Launch_and_stopped_by_releasing_it()
    {
        var (host, azure) = Played(hibernate: false);

        var place = await host.CreateAsync(Spec(), default);
        Assert.AreEqual("10.60.4.7:5900", place.Address);
        Assert.AreEqual("TrustedLaunch", Made(azure)["securityProfile"]!["securityType"]!.GetValue<string>());
        Assert.IsNull(Made(azure)["additionalCapabilities"]);
        Assert.AreEqual("Standard_D4as_v7", Made(azure)["hardwareProfile"]!["vmSize"]!.GetValue<string>());

        await host.StopAsync(Name, default);
        CollectionAssert.AreEqual(new[] { "deallocate" }, Posts(azure));
        Assert.IsFalse(azure.Machines[Name].Hibernated);
        Assert.IsFalse((await host.StateAsync(Name, default)).On);
    }

    [TestMethod]
    public async Task A_machine_of_an_environment_with_the_iPhone_is_made_from_the_image_that_has_its_simulator()
    {
        static string Image(FakeAzure azure) => Made(azure)["storageProfile"]!["imageReference"]!["id"]!.GetValue<string>();
        const string withIphone = "/images/machine-iphone/versions/1.0.0";

        // A deployment with such an image: the machine that wants the iPhone starts from it, and no other does.
        var (host, azure) = Played(hibernate: false, options => options.Azure.IphoneImageId = withIphone);
        await host.CreateAsync(Spec() with { Iphone = true }, default);
        Assert.AreEqual(withIphone, Image(azure));
        (host, azure) = Played(hibernate: false, options => options.Azure.IphoneImageId = withIphone);
        await host.CreateAsync(Spec(), default);
        Assert.AreEqual("/images/machine/versions/1.0.0", Image(azure));

        // A deployment without one: the machine starts from the one image there is, and its setup installs the simulator.
        (host, azure) = Played(hibernate: false);
        await host.CreateAsync(Spec() with { Iphone = true }, default);
        Assert.AreEqual("/images/machine/versions/1.0.0", Image(azure));
    }

    [TestMethod]
    public async Task With_hibernation_a_machine_is_made_to_hibernate_and_a_stop_hibernates_it()
    {
        var (host, azure) = Played(hibernate: true, options => options.Azure.Sizes[DesktopSizes.Standard] = "Standard_D4as_v6");

        await host.CreateAsync(Spec(), default);
        Assert.IsTrue(Made(azure)["additionalCapabilities"]!["hibernationEnabled"]!.GetValue<bool>());
        // Azure refuses Trusted Launch on a Linux machine that hibernates.
        Assert.IsNull(Made(azure)["securityProfile"]);
        Assert.AreEqual("Standard_D4as_v6", Made(azure)["hardwareProfile"]!["vmSize"]!.GetValue<string>());

        await host.StopAsync(Name, default);
        CollectionAssert.AreEqual(new[] { "deallocate?hibernate=true" }, Posts(azure));
        Assert.IsTrue(azure.Machines[Name].Hibernated);
        Assert.IsFalse((await host.StateAsync(Name, default)).On);

        // Stopping a machine that is already hibernated asks Azure for nothing: a plain stop would throw its memory away.
        await host.StopAsync(Name, default);
        Assert.AreEqual(1, Posts(azure).Length);
        Assert.IsTrue(azure.Machines[Name].Hibernated);

        // Starting it is the same request as starting any stopped machine; Azure resumes it.
        await host.StartAsync(Name, default);
        Assert.IsTrue((await host.StateAsync(Name, default)).On);
        Assert.AreEqual("start", Posts(azure)[^1]);
    }

    [TestMethod]
    public async Task A_machine_preparing_a_saved_setup_never_hibernates()
    {
        var (host, azure) = Played(hibernate: true);

        await host.CreateAsync(Spec(MachinePurpose.Prepare), default);
        // Its disk is copied for other machines, so no machine's memory may be on it; and it is made like the
        // machines that start from it, without Trusted Launch.
        Assert.IsNull(Made(azure)["additionalCapabilities"]);
        Assert.IsNull(Made(azure)["securityProfile"]);

        await host.StopAsync(Name, default);
        CollectionAssert.AreEqual(new[] { "deallocate" }, Posts(azure));
    }

    [TestMethod]
    public async Task A_machine_from_a_saved_setup_gets_a_disk_that_can_hold_its_memory()
    {
        var (host, azure) = Played(hibernate: true);
        azure.Snapshots["save-1"] = false;

        await host.CreateAsync(Spec(save: "save-1"), default);
        Assert.IsTrue(azure.Disks[Name + "-os"]["properties"]!["supportsHibernation"]!.GetValue<bool>());
        Assert.IsTrue(Made(azure)["additionalCapabilities"]!["hibernationEnabled"]!.GetValue<bool>());
        Assert.IsNull(Made(azure)["securityProfile"]);

        var (plain, plainAzure) = Played(hibernate: false);
        plainAzure.Snapshots["save-1"] = true;
        await plain.CreateAsync(Spec(save: "save-1"), default);
        Assert.IsNull(plainAzure.Disks[Name + "-os"]["properties"]!["supportsHibernation"]);
        Assert.AreEqual("TrustedLaunch", Made(plainAzure)["securityProfile"]!["securityType"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task A_desktop_archived_with_Trusted_Launch_comes_back_with_it_and_does_not_hibernate()
    {
        // Archived before the deployment hibernated: its disk has Trusted Launch, and Azure takes such a disk
        // only on a machine that has it too, which is one Azure will not hibernate.
        var (host, azure) = Played(hibernate: true);
        azure.Snapshots["wsa-1"] = true;

        await host.CreateAsync(Spec() with { Archive = "wsa-1" }, default);
        Assert.AreEqual("TrustedLaunch", Made(azure)["securityProfile"]!["securityType"]!.GetValue<string>());
        Assert.IsNull(Made(azure)["additionalCapabilities"]);
        Assert.IsNull(azure.Disks[Name + "-os"]["properties"]!["supportsHibernation"]);

        await host.StopAsync(Name, default);
        CollectionAssert.AreEqual(new[] { "deallocate" }, Posts(azure));
    }

    [TestMethod]
    public async Task A_desktop_archived_without_Trusted_Launch_comes_back_without_it_where_nothing_hibernates()
    {
        // The other way round: hibernation was turned off again, and the archive is of a disk made without Trusted Launch.
        var (host, azure) = Played(hibernate: false);
        azure.Snapshots["wsa-1"] = false;

        await host.CreateAsync(Spec() with { Archive = "wsa-1" }, default);
        Assert.IsNull(Made(azure)["securityProfile"]);
        Assert.IsNull(Made(azure)["additionalCapabilities"]);
    }

    [TestMethod]
    public async Task A_machine_made_before_the_deployment_hibernated_is_stopped_as_before()
    {
        var (before, azure) = Played(hibernate: false);
        await before.CreateAsync(Spec(), default);
        var after = new AzureDesktopHost(new HttpClient(azure), azure.Credential,
            new DesktopOptions { Azure = { SubscriptionId = "played", ResourceGroup = "played", Hibernate = true } }, NullLogger<AzureDesktopHost>.Instance);

        await after.StopAsync(Name, default);
        // It was not made to hibernate, so Azure is not asked to.
        CollectionAssert.AreEqual(new[] { "deallocate" }, Posts(azure));
        Assert.AreEqual("deallocated", azure.Machines[Name].Power);
    }

    [TestMethod]
    public async Task When_Azure_refuses_to_hibernate_the_machine_is_stopped_without_its_memory()
    {
        var (host, azure) = Played(hibernate: true);
        await host.CreateAsync(Spec(), default);
        azure.RefuseHibernate = "The VM could not be hibernated.";

        await host.StopAsync(Name, default);
        CollectionAssert.AreEqual(new[] { "deallocate?hibernate=true", "deallocate" }, Posts(azure));
        Assert.AreEqual("deallocated", azure.Machines[Name].Power);
        Assert.IsFalse(azure.Machines[Name].Hibernated);
    }

    [TestMethod]
    public async Task When_the_machine_does_not_manage_to_hibernate_it_is_stopped_without_its_memory()
    {
        var (host, azure) = Played(hibernate: true);
        await host.CreateAsync(Spec(), default);
        azure.HibernateFails = true;

        await host.StopAsync(Name, default);
        CollectionAssert.AreEqual(new[] { "deallocate?hibernate=true", "deallocate" }, Posts(azure));
        Assert.AreEqual("deallocated", azure.Machines[Name].Power);
        Assert.IsFalse(azure.Machines[Name].Hibernated);
    }

    [TestMethod]
    public async Task Stopping_a_machine_that_is_gone_is_done()
    {
        var (host, azure) = Played(hibernate: true);
        await host.StopAsync(Name, default);
        Assert.AreEqual(0, Posts(azure).Length);
    }
}
