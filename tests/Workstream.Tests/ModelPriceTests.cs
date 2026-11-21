using Workstream.Api.Model;

namespace Workstream.Tests;

/// <summary>What a model call costs: the arithmetic a team's allowance, and one day its bill, is made from.</summary>
[TestClass]
public sealed class ModelPriceTests
{
    static readonly ModelOptions Prices = new();

    [TestMethod]
    public void A_call_costs_its_tokens_at_the_models_rates()
    {
        // Opus: $4 in, $20 out, $0.20 read from the cache, a million tokens each.
        Assert.AreEqual(4m, Prices.CostOf("claude-opus-5-5", 1_000_000, 0, 0, 0));
        Assert.AreEqual(20m, Prices.CostOf("claude-opus-5-5", 0, 1_000_000, 0, 0));
        Assert.AreEqual(0.20m, Prices.CostOf("claude-opus-5-5", 0, 0, 1_000_000, 0));
        Assert.AreEqual(2m + 10m + 0.10m, Prices.CostOf("claude-sonnet-5-5", 1_000_000, 1_000_000, 1_000_000, 0));
        // A deployment's answer may name a dated version of the model.
        Assert.AreEqual(4m, Prices.CostOf("claude-opus-5-5-20261001", 1_000_000, 0, 0, 0));
    }

    [TestMethod]
    public void Writing_to_the_cache_costs_more_than_reading_new_and_most_when_kept_an_hour()
    {
        Assert.AreEqual(5m, Prices.CostOf("claude-opus-5-5", 0, 0, 0, cacheWrite: 1_000_000));
        Assert.AreEqual(8m, Prices.CostOf("claude-opus-5-5", 0, 0, 0, cacheWrite: 1_000_000, cacheWriteHour: 1_000_000));
        Assert.AreEqual(2.5m + 4m, Prices.CostOf("claude-opus-5-5", 0, 0, 0, cacheWrite: 1_000_000, cacheWriteHour: 500_000));
    }

    [TestMethod]
    public void Haiku_costs_more_for_a_long_prompt()
    {
        Assert.AreEqual(0.01m, Prices.CostOf("claude-haiku-5-5", 100_000, 0, 0, 0));
        // Over 100,000 tokens sent, counting what came from the cache: five times the rate.
        Assert.AreEqual(0.0505m, Prices.CostOf("claude-haiku-5-5", 101_000, 0, 0, 0));
        Assert.AreEqual(decimal.Round((1_000 * 0.50m + 100_000 * 0.05m) / 1_000_000m, 6), Prices.CostOf("claude-haiku-5-5", 1_000, 0, 100_000, 0));
    }

    [TestMethod]
    public void A_model_nobody_listed_is_priced_as_the_most_expensive()
    {
        Assert.AreEqual(Prices.CostOf("claude-opus-5-5", 1000, 1000, 0, 0), Prices.CostOf("some-new-model", 1000, 1000, 0, 0));
    }
}
