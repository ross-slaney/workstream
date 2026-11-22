using System.Data.Common;
using dotenv.net;

var builder = DistributedApplication.CreateBuilder(args);

// Settings come from the root .env, where there is one (.env.example lists them). Arguments and environment
// variables win, which is how the tests and a second copy run this same AppHost on ports of their own. Without
// a .env the stack still runs, on the defaults below; only the agent's model needs a value nobody can default.
var dotEnv = DotEnv.Read(new DotEnvOptions(
    envFilePaths: [Path.Combine(builder.AppHostDirectory, "..", ".env")],
    ignoreExceptions: true));
string Setting(string name, string fallback) =>
    builder.Configuration[name] is { Length: > 0 } value ? value
    : dotEnv.TryGetValue(name, out var fromFile) && fromFile.Length > 0 ? fromFile
    : fallback;

var apiOrigin = Setting("PUBLIC_ORIGIN", "http://localhost:5280");
// Where the web app's dev server answers; the API admits this one browser origin.
var webOrigin = Setting("WEB_ORIGIN", "http://localhost:8091");
// The local SQL Server container's own password: it protects nothing outside this computer.
var sql = ParseSql(Setting("CONNECTION_STRING", "Server=localhost,1454;Database=Workstream;User Id=sa;Password=Workstream-dev-1;TrustServerCertificate=True"));
var apiPort = new Uri(apiOrigin).Port;

var sqlPassword = builder.AddParameter("sql-password", value: sql.Password, secret: true);
var sqlServer = builder
    .AddSqlServer("sql", password: sqlPassword, port: sql.Port)
    .WithImageTag("2025-latest")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume();

// The AppHost owns the database: once SQL Server is up, AddDatabase creates it if it does not exist, and the
// API waits for that. The API only manages the schema inside it.
var db = sqlServer.AddDatabase("workstream-db", sql.Database);

var api = builder
    .AddProject<Projects.Workstream_Api>("api", options => options.ExcludeLaunchProfile = true)
    .WithHttpEndpoint(port: apiPort, targetPort: apiPort, isProxied: false)
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    // Listen on every interface: a desktop running in Docker reaches this API through the Docker host.
    .WithEnvironment("ASPNETCORE_URLS", "http://0.0.0.0:" + apiPort)
    .WithEnvironment("CONNECTION_STRING", db.Resource.ConnectionStringExpression)
    .WithEnvironment("PUBLIC_ORIGIN", apiOrigin)
    .WithEnvironment("WEB_ORIGIN", webOrigin)
    // Development signs in with an email and a password, so nobody needs an inbox.
    .WithEnvironment("Workstream__Auth__PasswordSignIn", "true")
    .WithEnvironment("Workstream__Administrator__Email", Setting("WORKSTREAM_ADMIN_EMAIL", "admin@workstream.test"))
    .WithEnvironment("Workstream__Administrator__Password", Setting("WORKSTREAM_ADMIN_PASSWORD", "Workstream-dev-1"))
    // There is no worker here: the API runs the background jobs itself.
    .WithEnvironment("Workstream__Jobs__InProcess", "true")
    // Desktops are Docker containers on this computer (desktop/Dockerfile), which reach the API through the Docker host.
    .WithEnvironment("Workstream__Desktops__Host", Setting("WORKSTREAM_DESKTOP_HOST", "docker"))
    .WithEnvironment("Workstream__Desktops__ApiUrl", Setting("WORKSTREAM_DESKTOP_API_URL", "http://host.docker.internal:" + apiPort))
    .WithEnvironment("Workstream__Desktops__IdleMinutes", Setting("WORKSTREAM_DESKTOP_IDLE_MINUTES", "120"))
    // The agent's models: Workstream's Foundry resource (README, "Models"). Without these the agent says models are not set up.
    .WithEnvironment("Workstream__Model__Endpoint", Setting("WORKSTREAM_MODEL_ENDPOINT", ""))
    .WithEnvironment("Workstream__Model__Key", Setting("WORKSTREAM_MODEL_KEY", ""))
    // Sign in with GitHub: a GitHub OAuth client of the developer's own whose callback is this API's
    // /sqlos/auth/oidc/callback (infra/README.md, "Sign in with GitHub"). Without both, the sign-in page
    // shows no GitHub button and desktops get no GitHub token.
    .WithEnvironment("SqlOS__Oidc__GitHub__ClientId", Setting("WORKSTREAM_GITHUB_OAUTH_CLIENT_ID", ""))
    .WithEnvironment("SqlOS__Oidc__GitHub__ClientSecret", Setting("WORKSTREAM_GITHUB_OAUTH_CLIENT_SECRET", ""))
    .WithUrl(apiOrigin, "API")
    .WithUrl($"{apiOrigin}/sqlos", "SqlOS")
    .WithUrl($"{apiOrigin}/openapi/v1.json", "OpenAPI")
    .WaitFor(db);

// The web app: Expo's dev server for client/ (`npm run web`), rendering pages on request the way the web
// container does, on the origin the API admits (WEB_ORIGIN).
var webPort = new Uri(webOrigin).Port;
builder
    .AddJavaScriptApp("web", "../client")
    // No package install from here: node_modules belongs to the developer (`npm ci` in client/ once).
    .WithNpm(install: false)
    .WithRunScript("web")
    // "--" so npm hands --port to the script rather than reading it as its own.
    .WithArgs("--", "--port", webPort.ToString())
    .WithHttpEndpoint(port: webPort, targetPort: webPort, isProxied: false)
    .WithEnvironment("EXPO_PUBLIC_API_URL", apiOrigin)
    .WithEnvironment("EXPO_PUBLIC_SQLOS_CLIENT_ID", "workstream-app")
    .WithEnvironment("EXPO_NO_TELEMETRY", "1")
    .WithUrl(webOrigin, "Web")
    .WaitFor(api);

builder.Build().Run();

static SqlSettings ParseSql(string connectionString)
{
    var parts = new DbConnectionStringBuilder { ConnectionString = connectionString };
    var server = RequirePart(parts, "Server", "Data Source");
    var comma = server.LastIndexOf(',');
    var port = comma >= 0 && int.TryParse(server[(comma + 1)..], out var parsed) ? parsed : 1433;
    return new SqlSettings(RequirePart(parts, "Password", "Pwd"), RequirePart(parts, "Database", "Initial Catalog"), port);
}

static string RequirePart(DbConnectionStringBuilder parts, params string[] keys) =>
    keys.Select(key => parts.TryGetValue(key, out var value) ? value as string : null)
        .FirstOrDefault(value => value is { Length: > 0 })
    ?? throw new InvalidOperationException($"CONNECTION_STRING must include {keys[0]}.");

sealed record SqlSettings(string Password, string Database, int Port);
