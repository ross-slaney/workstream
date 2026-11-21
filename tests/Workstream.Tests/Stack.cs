using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Workstream.Api;
using Workstream.Api.Desktops;
using Workstream.Api.Model;

namespace Workstream.Tests;

/// <summary>
/// The stack the journeys run against, started once per test run. SQL Server and the database come from the
/// real AppHost (apphost/AppHost.cs) through Aspire.Hosting.Testing, as a throwaway container on a port of its
/// own, without the api and web resources. The API runs here, in the test process, with the same
/// <c>AddWorkstreamApi</c>/<c>MapWorkstreamApi</c> as Program.cs, so coverage sees it. Two things are played
/// instead of real: the desktops (<see cref="FakeDesktops"/>) and the model (<see cref="FakeModel"/>). No job
/// server runs: a test calls the job it wants (<see cref="Jobs"/>). Tests isolate themselves by signing up
/// new people, each with a team of their own.
/// </summary>
[TestClass]
public static class Stack
{
    // Ports and a database of its own, so the tests run next to a developer's AppHost (5280, SQL on 1454).
    public static readonly Uri Origin = new($"http://localhost:{Port("WORKSTREAM_TEST_API_PORT", 15280)}");
    public const string WebOrigin = "http://localhost:18091";
    public const string AdministratorEmail = "admin@workstream.test";
    public const string AdministratorPassword = "Administrator-journeys-1";
    public const string ModelKey = "foundry-key-for-journeys";
    const string SqlPassword = "Workstream-Tests-Passw0rd!";
    static readonly int SqlPort = Port("WORKSTREAM_TEST_SQL_PORT", 14454);
    static readonly string Database = Environment.GetEnvironmentVariable("WORKSTREAM_TEST_DATABASE") is { Length: > 0 } name ? name : "workstream-tests";

    public static readonly FakeDesktops Desktops = new();
    public static readonly FakeModel Model = new();

    static DistributedApplication? aspire;
    static WebApplication? api;

    public static IServiceProvider Services => api!.Services;

    /// <summary>The background jobs, run when a test asks: starting, stopping and deleting desktops, and the sweep.</summary>
    public static DesktopJobs Jobs => Services.GetRequiredService<DesktopJobs>();

    [AssemblyInitialize]
    public static async Task StartAsync(TestContext context)
    {
        using var startup = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Workstream_AppHost>(
            [
                "DcpPublisher:RandomizePorts=false",
                $"PUBLIC_ORIGIN={Origin.GetLeftPart(UriPartial.Authority)}",
                $"WEB_ORIGIN={WebOrigin}",
                $"CONNECTION_STRING=Server=localhost,{SqlPort};Database={Database};User Id=sa;Password={SqlPassword};TrustServerCertificate=True;",
            ],
            (options, _) => options.DisableDashboard = true);

        // A throwaway SQL Server: no persistent container, no data volume.
        var sql = builder.Resources.OfType<SqlServerServerResource>().Single();
        foreach (var persistence in sql.Annotations.Where(a => a is ContainerLifetimeAnnotation or ContainerMountAnnotation).ToList())
        {
            sql.Annotations.Remove(persistence);
        }
        // The API runs in this process; the web app is for people.
        foreach (var resource in builder.Resources.Where(r => r.Name is "api" or "web" || r.GetType().Name == "JavaScriptInstallerResource").ToList())
        {
            builder.Resources.Remove(resource);
        }

        aspire = await builder.BuildAsync(startup.Token);
        await aspire.StartAsync(startup.Token);
        // Healthy means Aspire has created the database: nothing in the API does.
        await aspire.ResourceNotifications.WaitForResourceHealthyAsync("workstream-db", startup.Token);
        var connectionString = await aspire.GetConnectionStringAsync("workstream-db", startup.Token)
            ?? throw new InvalidOperationException("Aspire gave no connection string for workstream-db.");

        api = BuildApi(connectionString);
        api.Urls.Add(Origin.GetLeftPart(UriPartial.Authority));
        await api.ApplyWorkstreamSchemaAsync();
        await api.StartAsync();
        context.WriteLine($"API on {Origin}, database {Database} from Aspire on SQL port {SqlPort}.");
    }

    [AssemblyCleanup]
    public static async Task StopAsync()
    {
        if (api is not null) await api.DisposeAsync();
        if (aspire is not null) await aspire.DisposeAsync();
    }

    /// <summary>The API as Program.cs builds it, against the test database.</summary>
    static WebApplication BuildApi(string connectionString)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration["PUBLIC_ORIGIN"] = Origin.GetLeftPart(UriPartial.Authority);
        builder.Configuration["WEB_ORIGIN"] = WebOrigin;
        builder.Configuration["CONNECTION_STRING"] = connectionString;
        // As the AppHost runs it: an email and a password, so a journey needs no inbox.
        builder.Configuration["Workstream:Auth:PasswordSignIn"] = "true";
        builder.Configuration["Workstream:Administrator:Email"] = AdministratorEmail;
        builder.Configuration["Workstream:Administrator:Password"] = AdministratorPassword;
        // The desktops are played here; a desktop is told this API's address.
        builder.Configuration["Workstream:Desktops:Host"] = "none";
        builder.Configuration["Workstream:Desktops:ApiUrl"] = Origin.GetLeftPart(UriPartial.Authority);
        // Room for every spare a journey makes; the journeys about the limits lower them.
        builder.Configuration["Workstream:Desktops:Spares:Enabled"] = "true";
        builder.Configuration["Workstream:Desktops:Spares:Max"] = "1000";
        builder.Configuration["Workstream:Desktops:Spares:HoursPerDay"] = "100000";
        // The model: Foundry is played in this process, under the gateway's own client.
        builder.Configuration["Workstream:Model:Endpoint"] = "http://foundry.test/anthropic";
        builder.Configuration["Workstream:Model:Key"] = ModelKey;
        // Every journey invites from this one address; SqlOS's hourly caps are for the public.
        builder.Configuration["SqlOS:Invitations:MaxInvitationsPerIpPerHour"] = "100000";
        builder.AddWorkstreamApi();
        builder.Services.AddSingleton<IDesktopHost>(Desktops);
        builder.Services.AddHttpClient(ModelGateway.ClientName).ConfigurePrimaryHttpMessageHandler(() => Model);
        var app = builder.Build();
        app.MapWorkstreamApi();
        return app;
    }

    static int Port(string variable, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(variable), out var port) ? port : fallback;
}
