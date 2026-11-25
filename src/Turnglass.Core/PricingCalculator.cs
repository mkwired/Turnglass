namespace Turnglass.Core;

public static class PricingCalculator
{
    public static UsageTotals Calculate(IEnumerable<UsageFact> facts, PricingCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(catalog);
        var input = 0L;
        var cached = 0L;
        var writes = 0L;
        var output = 0L;
        var reasoning = 0L;
        var cost = 0m;
        var unpriced = 0L;
        var unpricedEvents = 0;
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in facts)
        {
            var tokens = fact.Tokens;
            input = checked(input + tokens.InputTokens);
            cached = checked(cached + tokens.CachedInputTokens);
            writes = checked(writes + tokens.CacheWriteInputTokens);
            output = checked(output + tokens.OutputTokens);
            reasoning = checked(reasoning + tokens.ReasoningOutputTokens);
            var (Cost, UnpricedTokens, Reason) = Price(fact, catalog);
            cost += Cost;
            if (Reason is not null)
            {
                unpriced += UnpricedTokens;
                unpricedEvents++;
                warnings.Add(Reason);
            }
        }

        return new UsageTotals(new TokenUsage(input, cached, writes, output, reasoning), cost, unpriced, unpricedEvents, [.. warnings.Order(StringComparer.Ordinal)]);
    }

    public static string? NormalizeTier(string? tier) => tier?.Trim().ToLowerInvariant() switch
    {
        "default" or "standard" => "standard",
        "priority" or "fast" => "fast",
        "ultrafast" => "ultrafast",
        "flex" => "flex",
        "batch" => "batch",
        _ => null,
    };

    private static (decimal Cost, long UnpricedTokens, string? Reason) Price(UsageFact fact, PricingCatalog catalog)
    {
        var tokens = fact.Tokens;
        var model = fact.Model;
        if (model is null)
        {
            return (0m, tokens.TotalTokens, "Unknown model");
        }

        if (catalog.ModelAliases.TryGetValue(model, out var alias))
        {
            model = alias;
        }

        var tier = fact.ServiceTier;
        if (catalog.TierOverrides.TryGetValue(fact.Model!, out var tierOverride))
        {
            tier = tierOverride;
        }

        tier = NormalizeTier(tier);
        if (tier is null)
        {
            return (0m, tokens.TotalTokens, $"Unknown service tier: {model}");
        }

        if (!fact.ContextKnown)
        {
            return (0m, tokens.TotalTokens, $"Unknown per-request context: {model}");
        }

        var rate = catalog.Rates.FirstOrDefault(candidate => candidate.Model == model && candidate.Tier == tier && tokens.InputTokens >= candidate.MinInputTokens && (candidate.MaxInputTokens is null || tokens.InputTokens <= candidate.MaxInputTokens));
        if (rate is null)
        {
            return (0m, tokens.TotalTokens, $"Missing rate: {model} / {tier}");
        }

        var ordinaryInput = tokens.InputTokens - tokens.CachedInputTokens - tokens.CacheWriteInputTokens;
        if (ordinaryInput < 0 || tokens.CachedInputTokens < 0 || tokens.CacheWriteInputTokens < 0 || tokens.OutputTokens < 0 || tokens.ReasoningOutputTokens < 0 || tokens.ReasoningOutputTokens > tokens.OutputTokens)
        {
            return (0m, Math.Max(0, tokens.TotalTokens), "Invalid token counters");
        }

        var cost = ((ordinaryInput * rate.InputPerMillion) + (tokens.CachedInputTokens * rate.CachedInputPerMillion) + (tokens.OutputTokens * rate.OutputPerMillion)) / 1_000_000m;
        if (tokens.CacheWriteInputTokens == 0)
        {
            return (cost, 0, null);
        }

        var lifetime = fact.CacheWriteLifetime;
        if (catalog.CacheWriteLifetimeOverrides.TryGetValue(fact.Model!, out var lifetimeOverride))
        {
            lifetime = lifetimeOverride;
        }

        var writeRate = lifetime switch
        {
            "5m" => rate.CacheWrite5mPerMillion,
            "1h" => rate.CacheWrite1hPerMillion,
            "30m" => rate.CacheWrite30mPerMillion,
            "12h" => rate.CacheWrite12hPerMillion,
            _ => null,
        };
        if (writeRate is null)
        {
            return (cost, tokens.CacheWriteInputTokens, $"Unknown cache-write lifetime or rate: {model}");
        }

        return (cost + (tokens.CacheWriteInputTokens * writeRate.Value / 1_000_000m), 0, null);
    }
}
