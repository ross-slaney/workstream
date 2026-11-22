using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SqlOS.Extensions;
using SqlOS.Fga.Interfaces;
using Workstream.Api.Domain;
using static Workstream.Api.WorkstreamAuthorization;

namespace Workstream.Api.Model;

/// <summary>
/// Where every agent's model calls go (<c>Workstream:Model</c>): Workstream's own Foundry resource, reached with a
/// key that never leaves this process.
/// </summary>
public sealed class ModelOptions
{
    /// <summary>The Foundry resource's Anthropic address, such as https://name.services.ai.azure.com/anthropic.</summary>
    public string Endpoint { get; set; } = "";
    public string Key { get; set; } = "";
    /// <summary>The Foundry deployments the agent uses. Each is named after its model.</summary>
    public string Main { get; set; } = "claude-opus-5-5";
    public string Sonnet { get; set; } = "claude-sonnet-5-5";
    public string Small { get; set; } = "claude-haiku-5-5";
    /// <summary>What a team is charged over what the model costs: 1 is cost.</summary>
    public decimal Markup { get; set; } = 1m;

    public bool IsConfigured => Endpoint.Length > 0 && Key.Length > 0;

    /// <summary>
    /// What each model costs, in US dollars per million tokens: input, output, and reading from the prompt
    /// cache. Writing to the cache costs 1.25 times input when kept five minutes and twice input when kept an
    /// hour. Haiku has a second, higher rate for a prompt over 100,000 tokens. Foundry bills Claude at
    /// Anthropic's own rates. Change these when the deployments change.
    /// </summary>
    static readonly Dictionary<string, Price> Prices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-opus-5-5"] = new(4m, 20m, 0.20m),
        ["claude-sonnet-5-5"] = new(2m, 10m, 0.10m),
        ["claude-haiku-5-5"] = new(0.10m, 0.50m, 0.01m) { LongPrompt = new(0.50m, 2.50m, 0.05m) },
    };

    const long LongPromptTokens = 100_000;

    sealed record Price(decimal Input, decimal Output, decimal CacheRead)
    {
        /// <summary>The rate for a prompt longer than <see cref="LongPromptTokens"/>, where the model has one.</summary>
        public Price? LongPrompt { get; init; }
    }

    /// <summary>
    /// A call's cost. <paramref name="cacheWrite"/> is every token written to the cache, of which
    /// <paramref name="cacheWriteHour"/> were written to be kept an hour. A model this table does not know is
    /// priced as the most expensive one it does.
    /// </summary>
    public decimal CostOf(string model, long input, long output, long cacheRead, long cacheWrite, long cacheWriteHour = 0)
    {
        var price = Prices.FirstOrDefault(x => model.StartsWith(x.Key, StringComparison.OrdinalIgnoreCase)).Value ?? Prices["claude-opus-5-5"];
        // The prompt is everything sent: what was read new, what came from the cache and what was written to it.
        if (price.LongPrompt is { } longer && input + cacheRead + cacheWrite > LongPromptTokens) price = longer;
        var hour = Math.Clamp(cacheWriteHour, 0, cacheWrite);
        var cost = (input * price.Input + output * price.Output + cacheRead * price.CacheRead
            + (cacheWrite - hour) * price.Input * 1.25m + hour * price.Input * 2m) / 1_000_000m;
        return decimal.Round(cost * Markup, 6);
    }
}

/// <summary>
/// The model gateway: <c>/api/model/anthropic/…</c> is Anthropic's Messages API as Foundry serves it, for one
/// kind of caller, a task's desktop with its key. Each call is checked (<c>TASK_RUN_AGENT</c> on the task, the
/// team's allowance for the month), forwarded to Foundry with the real key in place of the desktop's, streamed
/// back as it arrives, and its token counts recorded as <see cref="ModelUsage"/>. The agent on the desktop
/// never holds a credential that works anywhere but here.
/// </summary>
public static class ModelGateway
{
    public const string Path = "/api/model/anthropic";
    public const string ClientName = "model";
    const long MaxRequestBytes = 64 * 1024 * 1024;
    static readonly TimeSpan AllowanceFor = TimeSpan.FromSeconds(15);

    /// <summary>Where what a team has left this month is remembered between calls, for a few seconds.</summary>
    public static string AllowanceKey(string teamId) => $"allowance:{teamId}";

    /// <summary>Headers that belong to one hop, or that this gateway sets itself.</summary>
    static readonly HashSet<string> NotForwarded = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Authorization", "x-api-key", "api-key", "Content-Length", "Content-Type", "Connection", "Transfer-Encoding", "Accept-Encoding", "Cookie",
        "Keep-Alive", "Upgrade", "Proxy-Authorization", "Proxy-Connection", "TE", "Trailer", "Origin", "Referer",
    };
    static readonly HashSet<string> NotReturned = new(StringComparer.OrdinalIgnoreCase)
    {
        "Transfer-Encoding", "Content-Length", "Connection", "Keep-Alive", "Content-Encoding", "Set-Cookie", "apim-request-id", "x-ms-region",
    };

    public static WebApplicationBuilder AddModelGateway(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton(builder.Configuration.GetSection("Workstream:Model").Get<ModelOptions>() ?? new ModelOptions());
        builder.Services.AddMemoryCache();
        // An agent's turn can stream for many minutes: no overall timeout, only the caller going away ends it.
        builder.Services.AddHttpClient(ClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            });
        return builder;
    }

    public static async Task HandleAsync(HttpContext http, string? path, ModelOptions options, WorkstreamDbContext db, ISqlOSFgaAuthService fga,
        IHttpClientFactory clients, IMemoryCache cache, IServiceScopeFactory scopes, TimeProvider clock, ILoggerFactory logs)
    {
        var ct = http.RequestAborted;
        var desktop = Caller.Desktop(http);
        // The task this desktop works on, which is a grant: a spare, or the desktop of a deleted task, has none.
        var task = await Operations.DesktopTaskAsync(desktop, db, ct, tracked: false);
        if (task is null)
        {
            await RefuseAsync(http, StatusCodes.Status401Unauthorized, "authentication_error", "This desktop's key is no longer valid.");
            return;
        }
        if (!options.IsConfigured)
        {
            // A plain refusal: the agent's client waits and tries again on a 5xx, and reads a 401 or 403 as its own
            // credential having failed. This will not change by waiting, and the words are for the person.
            // The words reach the person in the conversation; which settings are missing is for whoever runs this (README, "Models").
            logs.CreateLogger(typeof(ModelGateway)).LogWarning("A model call was refused: Workstream:Model:Endpoint and Workstream:Model:Key are not set.");
            await RefuseAsync(http, StatusCodes.Status400BadRequest, "invalid_request_error", "No AI model is set up on this Workstream yet, so I can't do the work. Whoever runs it needs to add one.");
            return;
        }
        var allowance = await cache.GetOrCreateAsync(AllowanceKey(task.TeamId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = AllowanceFor;
            var team = await db.Teams.AsNoTracking().SingleAsync(x => x.Id == task.TeamId, ct);
            return team.ModelAllowanceUsd - await Operations.ModelSpentAsync(db, task.TeamId, Operations.MonthStart(clock.GetUtcNow()), ct);
        });
        if (allowance <= 0)
        {
            await RefuseAsync(http, StatusCodes.Status400BadRequest, "invalid_request_error", "Your team has used its model allowance for this month. A system administrator can raise it.");
            return;
        }
        if (http.Request.ContentLength > MaxRequestBytes)
        {
            await RefuseAsync(http, StatusCodes.Status413PayloadTooLarge, "request_too_large", "The request is too large.");
            return;
        }

        using var forward = new HttpRequestMessage(new HttpMethod(http.Request.Method), $"{options.Endpoint.TrimEnd('/')}/{path}{http.Request.QueryString}");
        foreach (var header in http.Request.Headers.Where(h => !NotForwarded.Contains(h.Key)))
        {
            forward.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
        forward.Headers.TryAddWithoutValidation("x-api-key", options.Key);
        forward.Headers.TryAddWithoutValidation("api-key", options.Key);
        if (HttpMethods.IsPost(http.Request.Method) || HttpMethods.IsPut(http.Request.Method))
        {
            // Foundry refuses a request that does not say how long it is, so the body goes on with its length:
            // the caller's own when it gave one, else counted here after reading it, up to the same limit.
            if (http.Request.ContentLength is { } length)
            {
                forward.Content = new StreamContent(http.Request.Body);
                forward.Content.Headers.ContentLength = length;
            }
            else
            {
                using var read = new MemoryStream();
                var buffer = new byte[81920];
                int count;
                while ((count = await http.Request.Body.ReadAsync(buffer, ct)) > 0)
                {
                    if (read.Length + count > MaxRequestBytes)
                    {
                        await RefuseAsync(http, StatusCodes.Status413PayloadTooLarge, "request_too_large", "The request is too large.");
                        return;
                    }
                    read.Write(buffer, 0, count);
                }
                forward.Content = new ByteArrayContent(read.ToArray());
            }
            if (http.Request.ContentType is { } contentType) forward.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        var usage = new UsageReader();
        try
        {
            using var response = await clients.CreateClient(ClientName).SendAsync(forward, HttpCompletionOption.ResponseHeadersRead, ct);
            http.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers.Concat(response.Content.Headers).Where(h => !NotReturned.Contains(h.Key)))
            {
                http.Response.Headers[header.Key] = header.Value.ToArray();
            }
            var counted = response.IsSuccessStatusCode && (path ?? "").TrimEnd('/').EndsWith("messages", StringComparison.OrdinalIgnoreCase);
            var events = response.Content.Headers.ContentType?.MediaType == "text/event-stream";
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await body.ReadAsync(buffer, ct)) > 0)
            {
                if (counted) usage.Take(buffer.AsSpan(0, read), events);
                await http.Response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
                await http.Response.Body.FlushAsync(ct);
            }
            if (counted && !events) usage.Finish();
        }
        catch (HttpRequestException error)
        {
            logs.CreateLogger(typeof(ModelGateway)).LogError(error, "The model did not answer.");
            if (!http.Response.HasStarted) await RefuseAsync(http, StatusCodes.Status502BadGateway, "api_error", "The model did not answer. Try again.");
        }
        catch (OperationCanceledException)
        {
            // The agent went away mid-answer. What was used before then is still recorded below.
        }

        if (usage.Any)
        {
            // Recorded apart from the request, which may already be gone.
            await using var scope = scopes.CreateAsyncScope();
            var record = scope.ServiceProvider.GetRequiredService<WorkstreamDbContext>();
            var model = usage.Model ?? options.Main;
            var cost = options.CostOf(model, usage.Input, usage.Output, usage.CacheRead, usage.CacheWrite, usage.CacheWriteHour);
            record.ModelUsage.Add(ModelUsage.Record(task.TeamId, task.Id, model, usage.Input, usage.Output, usage.CacheRead, usage.CacheWrite, cost, clock.GetUtcNow()));
            await record.SaveChangesAsync(CancellationToken.None);
            if (cache.TryGetValue(AllowanceKey(task.TeamId), out decimal left)) cache.Set(AllowanceKey(task.TeamId), left - cost, AllowanceFor);
        }
    }

    /// <summary>A refusal in the shape Anthropic's clients read, so the agent shows the words.</summary>
    static Task RefuseAsync(HttpContext http, int status, string type, string message)
    {
        http.Response.StatusCode = status;
        return http.Response.WriteAsJsonAsync(new { type = "error", error = new { type, message } });
    }

    /// <summary>
    /// Reads token counts out of an answer as it passes: from a stream's <c>message_start</c> and
    /// <c>message_delta</c> events, or from a whole JSON answer's <c>usage</c>.
    /// </summary>
    sealed class UsageReader
    {
        readonly MemoryStream pending = new();
        public string? Model { get; private set; }
        public long Input { get; private set; }
        public long Output { get; private set; }
        public long CacheRead { get; private set; }
        public long CacheWrite { get; private set; }
        /// <summary>Of <see cref="CacheWrite"/>, the tokens written to be kept an hour, which cost more.</summary>
        public long CacheWriteHour { get; private set; }
        public bool Any => Input + Output + CacheRead + CacheWrite > 0;

        public void Take(ReadOnlySpan<byte> bytes, bool events)
        {
            // A whole JSON answer is read at the end; past a few megabytes it is no longer worth keeping.
            if (!events)
            {
                if (pending.Length < 8 * 1024 * 1024) pending.Write(bytes);
                return;
            }
            pending.Write(bytes);
            var data = pending.GetBuffer().AsSpan(0, (int)pending.Length);
            var consumed = 0;
            int end;
            while ((end = data[consumed..].IndexOf((byte)'\n')) >= 0)
            {
                var line = data.Slice(consumed, end).TrimEnd((byte)'\r');
                consumed += end + 1;
                if (line.StartsWith("data:"u8)) Read(line[5..]);
            }
            var rest = data[consumed..].ToArray();
            pending.SetLength(0);
            pending.Write(rest);
        }

        public void Finish() => Read(pending.GetBuffer().AsSpan(0, (int)pending.Length));

        void Read(ReadOnlySpan<byte> json)
        {
            try
            {
                var reader = new Utf8JsonReader(json);
                using var document = JsonDocument.ParseValue(ref reader);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;
                var message = root.TryGetProperty("message", out var started) ? started : root;
                if (message.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String) Model = model.GetString();
                if (message.TryGetProperty("usage", out var usage) || root.TryGetProperty("usage", out usage)) Count(usage);
            }
            catch (JsonException)
            {
                // Not JSON, or half an event: nothing to count.
            }
        }

        // Each field is the running total for the answer, so the latest value replaces the one before.
        void Count(JsonElement usage)
        {
            if (usage.ValueKind != JsonValueKind.Object) return;
            if (Number(usage, "input_tokens") is { } input) Input = input;
            if (Number(usage, "output_tokens") is { } output) Output = output;
            if (Number(usage, "cache_read_input_tokens") is { } cacheRead) CacheRead = cacheRead;
            if (Number(usage, "cache_creation_input_tokens") is { } cacheWrite) CacheWrite = cacheWrite;
            if (usage.TryGetProperty("cache_creation", out var written) && written.ValueKind == JsonValueKind.Object
                && Number(written, "ephemeral_1h_input_tokens") is { } hour) CacheWriteHour = hour;
        }

        static long? Number(JsonElement parent, string name) =>
            parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;
    }
}
