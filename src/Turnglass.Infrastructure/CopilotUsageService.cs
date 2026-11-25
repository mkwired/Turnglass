using System.Globalization;
using Microsoft.Data.Sqlite;
using Turnglass.Core;

namespace Turnglass.Infrastructure;

public sealed class CopilotUsageService(CopilotDatabase database, CopilotIndexer indexer, IViewerService viewer, TimeProvider clock) : ICopilotUsageService
{
    public async Task<CopilotSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await CopilotDatabase.ReadSettingsAsync(connection, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CopilotSettings> UpdateSettingsAsync(CopilotSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(settings.Sources);
        var normalized = new List<CopilotSourceRoot>();
        foreach (var source in settings.Sources)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (source.Kind is not ("visual-studio" or "vscode" or "shared"))
            {
                throw new ArgumentException("Copilot source kind must be visual-studio, vscode, or shared.", nameof(settings));
            }

            if (string.IsNullOrWhiteSpace(source.Path) || !Path.IsPathFullyQualified(source.Path))
            {
                throw new ArgumentException("Copilot source directories must be absolute paths.", nameof(settings));
            }

            normalized.Add(source with { Path = Path.GetFullPath(source.Path) });
        }

        var value = settings with { Sources = [.. normalized.DistinctBy(source => CopilotIndexer.SourceId(source.Kind, source.Path))] };
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var fileLock = await database.AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        await CopilotDatabase.SaveAsync(connection, transaction, "configuration", "settings", value, cancellationToken).ConfigureAwait(false);
        var status = await CopilotDatabase.ReadAsync<CopilotIndexStatus>(connection, transaction, "configuration", "index", cancellationToken).ConfigureAwait(false) ?? CopilotIndexer.EmptyStatus();
        await CopilotDatabase.SaveAsync(connection, transaction, "configuration", "index", status with { InitialScanComplete = false, Warnings = ["Copilot sources changed; waiting for a scan. Retained usage remains available."] }, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return value;
    }

    public async Task<CopilotIndexStatus> GetIndexStatusAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var snapshot = await ReadSnapshotAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        return await ReadStatusAsync(connection, transaction, snapshot, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CopilotUsageReport> GetUsageAsync(CopilotUsageQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Client is not (null or "visual-studio" or "vscode" or "shared"))
        {
            throw new ArgumentException("Copilot client must be visual-studio, vscode, or shared.", nameof(query));
        }

        // Read shared configuration before opening a ledger transaction; the databases have independent locks.
        var settings = await viewer.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var timezone = query.Timezone ?? settings.Timezone;
        var now = clock.GetUtcNow();
        var periods = UsagePeriods.GetPeriods(now, query.Mode, timezone);
        var from = query.From ?? now.AddDays(-30);
        var to = query.To ?? now;
        if (from >= to || to - from > TimeSpan.FromDays(3660))
        {
            throw new ArgumentException("The chart date range must be increasing and no longer than ten years.", nameof(query));
        }

        using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var snapshot = await ReadSnapshotAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var facts = snapshot.Facts.Where(fact => (query.Client is null || fact.Client == query.Client) && (query.Model is null || fact.Model == query.Model)).ToArray();
        var assumptions = new HashSet<string>(StringComparer.Ordinal);
        UsageTotals Price(IEnumerable<CopilotUsageFact> selected)
        {
            var priced = CopilotPricing.Calculate(selected, settings.Pricing);
            assumptions.UnionWith(priced.Assumptions);
            return priced.Totals;
        }

        var summaries = periods.Select(period => new PeriodUsage(period.Label, period.Start, period.End, Price(facts.Where(fact => fact.Timestamp >= period.Start && fact.Timestamp < period.End)))).ToArray();
        var grouped = facts.Where(fact => fact.Timestamp >= from && fact.Timestamp < to).GroupBy(fact => UsagePeriods.GetDay(fact.Timestamp, timezone)).ToDictionary(group => group.Key, Price, StringComparer.Ordinal);
        var startDay = DateOnly.ParseExact(UsagePeriods.GetDay(from, timezone), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var endDay = DateOnly.ParseExact(UsagePeriods.GetDay(to.AddTicks(-1), timezone), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var buckets = new List<UsageBucket>();
        var empty = CopilotPricing.Calculate([], settings.Pricing).Totals;
        for (var day = startDay; day <= endDay; day = day.AddDays(1))
        {
            var date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            buckets.Add(new UsageBucket(date, grouped.TryGetValue(date, out var total) ? total : empty));
        }

        return new CopilotUsageReport(query.Mode, timezone, now, settings.Pricing.Version, summaries, buckets, [.. assumptions.Order(StringComparer.Ordinal)], facts.Length,
            facts.Count(fact => fact.CachedInputTokens is null), facts.Count(fact => fact.CacheWriteInputTokens is null), await ReadStatusAsync(connection, transaction, snapshot, cancellationToken).ConfigureAwait(false));
    }

    public async Task<CopilotFilterOptions> GetFiltersAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var snapshot = await ReadSnapshotAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        return new CopilotFilterOptions(["visual-studio", "vscode", "shared"], [.. snapshot.Facts.Select(fact => fact.Model).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
    }

    public Task RescanAsync(CancellationToken cancellationToken = default) => indexer.RefreshAsync(true, cancellationToken);

    private static async Task<Snapshot> ReadSnapshotAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var stored = await CopilotDatabase.ReadAllAsync<CopilotParsedUsage>(connection, transaction, "facts", cancellationToken).ConfigureAwait(false);
        var families = new Dictionary<string, string>(StringComparer.Ordinal);
        string Family(string session)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (families.TryGetValue(session, out var parent) && visited.Add(session))
            {
                session = parent;
            }

            return session;
        }

        foreach (var record in stored.Where(record => record.ParentSessionId is not null))
        {
            var child = Family(record.Fact.SessionId);
            var parent = Family(record.ParentSessionId!);
            if (child != parent)
            {
                families[child] = parent;
            }
        }

        var warnings = new List<string>();
        var sdk = CollapseAliases([.. stored.Where(record => record.Kind == CopilotSourceKind.Sdk)], Family, warnings);
        var debug = CollapseAliases([.. stored.Where(record => record.Kind == CopilotSourceKind.VsCodeDebug)], Family, warnings);
        var sdkFamilies = sdk.Select(record => Family(record.Fact.SessionId)).ToHashSet(StringComparer.Ordinal);
        var sdkCorrelations = sdk.SelectMany(record => record.CorrelationIds).ToHashSet(StringComparer.Ordinal);
        var debugByCorrelation = debug.SelectMany(record => record.CorrelationIds.Select(correlation => (correlation, record.Fact.Client)))
            .GroupBy(item => item.correlation, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Select(item => item.Client).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var facts = new List<CopilotUsageFact>();
        foreach (var record in sdk)
        {
            var clients = record.CorrelationIds.Where(debugByCorrelation.ContainsKey).SelectMany(id => debugByCorrelation[id]).Distinct(StringComparer.Ordinal).ToArray();
            facts.Add(record.Fact.Client == "shared" && clients.Length == 1 ? record.Fact with { Client = clients[0] } : record.Fact);
        }

        var ambiguous = 0;
        foreach (var record in debug)
        {
            if (record.CorrelationIds.Any(sdkCorrelations.Contains))
            {
                continue;
            }

            if (sdkFamilies.Contains(Family(record.Fact.SessionId)))
            {
                ambiguous++;
                continue;
            }

            facts.Add(record.Fact);
        }

        if (ambiguous > 0)
        {
            warnings.Add("SDK usage is authoritative for overlapping session families; debug records without matching request IDs were excluded to avoid double counting.");
        }

        if (facts.Any(fact => fact.Client == "shared") && facts.Any(fact => fact.Client == "vscode"))
        {
            warnings.Add("Shared Copilot records without client or request correlation may overlap VS Code records. Client attribution and historical coverage are incomplete.");
        }

        return new Snapshot(facts, ambiguous, warnings);
    }

    private static CopilotParsedUsage[] CollapseAliases(CopilotParsedUsage[] records, Func<string, string> family, List<string> warnings)
    {
        var parents = Enumerable.Range(0, records.Length).ToArray();
        var aliases = new Dictionary<(string Session, string Alias), int>();
        var sequences = new Dictionary<(string Session, long Sequence), int>();
        int Root(int index)
        {
            while (parents[index] != index)
            {
                parents[index] = parents[parents[index]];
                index = parents[index];
            }

            return index;
        }

        for (var index = 0; index < records.Length; index++)
        {
            var record = records[index];
            foreach (var alias in record.CorrelationIds)
            {
                var key = (family(record.Fact.SessionId), alias);
                if (aliases.TryGetValue(key, out var previous))
                {
                    parents[Root(index)] = Root(previous);
                }
                else
                {
                    aliases[key] = index;
                }
            }

            if (record.AccountingSequence is { } sequence)
            {
                var key = (record.Fact.SessionId, sequence);
                if (sequences.TryGetValue(key, out var previous))
                {
                    parents[Root(index)] = Root(previous);
                }
                else
                {
                    sequences[key] = index;
                }
            }
        }

        var result = new List<CopilotParsedUsage>();
        foreach (var group in Enumerable.Range(0, records.Length).GroupBy(Root))
        {
            var candidates = group.Select(index => records[index]).OrderByDescending(record => record.HasResponseModel)
                .ThenByDescending(record => (record.Fact.Model is null ? 0 : 1) + (record.Fact.CachedInputTokens is null ? 0 : 1) + (record.Fact.CacheWriteInputTokens is null ? 0 : 1))
                .ThenBy(record => record.Fact.Timestamp).ThenBy(record => record.Fact.Id, StringComparer.Ordinal).ToArray();
            var merged = candidates[0];
            foreach (var candidate in candidates.Skip(1))
            {
                merged = CopilotIndexer.Merge(merged, candidate, warnings);
            }

            result.Add(merged);
        }

        return [.. result];
    }

    private static async Task<CopilotIndexStatus> ReadStatusAsync(SqliteConnection connection, SqliteTransaction transaction, Snapshot snapshot, CancellationToken cancellationToken)
    {
        var status = await CopilotDatabase.ReadAsync<CopilotIndexStatus>(connection, transaction, "configuration", "index", cancellationToken).ConfigureAwait(false) ?? CopilotIndexer.EmptyStatus();
        return status with
        {
            RecordCount = snapshot.Facts.Count,
            ObservedFrom = snapshot.Facts.Count == 0 ? null : snapshot.Facts.Min(fact => fact.Timestamp),
            ObservedTo = snapshot.Facts.Count == 0 ? null : snapshot.Facts.Max(fact => fact.Timestamp),
            AmbiguousRecords = snapshot.AmbiguousRecords,
            Warnings = [.. status.Warnings.Concat(snapshot.Warnings).Distinct(StringComparer.Ordinal).Take(50)],
        };
    }

    private sealed record Snapshot(IReadOnlyList<CopilotUsageFact> Facts, int AmbiguousRecords, IReadOnlyList<string> Warnings);
}
