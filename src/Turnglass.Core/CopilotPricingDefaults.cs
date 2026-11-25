namespace Turnglass.Core;

/// <summary>Standard API benchmark rates verified November 25, 2026. References are documented in README.</summary>
public static class CopilotPricingDefaults
{
    public const string Version = "Copilot standard API benchmarks 2026-11-25";

    public static PricingCatalog Merge(PricingCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var defaults = new PriceRate[]
        {
            new("claude-haiku-4.5", "standard", 0, null, 1m, 0.1m, 5m, CacheWrite5mPerMillion: 1.25m, CacheWrite1hPerMillion: 2m),
            new("claude-sonnet-4.5", "standard", 0, null, 3m, 0.3m, 15m, CacheWrite5mPerMillion: 3.75m, CacheWrite1hPerMillion: 6m),
            new("claude-sonnet-4.6", "standard", 0, null, 3m, 0.3m, 15m, CacheWrite5mPerMillion: 3.75m, CacheWrite1hPerMillion: 6m),
            new("claude-opus-4.6", "standard", 0, null, 5m, 0.5m, 25m, CacheWrite5mPerMillion: 6.25m, CacheWrite1hPerMillion: 10m),
            new("gpt-5.3-codex", "standard", 0, null, 1.75m, 0.175m, 14m),
            new("gpt-5.4", "standard", 0, 272_000, 2.5m, 0.25m, 15m),
            new("gpt-5.4", "standard", 272_001, null, 5m, 0.5m, 22.5m),
            new("gpt-6-luna", "standard", 0, 272_000, 0.1m, 0.01m, 0.5m, CacheWrite30mPerMillion: 0.125m, CacheWrite12hPerMillion: 0.25m),
            new("gpt-6-luna", "standard", 272_001, null, 0.2m, 0.02m, 0.75m, CacheWrite30mPerMillion: 0.25m, CacheWrite12hPerMillion: 0.5m),
            new("mai-code-1.1-flash", "standard", 0, null, 0.2m, 0.02m, 1.2m),
        };
        var existing = catalog.Rates.Select(rate => (rate.Model, rate.Tier)).ToHashSet();
        var additions = defaults.Where(rate => !existing.Contains((rate.Model, rate.Tier))).ToArray();
        var aliases = new Dictionary<string, string>(catalog.ModelAliases, StringComparer.Ordinal);
        var addedAlias = aliases.TryAdd("claude-haiku-4-5-20251001", "claude-haiku-4.5");
        addedAlias |= aliases.TryAdd("gpt-5.4-2026-03-05", "gpt-5.4");
        return additions.Length == 0 && !addedAlias ? catalog : catalog with { Version = $"{catalog.Version}; {Version}", Rates = [.. catalog.Rates, .. additions], ModelAliases = aliases };
    }
}
