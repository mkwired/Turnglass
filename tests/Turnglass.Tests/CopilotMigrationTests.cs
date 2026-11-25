using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Turnglass.Core;

namespace Turnglass.Tests;

public sealed class CopilotMigrationTests
{
    [Fact]
    public async Task StarterMigrationPreservesCustomBandsAndDoesNotRestoreLaterDeletedPrices()
    {
        using var fixture = new InfrastructureFixture();
        var original = await fixture.Viewer.GetSettingsAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        var custom = new PriceRate("gpt-5.4", "standard", 0, 100, 999m, 1m, 5m);
        await fixture.Viewer.UpdateSettingsAsync(original with { Pricing = original.Pricing with { Rates = [custom] } }, TestContext.Current.CancellationToken).ConfigureAwait(true);
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(fixture.Data, "session-viewer.db")}"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM configuration WHERE key='copilot-pricing-v1'";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        using (var upgraded = fixture.CreateServices())
        {
            var viewer = upgraded.GetRequiredService<IViewerService>();
            var updated = await viewer.GetSettingsAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Equal(custom, Assert.Single(updated.Pricing.Rates, rate => rate.Model == "gpt-5.4"));
            Assert.Contains(updated.Pricing.Rates, rate => rate.Model == "claude-haiku-4.5");
            Assert.Equal(original.Sources, updated.Sources);
            Assert.Equal(original.Timezone, updated.Timezone);
            await viewer.UpdateSettingsAsync(updated with { Pricing = updated.Pricing with { Rates = [custom] } }, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        using var restarted = fixture.CreateServices();
        var afterRestart = await restarted.GetRequiredService<IViewerService>().GetSettingsAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(custom, Assert.Single(afterRestart.Pricing.Rates));
    }
}
