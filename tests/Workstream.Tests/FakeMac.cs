using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workstream.Api;
using Workstream.Api.Builds;
using Workstream.Api.Domain;

namespace Workstream.Tests;

/// <summary>
/// The Mac that builds iPhone apps, played here. Asked to build, it only notes the build; a journey then
/// speaks as the Mac with its key, the way the workflow does (.github/workflows/ios-build.yml).
/// </summary>
public sealed class FakeMac : IMacBuilder
{
    /// <summary>The builds the API asked the Mac for.</summary>
    public ConcurrentQueue<string> Asked { get; } = new();

    /// <summary>Say why the Mac cannot be asked, to have asking fail.</summary>
    public string? Refuse { get; set; }

    public Task AskAsync(AppBuild build, CancellationToken ct)
    {
        if (Refuse is { } why) throw new InvalidOperationException(why);
        Asked.Enqueue(build.Id);
        return Task.CompletedTask;
    }

    /// <summary>Run the job that asks the Mac for a build, as the server would once the source is here.</summary>
    public static async Task AskForAsync(string buildId)
    {
        using var scope = Stack.Services.CreateScope();
        var attempt = await scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>().Builds.AsNoTracking().Where(x => x.Id == buildId).Select(x => x.Attempt).SingleAsync();
        await Stack.Builds.AskAsync(buildId, attempt, CancellationToken.None);
    }

    /// <summary>Speak as the Mac, with the deployment's builder key or another.</summary>
    public static HttpClient Client(string? key = Stack.BuilderKey)
    {
        var http = new HttpClient { BaseAddress = Stack.Origin };
        if (key is not null) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return http;
    }

    public static ByteArrayContent Bytes(byte[] bytes) => new(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } };

    /// <summary>The whole of a Mac's work on one build: take it, read the source, hand back a log and an app, and say it worked.</summary>
    public static async Task<byte[]> BuildAsync(string buildId, byte[] app, string log = "Built.", string bundleId = "com.acme.app")
    {
        using var mac = Client();
        (await mac.PostAsync($"/api/builder/builds/{buildId}/take", null)).EnsureSuccessStatusCode();
        var source = await mac.GetByteArrayAsync($"/api/builder/builds/{buildId}/source");
        (await mac.PutAsync($"/api/builder/builds/{buildId}/log", Bytes(System.Text.Encoding.UTF8.GetBytes(log)))).EnsureSuccessStatusCode();
        (await mac.PutAsync($"/api/builder/builds/{buildId}/app", Bytes(app))).EnsureSuccessStatusCode();
        (await mac.PostAsJsonAsync($"/api/builder/builds/{buildId}/end", new BuildEnd(true, bundleId, null), Person.Json)).EnsureSuccessStatusCode();
        return source;
    }
}
