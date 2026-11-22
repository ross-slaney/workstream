using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Workstream.Api.Domain;

namespace Workstream.Api.Model;

/// <summary>
/// A model call the API makes itself, for a team: the same Foundry deployment, key and allowance as the
/// gateway (<see cref="ModelGateway"/>), and the same <see cref="ModelUsage"/> row, which has no task. Used
/// for an environment's suggestion (Operations/GitHub.cs).
/// </summary>
public sealed class TeamModel(ModelOptions options, IHttpClientFactory clients, IMemoryCache cache, WorkstreamDbContext db, TimeProvider clock, ILogger<TeamModel> log)
{
    static readonly TimeSpan AllowanceFor = TimeSpan.FromSeconds(15);
    static readonly TimeSpan Longest = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Send one Messages request for the team, and answer the model's whole JSON answer. Refused when no model
    /// is set up, or the team has used its allowance for the month.
    /// </summary>
    public async Task<Outcome<string>> MessagesAsync(string teamId, JsonObject request, CancellationToken ct)
    {
        if (!options.IsConfigured) return Outcome<string>.Unavailable("No AI model is set up on this Workstream yet.");
        var allowance = await cache.GetOrCreateAsync(ModelGateway.AllowanceKey(teamId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = AllowanceFor;
            var team = await db.Teams.AsNoTracking().SingleAsync(x => x.Id == teamId, ct);
            return team.ModelAllowanceUsd - await Operations.ModelSpentAsync(db, teamId, Operations.MonthStart(clock.GetUtcNow()), ct);
        });
        if (allowance <= 0) return Outcome<string>.LimitReached("Your team has used its model allowance for this month. A system administrator can raise it.");

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{options.Endpoint.TrimEnd('/')}/v1/messages")
        {
            Content = new StringContent(request.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };
        message.Headers.TryAddWithoutValidation("x-api-key", options.Key);
        message.Headers.TryAddWithoutValidation("api-key", options.Key);
        message.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        string answer;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Longest);
            using var response = await clients.CreateClient(ModelGateway.ClientName).SendAsync(message, timeout.Token);
            answer = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("The model answered {Status} to a call for team {TeamId}.", (int)response.StatusCode, teamId);
                return Outcome<string>.Unavailable("The model did not answer. Try again.");
            }
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning(error, "The model did not answer a call for team {TeamId}.", teamId);
            return Outcome<string>.Unavailable("The model did not answer. Try again.");
        }

        Record(teamId, request["model"]?.GetValue<string>() ?? options.Sonnet, answer);
        await db.SaveChangesAsync(CancellationToken.None);
        return Outcome<string>.Ok(answer);
    }

    void Record(string teamId, string asked, string answer)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(answer);
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }
        var usage = root?["usage"];
        long Count(string name) => usage?[name] is JsonValue value && value.TryGetValue<long>(out var count) ? count : 0;
        var (input, output, cacheRead, cacheWrite) = (Count("input_tokens"), Count("output_tokens"), Count("cache_read_input_tokens"), Count("cache_creation_input_tokens"));
        if (input + output + cacheRead + cacheWrite == 0) return;
        var model = root?["model"] is JsonValue named && named.TryGetValue<string>(out var said) ? said : asked;
        var cost = options.CostOf(model, input, output, cacheRead, cacheWrite);
        db.ModelUsage.Add(ModelUsage.Record(teamId, "", model, input, output, cacheRead, cacheWrite, cost, clock.GetUtcNow()));
        if (cache.TryGetValue(ModelGateway.AllowanceKey(teamId), out decimal left)) cache.Set(ModelGateway.AllowanceKey(teamId), left - cost, AllowanceFor);
    }
}
