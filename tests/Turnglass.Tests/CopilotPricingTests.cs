using System.Globalization;
using System.Text.Json;
using Turnglass.Core;

namespace Turnglass.Tests;

public sealed class CopilotPricingTests
{
    [Fact]
    public void MissingBreakdownsUseVisibleAssumptionsWithoutChangingObservedFacts()
    {
        var fact = Fact();
        var result = CopilotPricing.Calculate([fact], Catalog());
        Assert.Equal(1100, result.Totals.Tokens.TotalTokens);
        Assert.Equal(0.0045m, result.Totals.KnownCostUsd);
        Assert.True(result.Totals.IsPricingComplete);
        Assert.Equal(2, result.Assumptions.Count);
        Assert.Null(fact.CachedInputTokens);
        Assert.Null(fact.CacheWriteInputTokens);
        var measured = fact with { ServiceTier = "standard", CachedInputTokens = 200, CacheWriteInputTokens = 0, ReasoningOutputTokens = 50 };
        result = CopilotPricing.Calculate([measured], Catalog());
        Assert.Empty(result.Assumptions);
        Assert.Equal(0.00396m, result.Totals.KnownCostUsd);
        Assert.Equal(1100, result.Totals.Tokens.TotalTokens);
    }

    [Theory]
    [InlineData("5m", "0.004035")]
    [InlineData("1h", "0.00426")]
    public void ClaudeWritesUseExactRetentionAndNeverChargeReasoningTwice(string lifetime, string cost)
    {
        var fact = Fact() with { ServiceTier = "standard", CachedInputTokens = 200, CacheWriteInputTokens = 100, ReasoningOutputTokens = 50, CacheWriteLifetime = lifetime };
        var result = CopilotPricing.Calculate([fact], Catalog());
        Assert.Equal(decimal.Parse(cost, CultureInfo.InvariantCulture), result.Totals.KnownCostUsd);
        Assert.True(result.Totals.IsPricingComplete);
        Assert.Empty(result.Assumptions);
    }

    [Fact]
    public void KnownWritesWithUnknownRetentionStayUnpricedUntilExplicitOverride()
    {
        var fact = Fact() with { CachedInputTokens = 200, CacheWriteInputTokens = 100 };
        var catalog = Catalog();
        var unknown = CopilotPricing.Calculate([fact], catalog);
        Assert.Equal(100, unknown.Totals.UnpricedTokens);
        Assert.False(unknown.Totals.IsPricingComplete);
        Assert.Equal(0.00366m, unknown.Totals.KnownCostUsd);
        catalog = catalog with { CacheWriteLifetimeOverrides = new Dictionary<string, string> { [fact.Model!] = "5m" } };
        var priced = CopilotPricing.Calculate([fact], catalog);
        Assert.True(priced.Totals.IsPricingComplete);
        Assert.Contains(priced.Assumptions, assumption => assumption.Contains("retention", StringComparison.Ordinal));
        Assert.Equal(100, fact.CacheWriteInputTokens);
    }

    [Fact]
    public void OverridesAliasesMissingRatesAndRepricingUseSharedCatalog()
    {
        var fact = Fact() with { Model = "custom", CachedInputTokens = 0, CacheWriteInputTokens = 0 };
        Assert.False(CopilotPricing.Calculate([fact], Catalog()).Totals.IsPricingComplete);
        var catalog = Catalog() with
        {
            ModelAliases = new Dictionary<string, string> { ["custom"] = "claude-sonnet-4.6" },
            TierOverrides = new Dictionary<string, string> { ["custom"] = "standard" },
        };
        var result = CopilotPricing.Calculate([fact], catalog);
        Assert.True(result.Totals.IsPricingComplete);
        var item = Assert.Single(result.Assumptions);
        Assert.Contains("override", item, StringComparison.Ordinal);
        var changed = catalog with { Rates = [new("claude-sonnet-4.6", "standard", 0, null, 6m, 0.6m, 30m)] };
        Assert.Equal(result.Totals.KnownCostUsd * 2, CopilotPricing.Calculate([fact], changed).Totals.KnownCostUsd);
        Assert.Empty(CopilotPricing.Calculate([], catalog).Assumptions);
    }

    [Fact]
    public void StarterRatesPreserveEntireCustomModelTierAndOldJsonLoads()
    {
        var custom = new PriceRate("gpt-5.4", "standard", 0, 10, 999m, 1m, 5m);
        var customLuna = new PriceRate("gpt-6-luna", "standard", 0, 10, 100m, 2m, 20m);
        var catalog = PricingDefaults.CreateCatalog() with { Rates = [custom, customLuna] };
        var merged = CopilotPricingDefaults.Merge(catalog);
        Assert.Same(custom, Assert.Single(merged.Rates, rate => rate.Model == "gpt-5.4"));
        Assert.Same(customLuna, Assert.Single(merged.Rates, rate => rate.Model == "gpt-6-luna"));
        Assert.Same(merged, CopilotPricingDefaults.Merge(merged));
        var old = JsonSerializer.Deserialize<PriceRate>("""{"model":"test","tier":"standard","minInputTokens":0,"inputPerMillion":1,"cachedInputPerMillion":0,"outputPerMillion":2} """, JsonSerializerOptions.Web)!;
        Assert.Null(old.CacheWrite5mPerMillion);
        Assert.Null(old.CacheWrite1hPerMillion);
        var settings = new ViewerSettings([], "UTC", merged with { CacheWriteLifetimeOverrides = new Dictionary<string, string> { ["claude-sonnet-4.6"] = "5m" } });
        SettingsValidator.Validate(settings);
        Assert.Throws<ArgumentException>(() => SettingsValidator.Validate(settings with { Pricing = merged with { Rates = [old with { CacheWrite1hPerMillion = -1m }] } }));
    }

    [Fact]
    public void VerifiedResponseModelAliasesArePricedWithoutOverwritingUserMappings()
    {
        var fact = Fact() with { Model = "claude-haiku-4-5-20251001", CachedInputTokens = 200, CacheWriteInputTokens = 100, CacheWriteLifetime = "5m" };
        var result = CopilotPricing.Calculate([fact], Catalog());
        Assert.True(result.Totals.IsPricingComplete);
        Assert.Equal(0.001345m, result.Totals.KnownCostUsd);
        var custom = PricingDefaults.CreateCatalog() with { ModelAliases = new Dictionary<string, string> { [fact.Model] = "custom-price" } };
        Assert.Equal("custom-price", CopilotPricingDefaults.Merge(custom).ModelAliases[fact.Model]);
    }

    [Fact]
    public void LunaVisualStudioCallPricesObservedThirtyMinuteCacheWrites()
    {
        var fact = Fact() with
        {
            Model = "gpt-6-luna",
            Client = "visual-studio",
            InputTokens = 12_916,
            OutputTokens = 13,
            CachedInputTokens = 0,
            CacheWriteInputTokens = 12_913,
            CacheWriteLifetime = "30m",
        };
        var result = CopilotPricing.Calculate([fact], Catalog());
        Assert.True(result.Totals.IsPricingComplete);
        Assert.Equal(0.001620925m, result.Totals.KnownCostUsd);
        Assert.Equal(12_929, result.Totals.Tokens.TotalTokens);
        Assert.Equal(0, result.Totals.UnpricedEvents);
    }

    [Theory]
    [InlineData(272_000, "30m", "0.0272345")]
    [InlineData(272_001, "30m", "0.0544442")]
    [InlineData(272_000, "12h", "0.027247")]
    [InlineData(272_001, "12h", "0.0544692")]
    public void LunaUsesVerifiedContextBandsAndCacheRetentionRates(long input, string lifetime, string cost)
    {
        var fact = Fact() with { Model = "gpt-6-luna", InputTokens = input, CachedInputTokens = 200, CacheWriteInputTokens = 100, CacheWriteLifetime = lifetime };
        var result = CopilotPricing.Calculate([fact], Catalog());
        Assert.True(result.Totals.IsPricingComplete);
        Assert.Equal(decimal.Parse(cost, CultureInfo.InvariantCulture), result.Totals.KnownCostUsd);
    }

    private static CopilotUsageFact Fact() => new("call", "session", DateTimeOffset.Parse("2026-11-25T12:00:00Z", CultureInfo.InvariantCulture), "claude-sonnet-4.6", "shared", 1000, 100);

    private static PricingCatalog Catalog() => CopilotPricingDefaults.Merge(PricingDefaults.CreateCatalog());
}
