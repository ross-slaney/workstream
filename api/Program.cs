using Workstream.Api;

// Settings (PUBLIC_ORIGIN, WEB_ORIGIN, CONNECTION_STRING and the Workstream:* values) arrive as environment
// variables: from the AppHost in development, from infra/main.bicep when deployed. This process is the API,
// and it runs the background jobs too where Workstream:Jobs:InProcess is on. The web app is its own container.

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = Directory.GetCurrentDirectory(),
});
// Services and routes are shared with the tests' in-process host (Workstream.cs, WorkstreamApi.cs).
builder.AddWorkstreamApi();

var app = builder.Build();
app.MapWorkstreamApi();

await app.ApplyWorkstreamSchemaAsync();
app.Run();
