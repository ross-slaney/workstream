using Hangfire;
using Hangfire.SqlServer;
using Workstream.Api.Desktops;
using Workstream.Api.Email;

namespace Workstream.Api;

/// <summary>
/// Background work, on Hangfire with its jobs in the app database (schema <c>hangfire</c>, installed by Hangfire
/// at first start): starting, stopping and deleting desktops as people ask, and the sweep every five minutes
/// (<see cref="DesktopJobs"/>); writing down what every team keeps every fifteen minutes (<see cref="StorageJobs"/>);
/// and the email to a task's people when the agent's turn ends (<see cref="TaskEmails"/>). Every process can queue a job; a process runs them where
/// <c>Workstream:Jobs:InProcess</c> is on, which is the API itself in development and when deployed. The tests
/// run no server and call the jobs directly.
/// </summary>
public static class Jobs
{
    public static bool RunHere(IConfiguration configuration) => configuration.GetValue<bool>("Workstream:Jobs:InProcess");

    public static WebApplicationBuilder AddWorkstreamJobs(this WebApplicationBuilder builder, string connectionString)
    {
        builder.Services.AddTransient<DesktopJobs>();
        builder.Services.AddTransient<StorageJobs>();
        builder.Services.AddTransient<TaskEmails>();
        // BuildJobs is registered with the builds (Builds/Builds.cs).
        // Storage is made when first resolved: by the server below, or by whoever queues a job.
        builder.Services.AddHangfire(configuration => configuration
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseSqlServerStorage(connectionString, new SqlServerStorageOptions
            {
                SchemaName = "hangfire",
                PrepareSchemaIfNecessary = true,
                QueuePollInterval = TimeSpan.Zero,
                UseRecommendedIsolationLevel = true,
                DisableGlobalLocks = true,
            }));
        if (!RunHere(builder.Configuration)) return builder;
        // Making a machine holds a worker for a minute or two, so there are enough for several at once.
        builder.Services.AddHangfireServer(options => options.WorkerCount = 8);
        builder.Services.AddHostedService<RecurringJobsHostedService>();
        return builder;
    }
}

/// <summary>Declares the recurring jobs once the server is up. Idempotent: the same id and schedule every start.</summary>
public sealed class RecurringJobsHostedService(IRecurringJobManager recurring) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        recurring.AddOrUpdate<DesktopJobs>(DesktopJobs.SweepId, job => job.SweepAsync(CancellationToken.None), DesktopJobs.SweepCron);
        recurring.AddOrUpdate<Builds.BuildJobs>(Builds.BuildJobs.SweepId, job => job.SweepAsync(CancellationToken.None), Builds.BuildJobs.SweepCron);
        recurring.AddOrUpdate<StorageJobs>(StorageJobs.ReconcileId, job => job.ReconcileAsync(CancellationToken.None), StorageJobs.ReconcileCron);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
