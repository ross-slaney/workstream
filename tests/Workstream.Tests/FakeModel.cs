using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Workstream.Tests;

/// <summary>
/// Foundry, played under the gateway's own HTTP client: it remembers each call that reached it and answers
/// what the test set, a whole JSON message by default.
/// </summary>
public sealed class FakeModel : HttpMessageHandler
{
    public sealed record Call(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string Body);

    public ConcurrentQueue<Call> Calls { get; } = new();

    /// <summary>What the next calls are answered with.</summary>
    public Func<HttpResponseMessage> Answer { get; set; } = () => Json(Message("claude-opus-5-5", input: 1000, output: 500));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
        Calls.Enqueue(new Call(request.Method, request.RequestUri!, headers, body));
        return Answer();
    }

    public static string Message(string model, long input, long output, long cacheRead = 0, long cacheWrite = 0) => JsonSerializer.Serialize(new
    {
        id = "msg_1", type = "message", role = "assistant", model,
        content = new[] { new { type = "text", text = "Done." } },
        stop_reason = "end_turn",
        usage = new { input_tokens = input, output_tokens = output, cache_read_input_tokens = cacheRead, cache_creation_input_tokens = cacheWrite },
    });

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>The same answer as a stream of events, as the agent asks for it.</summary>
    public static HttpResponseMessage Events(string model, long input, long output)
    {
        var events = new StringBuilder();
        void Event(string name, object data) => events.Append($"event: {name}\ndata: {JsonSerializer.Serialize(data)}\n\n");
        Event("message_start", new
        {
            type = "message_start",
            message = new { id = "msg_1", type = "message", role = "assistant", model, content = Array.Empty<object>(), usage = new { input_tokens = input, output_tokens = 1 } },
        });
        Event("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = "Done." } });
        Event("message_delta", new { type = "message_delta", delta = new { stop_reason = "end_turn" }, usage = new { output_tokens = output } });
        Event("message_stop", new { type = "message_stop" });
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(events.ToString(), Encoding.UTF8, "text/event-stream") };
    }
}
