namespace Turnglass.Core;

public static class CopilotPricing
{
    public static CopilotPricingResult Calculate(IEnumerable<CopilotUsageFact> facts, PricingCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(catalog);
        var assumptions = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<UsageFact>();
        foreach (var fact in facts)
        {
            var tier = fact.ServiceTier;
            if (string.IsNullOrWhiteSpace(tier))
            {
                tier = "standard";
                if (fact.Model is null || !catalog.TierOverrides.ContainsKey(fact.Model))
                {
                    assumptions.Add("Standard API tier is assumed where the source does not record a service tier.");
                }
            }

            if (fact.CachedInputTokens is null || fact.CacheWriteInputTokens is null)
            {
                assumptions.Add("Input without a recorded cache breakdown is priced as uncached input. Missing cache counters are unknown, not observed zeros.");
            }

            if (fact.Model is not null && catalog.CacheWriteLifetimeOverrides.ContainsKey(fact.Model) && fact.CacheWriteInputTokens > 0)
            {
                assumptions.Add("Cache-write retention uses the configured model override.");
            }

            if (fact.Model is not null && catalog.TierOverrides.ContainsKey(fact.Model))
            {
                assumptions.Add("Service tier uses the configured model override.");
            }

            var tokens = new TokenUsage(fact.InputTokens, fact.CachedInputTokens ?? 0, fact.CacheWriteInputTokens ?? 0, fact.OutputTokens, fact.ReasoningOutputTokens ?? 0);
            normalized.Add(new UsageFact(fact.Id, fact.SessionId, fact.Timestamp, fact.Model, tier, tokens, CacheWriteLifetime: fact.CacheWriteLifetime));
        }

        return new CopilotPricingResult(PricingCalculator.Calculate(normalized, catalog), [.. assumptions.Order(StringComparer.Ordinal)]);
    }
}
