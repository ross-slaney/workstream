using System.Net.Http.Headers;
using System.Net.Http.Json;
using Workstream.Api;

namespace Workstream.Tests;

/// <summary>
/// A task's desktop, as its agent speaks to the API: with the key the machine was made with and nothing else
/// (desktop/agent/agent.mjs). Made by running the job that starts the task's desktop against the played host.
/// </summary>
public sealed class Desktop : IDisposable
{
    readonly HttpClient http = new() { BaseAddress = Stack.Origin };
    // Carries no credential of its own: a model call names its key in the header the test chooses.
    readonly HttpClient bare = new() { BaseAddress = Stack.Origin };

    public string Key { get; }
    public string Host { get; }

    Desktop(string key, string host)
    {
        Key = key;
        Host = host;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    /// <summary>Run the start job for a task, then speak as the desktop it made.</summary>
    public static async Task<Desktop> StartAsync(string taskId)
    {
        await Stack.Jobs.StartAsync(taskId, CancellationToken.None);
        return Of(taskId);
    }

    /// <summary>Speak as the desktop a start job already made for the task.</summary>
    public static Desktop Of(string taskId)
    {
        var spec = Stack.Desktops.For(taskId);
        return new Desktop(spec.Key, spec.Name);
    }

    /// <summary>Speak as the machine preparing an environment now, with the key it was made with.</summary>
    public static Desktop Preparing(string environmentId)
    {
        var spec = Stack.Desktops.Preparing(environmentId) ?? throw new AssertFailedException("No machine is preparing the environment.");
        return new Desktop(spec.Key, spec.Name);
    }

    public Task<DesktopSetup> SetupAsync() => ReadAsync<DesktopSetup>(http.GetAsync("/api/desktop/setup"));

    /// <summary>The preparing desktop says how the setup is going.</summary>
    public Task<HttpResponseMessage> ReportAsync(string state, int? exit = null, string? output = null, string? commit = null, long? bytes = null) =>
        http.PostAsJsonAsync("/api/desktop/setup", new SetupReport(state, exit, output, commit, bytes), Person.Json);

    /// <summary>One turn of the agent's loop: what it wrote since last time, and what people wrote to it.</summary>
    public Task<DesktopReply> ExchangeAsync(bool working = false, long after = 0, params DesktopMessage[] messages) =>
        ReadAsync<DesktopReply>(http.PostAsJsonAsync("/api/desktop/exchange", new DesktopExchange(working, after, messages), Person.Json));

    /// <summary>The same, in the middle of a command that has run for some seconds.</summary>
    public Task<DesktopReply> ExchangeRunningAsync(string? text, int seconds, bool working = true) =>
        ReadAsync<DesktopReply>(http.PostAsJsonAsync("/api/desktop/exchange", new DesktopExchange(working, 0, null, null, text is null ? null : new DesktopRunning(text, seconds)), Person.Json));

    public Task<HttpResponseMessage> GetAsync(string path) => http.GetAsync(path);

    public Task<HttpResponseMessage> PostAsync(string path, object body) => http.PostAsJsonAsync(path, body, Person.Json);

    /// <summary>A file sent as it is: a build's source.</summary>
    public Task<HttpResponseMessage> PutAsync(string path, HttpContent content) => http.PutAsync(path, content);

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) => http.SendAsync(request);

    /// <summary>A model call, as the agent's harness makes it: Anthropic's Messages API at the gateway's address.</summary>
    /// <param name="chunked">Send the body without saying its length, as some clients do.</param>
    public Task<HttpResponseMessage> AskModelAsync(bool stream = false, string key = "x-api-key", bool chunked = false)
    {
        var body = new { model = "claude-opus-5-5", max_tokens = 64, stream, messages = new[] { new { role = "user", content = "Say done." } } };
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/model/anthropic/v1/messages")
        {
            Content = chunked ? JsonContent.Create(body) : new StringContent(System.Text.Json.JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"),
        };
        if (chunked) request.Headers.TransferEncodingChunked = true;
        // The harness sends its key as Anthropic's clients do; the gateway reads either header.
        request.Headers.TryAddWithoutValidation(key, Key);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        return bare.SendAsync(request);
    }

    static async Task<T> ReadAsync<T>(Task<HttpResponseMessage> answer) => await Person.ReadAsync<T>(await answer);

    public static DesktopMessage Agent(string text, string kind = "text") => new(Guid.NewGuid().ToString("D"), "agent", kind, text);

    public void Dispose()
    {
        http.Dispose();
        bare.Dispose();
    }
}
