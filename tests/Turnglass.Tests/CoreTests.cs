using System.Globalization;
using Turnglass.Core;

namespace Turnglass.Tests;

public sealed class CoreTests
{
    [Fact]
    public void PricingSubtractsCachedInputAndDoesNotChargeReasoningTwice()
    {
        var result = PricingCalculator.Calculate([Fact(new TokenUsage(1000, 200, 0, 100, 50))], PricingDefaults.CreateCatalog());
        Assert.Equal(1100, result.Tokens.TotalTokens);
        Assert.Equal(0.0132m, result.KnownCostUsd);
        Assert.True(result.IsPricingComplete);
    }

    [Theory]
    [InlineData(272000, "default", "2.725")]
    [InlineData(272001, "default", "5.44752")]
    [InlineData(272001, "priority", "10.89504")]
    [InlineData(272001, "fast", "10.89504")]
    public void ContextBandAppliesToEntireRequest(long input, string tier, string expected)
    {
        var result = PricingCalculator.Calculate([Fact(new TokenUsage(input, 0, 0, 100), tier: tier)], PricingDefaults.CreateCatalog());
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), result.KnownCostUsd);
    }

    [Theory]
    [InlineData("30m", "0.01345")]
    [InlineData("12h", "0.0147")]
    public void WritesAreInputSubsetsWithRetentionSpecificPrices(string lifetime, string expected)
    {
        var fact = Fact(new TokenUsage(1000, 200, 100, 100)) with { CacheWriteLifetime = lifetime };
        var result = PricingCalculator.Calculate([fact], PricingDefaults.CreateCatalog());
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), result.KnownCostUsd);
        Assert.True(result.IsPricingComplete);
    }

    [Fact]
    public void UnknownWriteLifetimeKeepsKnownSubtotalAndReportsOnlyUnpricedWrites()
    {
        var result = PricingCalculator.Calculate([Fact(new TokenUsage(1000, 200, 100, 100))], PricingDefaults.CreateCatalog());
        Assert.Equal(0.0122m, result.KnownCostUsd);
        Assert.Equal(100, result.UnpricedTokens);
        Assert.Equal(1, result.UnpricedEvents);
        Assert.False(result.IsPricingComplete);
        Assert.Contains("cache-write", Assert.Single(result.PricingWarnings), StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownModelsTiersAndContextAreExplicitGaps()
    {
        var facts = new[]
        {
            Fact(new TokenUsage(10), model: null),
            Fact(new TokenUsage(20), model: "codex-auto-review"),
            Fact(new TokenUsage(30), tier: null),
            Fact(new TokenUsage(40)) with { ContextKnown = false },
        };
        var result = PricingCalculator.Calculate(facts, PricingDefaults.CreateCatalog());
        Assert.Equal(100, result.Tokens.TotalTokens);
        Assert.Equal(100, result.UnpricedTokens);
        Assert.Equal(4, result.UnpricedEvents);
        Assert.Equal(4, result.PricingWarnings.Count);
        Assert.Equal(0m, result.KnownCostUsd);
    }

    [Fact]
    public void OverridesResolveAliasesTierAndWriteLifetimeWithoutChangingTokenFacts()
    {
        var catalog = PricingDefaults.CreateCatalog() with
        {
            ModelAliases = new Dictionary<string, string> { ["custom"] = "gpt-6-astra" },
            TierOverrides = new Dictionary<string, string> { ["custom"] = "priority" },
            CacheWriteLifetimeOverrides = new Dictionary<string, string> { ["custom"] = "12h" },
        };
        var result = PricingCalculator.Calculate([Fact(new TokenUsage(1000, 200, 100, 100), "custom", null)], catalog);
        Assert.Equal(0.0294m, result.KnownCostUsd);
        Assert.Equal(1100, result.Tokens.TotalTokens);
        Assert.True(result.IsPricingComplete);
    }

    [Fact]
    public void CatalogChangesRepriceHistoricalFactsWithoutRoundingEachEvent()
    {
        var facts = Enumerable.Range(0, 100).Select(_ => Fact(new TokenUsage(1))).ToArray();
        var catalog = PricingDefaults.CreateCatalog();
        Assert.Equal(0.001m, PricingCalculator.Calculate(facts, catalog).KnownCostUsd);
        catalog = catalog with { Rates = [new PriceRate("gpt-6-astra", "standard", 0, null, 20m, 0m, 0m)] };
        Assert.Equal(0.002m, PricingCalculator.Calculate(facts, catalog).KnownCostUsd);
    }

    [Fact]
    public void ZeroUsageIsCompleteAndInvalidCountersAreNotPriced()
    {
        Assert.True(PricingCalculator.Calculate([], PricingDefaults.CreateCatalog()).IsPricingComplete);
        var invalid = PricingCalculator.Calculate([Fact(new TokenUsage(10, 20))], PricingDefaults.CreateCatalog());
        Assert.False(invalid.IsPricingComplete);
        Assert.Contains("Invalid token counters", invalid.PricingWarnings);
        Assert.Equal(0m, invalid.KnownCostUsd);
    }

    [Fact]
    public void MissingWriteRateAndUnknownContextBandRemainIncomplete()
    {
        var catalog = PricingDefaults.CreateCatalog() with { Rates = [new PriceRate("gpt-6-astra", "standard", 0, 100, 10m, 1m, 50m)] };
        var write = Fact(new TokenUsage(100, 0, 20)) with { CacheWriteLifetime = "30m" };
        Assert.False(PricingCalculator.Calculate([write], catalog).IsPricingComplete);
        Assert.Equal(101, PricingCalculator.Calculate([Fact(new TokenUsage(101))], catalog).UnpricedTokens);
    }

    [Theory]
    [InlineData("default", "standard")]
    [InlineData(" PRIORITY ", "fast")]
    [InlineData("ultrafast", "ultrafast")]
    [InlineData("flex", "flex")]
    [InlineData("batch", "batch")]
    [InlineData("auto", null)]
    [InlineData(null, null)]
    public void OnlyDocumentedTierAliasesAreNormalized(string? input, string? expected)
    {
        Assert.Equal(expected, PricingCalculator.NormalizeTier(input));
    }

    [Fact]
    public void CalendarPeriodsUseLocalMidnightMondayAndMonthStart()
    {
        var now = Timestamp("2026-11-21T15:00:00Z");
        var periods = UsagePeriods.GetPeriods(now, "calendar", "America/New_York");
        Assert.Equal(Timestamp("2026-11-21T05:00:00Z"), periods[0].Start);
        Assert.Equal(Timestamp("2026-11-16T05:00:00Z"), periods[1].Start);
        Assert.Equal(Timestamp("2026-11-01T04:00:00Z"), periods[2].Start);
        Assert.All(periods, period => Assert.Equal(now, period.End));
    }

    [Fact]
    public void RollingPeriodsAreExactDurationsAcrossDst()
    {
        var now = Timestamp("2026-03-09T03:00:00Z");
        var rolling = UsagePeriods.GetPeriods(now, "rolling", "America/New_York");
        Assert.Equal(TimeSpan.FromHours(24), rolling[0].End - rolling[0].Start);
        Assert.Equal(TimeSpan.FromDays(7), rolling[1].End - rolling[1].Start);
        Assert.Equal(TimeSpan.FromDays(30), rolling[2].End - rolling[2].Start);
        var calendar = UsagePeriods.GetPeriods(now, "calendar", "America/New_York");
        Assert.Equal(TimeSpan.FromHours(22), calendar[0].End - calendar[0].Start);
    }

    [Fact]
    public void CalendarFallBackIncludesRepeatedHourAndDayBucketsUseTimezone()
    {
        var now = Timestamp("2026-11-02T04:30:00Z");
        Assert.Equal(TimeSpan.FromHours(24.5), now - UsagePeriods.GetPeriods(now, "calendar", "America/New_York")[0].Start);
        Assert.Equal("2026-11-01", UsagePeriods.GetDay(now, "America/New_York"));
        Assert.Equal("2026-11-02", UsagePeriods.GetDay(now, "UTC"));
    }

    [Fact]
    public void CalendarHandlesMidnightOffsetChanges()
    {
        var spring = UsagePeriods.GetPeriods(Timestamp("2026-03-08T08:00:00Z"), "calendar", "America/Havana");
        Assert.Equal(Timestamp("2026-03-08T05:00:00Z"), spring[0].Start);
        var fall = UsagePeriods.GetPeriods(Timestamp("2026-11-01T08:00:00Z"), "calendar", "America/Havana");
        Assert.Equal(Timestamp("2026-11-01T04:00:00Z"), fall[0].Start);
    }

    [Fact]
    public void InvalidPeriodInputsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => UsagePeriods.GetPeriods(DateTimeOffset.UtcNow, "invalid", "UTC"));
        Assert.Throws<TimeZoneNotFoundException>(() => UsagePeriods.GetPeriods(DateTimeOffset.UtcNow, "calendar", "not-a-timezone"));
        Assert.Throws<ArgumentException>(() => UsagePeriods.GetPeriods(DateTimeOffset.UtcNow, "calendar", " "));
    }

    [Fact]
    public void DefaultCatalogAndEmptySourcesAreValid()
    {
        SettingsValidator.Validate(Settings());
        Assert.Equal(24, PricingDefaults.CreateCatalog().Rates.Count);
    }

    [Fact]
    public void SettingsRejectRelativeSourcesAndUnknownTimezone()
    {
        Assert.Throws<ArgumentException>(() => SettingsValidator.Validate(Settings() with { Sources = ["relative"] }));
        Assert.Throws<TimeZoneNotFoundException>(() => SettingsValidator.Validate(Settings() with { Timezone = "bad-zone" }));
    }

    [Theory]
    [InlineData("", "standard", 0, 100, 1)]
    [InlineData("model", "priority", 0, 100, 1)]
    [InlineData("model", "standard", -1, 100, 1)]
    [InlineData("model", "standard", 100, 0, 1)]
    [InlineData("model", "standard", 0, 100, -1)]
    public void SettingsRejectInvalidRates(string model, string tier, long min, long max, int price)
    {
        var settings = Settings();
        settings = settings with { Pricing = settings.Pricing with { Rates = [new PriceRate(model, tier, min, max, price, 0m, 0m)] } };
        Assert.Throws<ArgumentException>(() => SettingsValidator.Validate(settings));
    }

    [Theory]
    [InlineData(100L)]
    [InlineData(null)]
    public void SettingsRejectOverlappingBands(long? firstMax)
    {
        var settings = Settings();
        settings = settings with { Pricing = settings.Pricing with { Rates = [new PriceRate("m", "standard", 0, firstMax, 1m, 1m, 1m), new PriceRate("m", "standard", 100, null, 1m, 1m, 1m)] } };
        Assert.Throws<ArgumentException>(() => SettingsValidator.Validate(settings));
    }

    [Fact]
    public void SettingsValidateExplicitMappings()
    {
        var settings = Settings();
        Assert.Throws<ArgumentException>(() => SettingsValidator.Validate(settings with { Pricing = settings.Pricing with { ModelAliases = new Dictionary<string, string> { [""] = "m" } } }));
        Assert.Throws<ArgumentException>(() => SettingsValidator.Validate(settings with { Pricing = settings.Pricing with { TierOverrides = new Dictionary<string, string> { ["m"] = "unknown" } } }));
        Assert.Throws<ArgumentException>(() => SettingsValidator.Validate(settings with { Pricing = settings.Pricing with { CacheWriteLifetimeOverrides = new Dictionary<string, string> { ["m"] = "forever" } } }));
        SettingsValidator.Validate(settings with { Pricing = settings.Pricing with { ModelAliases = new Dictionary<string, string> { ["alias"] = "gpt-6-astra" }, TierOverrides = new Dictionary<string, string> { ["alias"] = "standard" }, CacheWriteLifetimeOverrides = new Dictionary<string, string> { ["alias"] = "30m" } } });
    }

    private static ViewerSettings Settings() => new([], "America/New_York", PricingDefaults.CreateCatalog());

    private static UsageFact Fact(TokenUsage tokens, string? model = "gpt-6-astra", string? tier = "default") => new(Guid.NewGuid().ToString(), "session", DateTimeOffset.UtcNow, model, tier, tokens);

    private static DateTimeOffset Timestamp(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
