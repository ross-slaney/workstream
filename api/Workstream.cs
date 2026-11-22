using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SqlOS.AuthServer.Authentication;
using SqlOS.AuthServer.Configuration;
using SqlOS.Configuration;
using SqlOS.Extensions;
using Workstream.Api.Desktops;
using Workstream.Api.GitHub;
using Workstream.Api.Model;

namespace Workstream.Api;

/// <summary>
/// The two origins the product lives on, and the two OAuth clients: the web app's and the command line's. The
/// API (this host, SqlOS included) answers on <see cref="Origin"/>; the web app is served from
/// <see cref="WebOrigin"/> and signs in against this issuer with SqlOS's headless flow. The command line
/// (cli/workstream.mjs) signs in with SqlOS's device flow, approved in the web app's <see cref="DevicePath"/>.
/// </summary>
public sealed class WorkstreamApp
{
    public const string Name = "Workstream";
    /// <summary>The app's client. Keep in sync with EXPO_PUBLIC_SQLOS_CLIENT_ID (client/.env).</summary>
    public const string AppClientId = "workstream-app";
    /// <summary>The command line's client: public, device flow only, reading and starting tasks. Keep in sync with cli/workstream.mjs.</summary>
    public const string CliClientId = "workstream-cli";
    /// <summary>Where the web app lands after a provider redirect, under <see cref="WebOrigin"/>.</summary>
    public const string WebRedirectPath = "/auth/callback";
    /// <summary>The GitHub OAuth client people sign in with: its id and secret, set together or not at all.</summary>
    public const string GitHubSignInClientId = "SqlOS:Oidc:GitHub:ClientId";
    public const string GitHubSignInClientSecret = "SqlOS:Oidc:GitHub:ClientSecret";
    /// <summary>The key SqlOS seeds the GitHub connection under, which a person's GitHub token is read by.</summary>
    public const string GitHubConnectionKey = "github";
    /// <summary>
    /// What GitHub is asked for: the person's profile and verified address, to know who they are, and their
    /// repositories, so the environment form lists them and the desktops of their tasks push as them.
    /// </summary>
    public static readonly IReadOnlyList<string> GitHubScopes = ["read:user", "user:email", "repo"];
    /// <summary>The web app's sign-in (client/app/sign-in.tsx), under <see cref="WebOrigin"/>.</summary>
    public const string SignInPath = "/sign-in";
    /// <summary>Where an invitation is accepted (client/app/invite.tsx), under <see cref="WebOrigin"/>, with <c>?token=</c>.</summary>
    public const string InvitePath = "/invite";
    /// <summary>Where a person approves the command line's sign-in (client/app/device.tsx), under <see cref="WebOrigin"/>, with <c>?request=</c>.</summary>
    public const string DevicePath = "/device";
    /// <summary>Where an invitation to a shared task is taken (client/app/share.tsx), under <see cref="WebOrigin"/>, with <c>?token=</c>.</summary>
    public const string SharePath = "/share";
    public const string ApiPath = "/api";
    /// <summary>Required by every /api route. Keep in sync with AUTH_SCOPE in client/lib/auth.ts.</summary>
    public const string ReadScope = "workstream.read";
    /// <summary>Required by every /api route that changes something.</summary>
    public const string WriteScope = "workstream.write";
    /// <summary>
    /// Starting a task and nothing else that changes something: the command line's one change
    /// (<see cref="WorkstreamApi.AllowsScope"/> on the route).
    /// </summary>
    public const string StartScope = "workstream.start";

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
    /// <summary>Every scope the application knows.</summary>
    public IReadOnlyList<string> Scopes { get; }
    /// <summary>The web app's: read and change everything the person may.</summary>
    public IReadOnlyList<string> AppScopes { get; }
    /// <summary>The command line's: read what the person may, and start tasks.</summary>
    public IReadOnlyList<string> CliScopes { get; }
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
    /// <summary>
    /// Whether people can sign in with GitHub here: a GitHub OAuth client is configured
    /// (<see cref="SeedGitHubSignIn"/>). Without it nobody has a GitHub token, and the app says so instead of
    /// asking people to sign in with GitHub.
    /// </summary>
    public bool GitHubSignIn { get; }

    WorkstreamApp(string origin, string webOrigin, string? sender, string? emailConnectionString, bool passwordSignIn, string? administratorEmail, string? administratorPassword,
        bool gitHubSignIn)
    {
        GitHubSignIn = gitHubSignIn;
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
        AppScopes = ["openid", "profile", "email", "offline_access", ReadScope, WriteScope];
        CliScopes = ["openid", "profile", "email", "offline_access", ReadScope, StartScope];
        Scopes = [.. AppScopes.Union(CliScopes)];
    }

    public static WorkstreamApp From(IConfiguration configuration)
    {
        var origin = (configuration["PUBLIC_ORIGIN"] ?? "http://localhost:5280").TrimEnd('/');
        var webOrigin = (configuration["WEB_ORIGIN"] ?? "http://localhost:8091").TrimEnd('/');
        var administrator = configuration["Workstream:Administrator:Email"] is { } email && email.Contains('@') ? email.Trim() : null;
        return new WorkstreamApp(origin, webOrigin,
            configuration["SqlOS:EmailOtp:FromAddress"], configuration["SqlOS:EmailOtp:AzureCommunicationServicesConnectionString"],
            configuration.GetValue<bool>("Workstream:Auth:PasswordSignIn"), administrator, configuration["Workstream:Administrator:Password"],
            !string.IsNullOrWhiteSpace(configuration[GitHubSignInClientId]) && !string.IsNullOrWhiteSpace(configuration[GitHubSignInClientSecret]));
    }

    /// <summary>One public PKCE client. The web app starts the headless flow itself and exchanges the code itself.</summary>
    public void ConfigureAppClient(SqlOSClientSeedOptions client)
    {
        client.ClientId = AppClientId;
        client.Name = Name;
        client.Description = "The web app.";
        client.Audience = Resource;
        client.RedirectUris.Add($"{WebOrigin}{WebRedirectPath}");
        client.AllowedScopes.AddRange(AppScopes);
        client.ClientType = "public_pkce";
        client.RequirePkce = true;
        client.IsFirstParty = true;
        client.AllowNativeHeadlessAuth = true;
    }

    /// <summary>
    /// The command line's client, a public one with no secret and no redirect: SqlOS's device flow. The person
    /// opens the link it prints, signs in on the web app's device page and approves; the command line then holds
    /// a session of the person's own, for this API, that can read and start tasks (docs/core-model.md, "The
    /// command line").
    /// </summary>
    public void SeedCliClient(SqlOSAuthServerOptions auth) =>
        auth.SeedCliClient(CliClientId, "Workstream command line", Resource, [.. CliScopes]);

    /// <summary>
    /// Where GitHub sends a person back after they sign in there: SqlOS's own address for every provider,
    /// under this API. It is the "Authorization callback URL" of the GitHub OAuth client (infra/README.md,
    /// "Sign in with GitHub").
    /// </summary>
    public string ProviderCallback => $"{Issuer}/oidc/callback";

    /// <summary>
    /// Sign in with GitHub beside the emailed code, where a GitHub OAuth client is configured
    /// (<see cref="GitHubSignInClientId"/> and <see cref="GitHubSignInClientSecret"/>). SqlOS owns the whole
    /// of it: the connection, the round trip to GitHub, and who the person is. It asks for
    /// <see cref="GitHubScopes"/>: their GitHub profile and verified primary email, and their repositories. It
    /// keeps the token GitHub gives it, which <see cref="SqlOSGitHubTokens"/> reads. Someone whose address
    /// already has an account is that person, and someone new gets what a new person gets
    /// (IdentityProvisioning.cs). Without the two settings nothing is seeded, the sign-in page lists no
    /// provider and shows no button. Answers whether it is on.
    /// </summary>
    public bool SeedGitHubSignIn(SqlOSAuthServerOptions auth, IConfiguration configuration)
    {
        var clientId = configuration[GitHubSignInClientId]?.Trim();
        var clientSecret = configuration[GitHubSignInClientSecret]?.Trim();
        if (string.IsNullOrEmpty(clientId) && string.IsNullOrEmpty(clientSecret)) return false;
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            throw new InvalidOperationException($"Set both {GitHubSignInClientId} and {GitHubSignInClientSecret}, or leave both unset.");
        }
        auth.SeedGitHubConnection(clientId, clientSecret, [ProviderCallback], GitHubScopes, storeProviderTokens: true);
        return true;
    }

    /// <summary>
    /// Where SqlOS sends a browser that reaches one of its hosted views: the web app's invite page for an
    /// invitation email's link, its device page for the command line's sign-in, its sign-in for anything else.
    /// Someone back from GitHub who is on several teams lands on the sign-in page with the request, the
    /// organization view and SqlOS's pending token, and the page picks the team (client/hooks/use-sign-in.ts).
    /// Someone who comes back from GitHub without being signed in (GitHub or SqlOS refused, or SqlOS has a
    /// question left) lands on the sign-in page with the request they began; the page says that signing in with
    /// GitHub did not work and shows the form again (client/app/sign-in.tsx). SqlOS's own words for the
    /// refusal stay out of the address, and so do its tokens but the one pending token above.
    /// </summary>
    public string SignInUiUrl(SqlOSHeadlessUiRouteContext ui)
    {
        if (ui.View == "invite" && ui.UiContext?["invitationToken"]?.GetValue<string>() is { Length: > 0 } token)
        {
            return $"{WebOrigin}{InvitePath}?token={Uri.EscapeDataString(token)}";
        }
        if (ui.View.StartsWith("device", StringComparison.Ordinal))
        {
            var request = string.IsNullOrEmpty(ui.RequestId) ? "" : $"request={Uri.EscapeDataString(ui.RequestId)}&";
            return $"{WebOrigin}{DevicePath}?{request}view={Uri.EscapeDataString(ui.View)}";
        }
        if (!string.IsNullOrEmpty(ui.RequestId) && ui.View == "organization" && !string.IsNullOrEmpty(ui.PendingToken) && string.IsNullOrEmpty(ui.Error))
        {
            // Signed in by GitHub, and on several teams: SqlOS asks which one the session is for. The page answers
            // as it does after an emailed code, with the first, and needs SqlOS's pending token to (the browser's
            // cookie on this API's address does not reach a page on another site). The token is good once, for
            // this request, and what it leads to is a code only this tab's PKCE verifier can redeem.
            return $"{WebOrigin}{SignInPath}?request={Uri.EscapeDataString(ui.RequestId)}&view=organization&pendingToken={Uri.EscapeDataString(ui.PendingToken)}";
        }
        if (!string.IsNullOrEmpty(ui.RequestId) && (!string.IsNullOrEmpty(ui.Error) || !string.IsNullOrEmpty(ui.PendingToken)))
        {
            return $"{WebOrigin}{SignInPath}?request={Uri.EscapeDataString(ui.RequestId)}&view={Uri.EscapeDataString(ui.View)}";
        }
        return $"{WebOrigin}{SignInPath}";
    }

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
        builder.AddGitHub();
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
                app.SeedCliClient(options.AuthServer);
                app.SeedGitHubSignIn(options.AuthServer, builder.Configuration);
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
