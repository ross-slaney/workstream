using EFCore.SchemaSync;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SqlOS;
using Workstream.Api.Domain;

namespace Workstream.Api;

/// <summary>
/// Persistence for the domain in <c>Domain/</c>: tables, keys and indexes. No behavior lives here.
/// <see cref="SqlOSDbContext{TContext}"/> also syncs every <c>ISqlOSResourceEntity</c> (team, environment, task)
/// to its node in the same SaveChanges.
/// </summary>
public sealed class WorkstreamDbContext(DbContextOptions<WorkstreamDbContext> options)
    : SqlOSDbContext<WorkstreamDbContext>(options), IDataProtectionKeyContext
{
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<WorkEnvironment> Environments => Set<WorkEnvironment>();
    public DbSet<WorkTask> Tasks => Set<WorkTask>();
    public DbSet<TaskMessage> TaskMessages => Set<TaskMessage>();
    public DbSet<DesktopRun> DesktopRuns => Set<DesktopRun>();
    public DbSet<Spare> Spares => Set<Spare>();
    public DbSet<EnvironmentSetup> Setups => Set<EnvironmentSetup>();
    public DbSet<ModelUsage> ModelUsage => Set<ModelUsage>();

    /// <summary>What a new team may use, from configuration (Workstream:Teams). Set once at start.</summary>
    internal static decimal DefaultModelAllowanceUsd { get; set; } = 20m;
    internal static int DefaultMaxRunningDesktops { get; set; } = 2;

    protected override void OnApplicationModelCreating(ModelBuilder model)
    {
        // SqlOS checks each node through its ResourceId. On a protected table it also keeps a copy of every
        // other index under each ancestor node (indexes that contain ResourceId, or are exactly a foreign key,
        // are exempt), so each table below carries only the index its list is ordered by.
        model.Entity<Team>(team =>
        {
            team.ToTable("Teams");
            team.HasIndex(x => x.ResourceId).IsUnique();
            // "My teams", oldest first.
            team.HasIndex(x => x.CreatedAt);
            team.Property(x => x.Id).HasMaxLength(100);
            team.Property(x => x.ResourceId).HasMaxLength(128);
            team.Property(x => x.ModelAllowanceUsd).HasPrecision(18, 2);
        });

        model.Entity<WorkEnvironment>(environment =>
        {
            environment.ToTable("Environments");
            environment.HasIndex(x => x.ResourceId).IsUnique();
            // A team's environments, A to Z.
            environment.HasIndex(x => x.Name);
            environment.HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Restrict);
            environment.Property(x => x.Id).HasMaxLength(36);
            environment.Property(x => x.TeamId).HasMaxLength(100);
            environment.Property(x => x.ResourceId).HasMaxLength(128);
            environment.Property(x => x.Name).HasMaxLength(100);
            environment.Property(x => x.Size).HasMaxLength(20);
            environment.Property(x => x.RepositoryUrl).HasMaxLength(500);
            environment.Property(x => x.RepositoryBranch).HasMaxLength(200);
            environment.Property(x => x.CreatedBy).HasMaxLength(450);
            environment.Property(x => x.SetupInputs).HasMaxLength(64);
            environment.Ignore(x => x.HasSetup);
        });

        model.Entity<WorkTask>(task =>
        {
            task.ToTable("Tasks");
            task.HasIndex(x => x.ResourceId).IsUnique();
            // A team's tasks, newest first.
            task.HasIndex(x => x.CreatedAt);
            task.HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Restrict);
            task.HasOne<WorkEnvironment>().WithMany().HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Restrict);
            task.Property(x => x.Id).HasMaxLength(36);
            task.Property(x => x.TeamId).HasMaxLength(100);
            task.Property(x => x.EnvironmentId).HasMaxLength(36);
            task.Property(x => x.ResourceId).HasMaxLength(128);
            task.Property(x => x.Title).HasMaxLength(WorkTask.MaxTitleLength);
            task.Property(x => x.CreatedBy).HasMaxLength(450);
            task.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
            task.Property(x => x.Failure).HasMaxLength(500);
            task.Property(x => x.Size).HasMaxLength(20);
            task.Property(x => x.Host).HasMaxLength(100);
            task.Property(x => x.Address).HasMaxLength(100);
            task.Ignore(x => x.IsRunning);
            task.Ignore(x => x.Step);
            task.Ignore(x => x.CanBeWatched);
        });

        model.Entity<TaskMessage>(message =>
        {
            message.ToTable("TaskMessages");
            message.HasKey(x => x.Id);
            message.Property(x => x.Id).UseIdentityColumn();
            // A send tried again writes once. The index contains ResourceId (the task's node), so SqlOS treats it
            // as a lookup and keeps no copy of it.
            message.HasIndex(x => new { x.ResourceId, x.ClientKey }).IsUnique();
            message.HasOne<WorkTask>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Restrict);
            message.Property(x => x.TaskId).HasMaxLength(36);
            message.Property(x => x.ResourceId).HasMaxLength(128);
            message.Property(x => x.AuthorId).HasMaxLength(450);
            message.Property(x => x.Author).HasConversion<string>().HasMaxLength(20);
            message.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            message.Property(x => x.ClientKey).HasMaxLength(36);
        });

        model.Entity<DesktopRun>(run =>
        {
            run.ToTable("DesktopRuns");
            run.Property(x => x.Id).HasMaxLength(36);
            run.Property(x => x.TaskId).HasMaxLength(36);
            run.Property(x => x.TeamId).HasMaxLength(100);
            run.Property(x => x.Size).HasMaxLength(20);
            // Rows from before a run said what it was for were all tasks' runs, made from the image.
            run.Property(x => x.Purpose).HasMaxLength(20).HasDefaultValue(MachinePurpose.Task);
            run.Property(x => x.Source).HasMaxLength(20).HasDefaultValue(StartSource.Image);
            run.HasIndex(x => new { x.TeamId, x.StartedAt });
            run.HasIndex(x => new { x.TaskId, x.EndedAt });
            run.HasIndex(x => x.EndedAt);
        });

        model.Entity<Spare>(spare =>
        {
            spare.ToTable("Spares");
            spare.Property(x => x.Id).HasMaxLength(36);
            spare.Property(x => x.TeamId).HasMaxLength(100);
            spare.Property(x => x.EnvironmentId).HasMaxLength(36);
            spare.Property(x => x.Size).HasMaxLength(20);
            spare.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
            spare.Property(x => x.Host).HasMaxLength(100);
            spare.Property(x => x.Address).HasMaxLength(100);
            spare.Property(x => x.Save).HasMaxLength(100);
            // A team has one spare at most: two people opening the workspace at once make one.
            spare.HasIndex(x => x.TeamId).IsUnique();
            spare.HasOne<Team>().WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<EnvironmentSetup>(setup =>
        {
            setup.ToTable("EnvironmentSetups");
            setup.HasKey(x => x.EnvironmentId);
            setup.Property(x => x.EnvironmentId).HasMaxLength(36);
            setup.Property(x => x.TeamId).HasMaxLength(100);
            setup.Property(x => x.Save).HasMaxLength(100);
            setup.Property(x => x.SaveInputs).HasMaxLength(64);
            setup.Property(x => x.SaveImage).HasMaxLength(40);
            setup.Property(x => x.SaveCommit).HasMaxLength(64);
            setup.Property(x => x.SaveCommitPending).HasMaxLength(64);
            // Whoever begins or ends an attempt changes this, and only from what it read: two never both win.
            setup.Property(x => x.RunId).HasMaxLength(36).IsConcurrencyToken();
            setup.Property(x => x.RunHost).HasMaxLength(100);
            setup.Property(x => x.RunInputs).HasMaxLength(64);
            setup.Property(x => x.RunImage).HasMaxLength(40);
            setup.Property(x => x.Output).HasMaxLength(EnvironmentSetup.MaxOutputLength);
            setup.Property(x => x.Failure).HasMaxLength(500);
            setup.Property(x => x.FailedInputs).HasMaxLength(64);
            setup.Ignore(x => x.IsPreparing);
            // It goes with its environment.
            setup.HasOne<WorkEnvironment>().WithOne().HasForeignKey<EnvironmentSetup>(x => x.EnvironmentId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ModelUsage>(usage =>
        {
            usage.ToTable("ModelUsage");
            usage.HasKey(x => x.Id);
            usage.Property(x => x.Id).UseIdentityColumn();
            usage.Property(x => x.TeamId).HasMaxLength(100);
            usage.Property(x => x.TaskId).HasMaxLength(36);
            usage.Property(x => x.Model).HasMaxLength(100);
            usage.Property(x => x.CostUsd).HasPrecision(18, 6);
            usage.HasIndex(x => new { x.TeamId, x.At });
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        SaveWithProvisioningAsync(acceptAllChangesOnSuccess, synchronous: true, CancellationToken.None).GetAwaiter().GetResult();

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default) =>
        SaveWithProvisioningAsync(acceptAllChangesOnSuccess, synchronous: false, ct);

    // Identities, memberships, nodes and grants commit together: provisioning runs before SqlOS's own save.
    async Task<int> SaveWithProvisioningAsync(bool acceptAllChangesOnSuccess, bool synchronous, CancellationToken ct)
    {
        await IdentityProvisioning.SynchronizeAsync(this, ct);
        return synchronous ? base.SaveChanges(acceptAllChangesOnSuccess) : await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
    }
}

public static class WorkstreamSchema
{
    /// <summary>
    /// The model is the schema: EFCore.SchemaSync compares it with the live database and applies the difference,
    /// with data-loss and object-removal protection on. No migrations. The app owns dbo, where its model and
    /// SqlOS's live; Hangfire keeps its jobs in its own schema, which schema sync never touches.
    /// </summary>
    public static Task<SchemaSyncResult> ApplyWorkstreamSchemaAsync(this WebApplication app)
    {
        var teams = app.Configuration.GetSection("Workstream:Teams");
        WorkstreamDbContext.DefaultModelAllowanceUsd = teams.GetValue<decimal?>("ModelAllowanceUsd") ?? WorkstreamDbContext.DefaultModelAllowanceUsd;
        WorkstreamDbContext.DefaultMaxRunningDesktops = teams.GetValue<int?>("MaxRunningDesktops") ?? WorkstreamDbContext.DefaultMaxRunningDesktops;
        return app.Services.ApplyDatabaseSchemaAsync<WorkstreamDbContext>(options => options.ManagedSchemas.Add("dbo"));
    }
}
