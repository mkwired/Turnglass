namespace Turnglass.Core;

public static class SettingsValidator
{
    public static void Validate(ViewerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(settings.Sources);
        ArgumentNullException.ThrowIfNull(settings.Pricing);
        ArgumentNullException.ThrowIfNull(settings.Pricing.Rates);
        ArgumentNullException.ThrowIfNull(settings.Pricing.ModelAliases);
        ArgumentNullException.ThrowIfNull(settings.Pricing.TierOverrides);
        ArgumentNullException.ThrowIfNull(settings.Pricing.CacheWriteLifetimeOverrides);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Pricing.Version);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Timezone);
        _ = TimeZoneInfo.FindSystemTimeZoneById(settings.Timezone);
        foreach (var source in settings.Sources)
        {
            if (string.IsNullOrWhiteSpace(source) || !Path.IsPathFullyQualified(source))
            {
                throw new ArgumentException("Source directories must be absolute paths.", nameof(settings));
            }
        }

        foreach (var rate in settings.Pricing.Rates)
        {
            if (string.IsNullOrWhiteSpace(rate.Model) || PricingCalculator.NormalizeTier(rate.Tier) != rate.Tier || rate.MinInputTokens < 0 || rate.MaxInputTokens < rate.MinInputTokens || rate.InputPerMillion < 0 || rate.CachedInputPerMillion < 0 || rate.OutputPerMillion < 0 || rate.CacheWrite30mPerMillion < 0 || rate.CacheWrite12hPerMillion < 0 || rate.CacheWrite5mPerMillion < 0 || rate.CacheWrite1hPerMillion < 0)
            {
                throw new ArgumentException("Rates require a model, canonical tier, valid input range, and nonnegative prices.", nameof(settings));
            }
        }

        foreach (var group in settings.Pricing.Rates.GroupBy(rate => (rate.Model, rate.Tier)))
        {
            var ordered = group.OrderBy(rate => rate.MinInputTokens).ToArray();
            for (var index = 1; index < ordered.Length; index++)
            {
                if (ordered[index - 1].MaxInputTokens is null || ordered[index].MinInputTokens <= ordered[index - 1].MaxInputTokens)
                {
                    throw new ArgumentException("Pricing context bands must not overlap.", nameof(settings));
                }
            }
        }

        foreach (var pair in settings.Pricing.ModelAliases)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
            {
                throw new ArgumentException("Model mappings require nonempty names.", nameof(settings));
            }
        }

        foreach (var pair in settings.Pricing.TierOverrides)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || PricingCalculator.NormalizeTier(pair.Value) is null)
            {
                throw new ArgumentException("Tier overrides require a model and supported tier.", nameof(settings));
            }
        }

        foreach (var pair in settings.Pricing.CacheWriteLifetimeOverrides)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is not ("5m" or "1h" or "30m" or "12h"))
            {
                throw new ArgumentException("Cache-write overrides must specify 5m, 1h, 30m or 12h.", nameof(settings));
            }
        }
    }
}
