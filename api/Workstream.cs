using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Authentication;
using SqlOS.AuthServer.Configuration;
using SqlOS.Configuration;
using SqlOS.Extensions;
using Workstream.Api.Desktops;
using Workstream.Api.Model;

namespace Workstream.Api;

/// <summary>
/// The two origins the product lives on, and the one OAuth client the app uses. The API (this host, SqlOS
/// included) answers on <see cref="Origin"/>; the web app is served from <see cref="WebOrigin"/> and signs in
/// against this issuer with SqlOS's headless flow.
/// </summary>
public sealed class WorkstreamApp
{
    public const string Name = "Workstream";
    /// <summary>The app's client. Keep in sync with EXPO_PUBLIC_SQLOS_CLIENT_ID (client/.env).</summary>
    public const string AppClientId = "workstream-app";
    /// <summary>Where the web app lands after a provider redirect, under <see cref="WebOrigin"/>.</summary>
    public const string WebRedirectPath = "/auth/callback";
    /// <summary>The web app's sign-in (client/app/sign-in.tsx), under <see cref="WebOrigin"/>.</summary>
    public const string SignInPath = "/sign-in";
    /// <summary>Where an invitation is accepted (client/app/invite.tsx), under <see cref="WebOrigin"/>, with <c>?token=</c>.</summary>
    public const string InvitePath = "/invite";
    /// <summary>Where an invitation to a shared task is taken (client/app/share.tsx), under <see cref="WebOrigin"/>, with <c>?token=</c>.</summary>
    public const string SharePath = "/share";
    public const string ApiPath = "/api";
    /// <summary>Required by every /api route. Keep in sync with AUTH_SCOPE in client/lib/auth.ts.</summary>
    public const string ReadScope = "workstream.read";
    /// <summary>Required by every /api route that changes something.</summary>
    public const string WriteScope = "workstream.write";

    /// <summary>The API's own origin (PUBLIC_ORIGIN).</summary>
    public string Origin { get; }
    /// <summary>The web app's origin (WEB_ORIGIN). The only origin CORS admits outside development.</summary>
    public string WebOrigin { get; }
    /// <summary>
    /// The address sign-in codes and invitations come from (<c>SqlOS:EmailOtp:FromAddress</c>), where email is set up:
    /// the address and the Azure Communication Services connection string are configured together or not at all.
    /// Null in development, which signs in with a password and hands an invitation's link to whoever made it.
    /// </summary>
    public string? Sender { get; }
    /// <summary>The Azure Communication Services resource email is sent through. Null where <see cref="Sender"/> is.</summary>
    public string? EmailConnectionString { get; }
    /// <summary>Token audience: {Origin}/api, which SqlOS derives from the application's Api path.</summary>
    public string Resource { get; }
    public string Issuer { get; }
    public IReadOnlyList<string> Scopes { get; }
    /// <summary>
    /// Sign in with an email and a password beside the emailed code (<c>Workstream:Auth:PasswordSignIn</c>).
    /// On in development and the tests, so nobody needs an inbox; off in production.
    /// </summary>
    public bool PasswordSignIn { get; }
    /// <summary>
    /// The system administrator's address (<c>Workstream:Administrator:Email</c>): the one member of
    /// <c>system_admins</c>, kept so at every start. Null where none is set: then nobody reaches /admin.
    /// </summary>
    public string? AdministratorEmail { get; }
    /// <summary>The administrator's password, only where people sign in with one. Null in production.</summary>
    public string? AdministratorPassword { get; }

    WorkstreamApp(string origin, string webOrigin, string? sender, string? emailConnectionString, bool passwordSignIn, string? administratorEmail, string? administratorPassword)
    {
        Origin = origin;
        WebOrigin = webOrigin;
        var email = !string.IsNullOrWhiteSpace(sender) && !string.IsNullOrWhiteSpace(emailConnectionString);
        Sender = email ? sender!.Trim() : null;
        EmailConnectionString = email ? emailConnectionString : null;
        PasswordSignIn = passwordSignIn;
        AdministratorEmail = administratorEmail;
        AdministratorPassword = passwordSignIn && administratorEmail is not null && !string.IsNullOrEmpty(administratorPassword) ? administratorPassword : null;
        Resource = $"{origin}{ApiPath}";
        Issuer = $"{origin}/sqlos/auth";
        Scopes = ["openid", "profile", "email", "offline_access", ReadScope, WriteScope];
    }

    public static WorkstreamApp From(IConfiguration configuration)
    {
        var origin = (configuration["PUBLIC_ORIGIN"] ?? "http://localhost:5280").TrimEnd('/');
        var webOrigin = (configuration["WEB_ORIGIN"] ?? "http://localhost:8091").TrimEnd('/');
        var administrator = configuration["Workstream:Administrator:Email"] is { } email && email.Contains('@') ? email.Trim() : null;
        return new WorkstreamApp(origin, webOrigin,
            configuration["SqlOS:EmailOtp:FromAddress"], configuration["SqlOS:EmailOtp:AzureCommunicationServicesConnectionString"],
            configuration.GetValue<bool>("Workstream:Auth:PasswordSignIn"), administrator, configuration["Workstream:Administrator:Password"]);
    }

    /// <summary>One public PKCE client. The web app starts the headless flow itself and exchanges the code itself.</summary>
    public void ConfigureAppClient(SqlOSClientSeedOptions client)
    {
        client.ClientId = AppClientId;
        client.Name = Name;
        client.Description = "The web app.";
        client.Audience = Resource;
        client.RedirectUris.Add($"{WebOrigin}{WebRedirectPath}");
        client.AllowedScopes.AddRange(Scopes);
        client.ClientType = "public_pkce";
        client.RequirePkce = true;
        client.IsFirstParty = true;
        client.AllowNativeHeadlessAuth = true;
    }

    /// <summary>
    /// Where SqlOS sends a browser that reaches one of its hosted views: the web app's invite page for an
    /// invitation email's link, its sign-in for anything else.
    /// </summary>
    public string SignInUiUrl(SqlOSHeadlessUiRouteContext ui) =>
        ui.View == "invite" && ui.UiContext?["invitationToken"]?.GetValue<string>() is { Length: > 0 } token
            ? $"{WebOrigin}{InvitePath}?token={Uri.EscapeDataString(token)}"
            : $"{WebOrigin}{SignInPath}";

    public void BrandAuthPage(SqlOSAuthPageSeedOptions page)
    {
        page.PageTitle = Name;
        page.PageSubtitle = PasswordSignIn ? "Sign in with your email and password." : "Sign in with a code sent to your email.";
        // The light palette in client/components/theme/palette.ts (primary, background). It dresses SqlOS's emails.
        page.PrimaryColor = "#1F4FE0";
        page.AccentColor = "#1F4FE0";
        page.BackgroundColor = "#FFFFFF";
        page.EnablePasswordSignup = PasswordSignIn;
        page.EnabledCredentialTypes = PasswordSignIn ? ["password", "email_otp"] : ["email_otp"];
    }
}

public static class WorkstreamHosting
{
    /// <summary>
    /// The API's services. Program.cs and the tests' in-process host both call this, then
    /// <see cref="WorkstreamApi.MapWorkstreamApi"/>, so tests run what production runs.
    /// </summary>
    public static WebApplicationBuilder AddWorkstreamApi(this WebApplicationBuilder builder)
    {
        builder.Services.AddOpenApi();
        var app = WorkstreamApp.From(builder.Configuration);
        builder.Services.AddSingleton(app);
        builder.Services.AddSingleton(TimeProvider.System);
        // A desktop calls /api/desktop and /api/model with its key: the scheme beside SqlOS's.
        builder.Services.AddDesktopKeys();
        // The web app is the one browser origin that calls this host. Development admits any origin so the
        // dev server works on whatever port it was given.
        builder.Services.AddCors(options =>
            options.AddPolicy(WorkstreamApi.WebCorsPolicy, policy =>
                policy.SetIsOriginAllowed(origin => builder.Environment.IsDevelopment()
                        || string.Equals(origin, app.WebOrigin, StringComparison.OrdinalIgnoreCase))
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials()));

        var connectionString =
            builder.Configuration["CONNECTION_STRING"]
            ?? builder.Configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Set CONNECTION_STRING.");

        // Signs SqlOS's cookies and protects each environment's variables (Operations/Environments.cs).
        builder.Services.AddDataProtection()
            .SetApplicationName(WorkstreamApp.Name)
            .PersistKeysToDbContext<WorkstreamDbContext>();

        builder.AddDesktops();
        builder.AddModelGateway();
        builder.AddWorkstreamJobs(connectionString);
        return builder.AddWorkstreamSqlOS(app, connectionString);
    }

    /// <summary>SqlOS for this app: the auth server, the seeded client, and the authorization model in docs/core-model.md.</summary>
    public static WebApplicationBuilder AddWorkstreamSqlOS(this WebApplicationBuilder builder, WorkstreamApp app, string connectionString)
    {
        builder.AddSqlOS<WorkstreamDbContext>(
            db => db.UseSqlServer(connectionString),
            options =>
            {
                // SqlOS's own dashboard (/sqlos) is open in development and absent everywhere else.
                options.Dashboard.AuthMode = SqlOSDashboardAuthMode.DevelopmentOnly;
                options.AuthServer.PublicOrigin = app.Origin;
                options.AuthServer.Issuer = app.Issuer;
                options.AuthServer.DefaultAudience = app.Resource;
                options.AuthServer.EnableLocalPasswordAuth = app.PasswordSignIn;
                options.AuthServer.ConfigureEmailOtp(email =>
                {
                    // SqlOS takes the sender and its connection string together or not at all.
                    if (app.Sender is not null)
                    {
                        email.AzureCommunicationServicesConnectionString = app.EmailConnectionString;
                        email.FromAddress = app.Sender;
                    }
                    email.ApplicationName = WorkstreamApp.Name;
                    // SqlOS's per-hour caps protect the public sign-in; the tests, signing up every journey
                    // from one address, raise them through configuration.
                    email.MaxChallengesPerIpPerHour = builder.Configuration.GetValue<int?>("SqlOS:EmailOtp:MaxChallengesPerIpPerHour") ?? email.MaxChallengesPerIpPerHour;
                    email.MaxChallengesPerClientPerHour = builder.Configuration.GetValue<int?>("SqlOS:EmailOtp:MaxChallengesPerClientPerHour") ?? email.MaxChallengesPerClientPerHour;
                    email.MaxChallengesPerHour = builder.Configuration.GetValue<int?>("SqlOS:EmailOtp:MaxChallengesPerHour") ?? email.MaxChallengesPerHour;
                });
                options.AuthServer.ConfigureInvitations(invitations =>
                {
                    invitations.ApplicationName = WorkstreamApp.Name;
                    invitations.MaxInvitationsPerIpPerHour = builder.Configuration.GetValue<int?>("SqlOS:Invitations:MaxInvitationsPerIpPerHour") ?? invitations.MaxInvitationsPerIpPerHour;
                });
                options.AuthServer.ConfigureMfa(mfa => mfa.Enabled = false);
                if (app.Sender is not null)
                {
                    options.ConfigureEmail(email =>
                    {
                        email.AzureCommunicationServicesConnectionString = app.EmailConnectionString;
                        email.FromAddress = app.Sender;
                    });
                }

                // Describe the host once. Declaring Api derives the token audience ({Origin}/api) and the
                // `SqlOS` bearer scheme; the auth server and its headless endpoints are mapped at startup.
                options.ConfigureApplication(WorkstreamApp.Name, application =>
                {
                    application.Origin = app.Origin;
                    application.Api = WorkstreamApp.ApiPath;
                    application.AllowedScopes = [.. app.Scopes];
                    // The web app draws sign-in itself (@sqlos/headless), so a browser that reaches one of
                    // SqlOS's hosted views is handed to it.
                    application.Headless(headless => headless.BuildUiUrl = app.SignInUiUrl);
                    application.Brand(app.BrandAuthPage);
                    application.Authorization(WorkstreamAuthorization.Seed);
                });
                options.AuthServer.SeedClient(app.ConfigureAppClient);
            });

        // After SqlOS's own bootstrap: the system administrators and the desktop operator.
        builder.Services.AddHostedService<PlatformSeedHostedService>();

        // A declared scope is an enforced scope: a token granted without workstream.read gets 403.
        builder.Services.PostConfigure<SqlOSJwtOptions>(
            SqlOSJwtDefaults.AuthenticationScheme,
            jwt => jwt.RequiredScopes = [WorkstreamApp.ReadScope]);

        return builder;
    }
}
