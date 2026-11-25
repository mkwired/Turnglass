namespace Turnglass.Core;

public static class PricingDefaults
{
    public static PricingCatalog CreateCatalog()
    {
        var rates = new List<PriceRate>();
        AddModel(rates, "gpt-6-astra", 10m, 1m, 50m, 6m);
        AddModel(rates, "gpt-6.2-astra", 8m, 0.4m, 40m, 3m);
        AddModel(rates, "gpt-6.2-sol", 2m, 0.1m, 10m, 3m);
        return new PricingCatalog("OpenAI 2026-11-21 (6.2 Ultrafast promotional snapshot)", "https://developers.openai.com/api/docs/pricing", rates,
            new Dictionary<string, string>(StringComparer.Ordinal), new Dictionary<string, string>(StringComparer.Ordinal), new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static void AddModel(List<PriceRate> rates, string model, decimal input, decimal cached, decimal output, decimal ultrafastMultiplier)
    {
        // These multipliers are documented for these three models, not a universal pricing rule.
        foreach (var (tier, multiplier) in new[] { ("standard", 1m), ("fast", 2m), ("batch", 0.5m), ("ultrafast", ultrafastMultiplier) })
        {
            rates.Add(new PriceRate(model, tier, 0, 272_000, input * multiplier, cached * multiplier, output * multiplier, input * 1.25m * multiplier, input * 2.5m * multiplier));
            rates.Add(new PriceRate(model, tier, 272_001, null, input * 2m * multiplier, cached * 2m * multiplier, output * 1.5m * multiplier, input * 2.5m * multiplier, input * 5m * multiplier));
        }
    }
}
