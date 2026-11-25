using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Turnglass.Core;

namespace Turnglass.Infrastructure;

public sealed class CopilotIndexer(CopilotDatabase database, TimeProvider clock, ILogger<CopilotIndexer> logger)
{
    private static readonly Action<ILogger, string, Exception?> _fileError = LoggerMessage.Define<string>(LogLevel.Warning, new EventId(3, nameof(CopilotIndexer)), "A Copilot usage file could not be indexed: {FileName}");

    public Task RefreshAsync(CancellationToken cancellationToken = default) => RefreshAsync(false, cancellationToken);

    public async Task RefreshAsync(bool force, CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var fileLock = force ? await database.AcquireLockAsync(cancellationToken).ConfigureAwait(false) : database.TryAcquireLock();
        if (fileLock is null)
        {
            return;
        }

        using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var settings = await CopilotDatabase.ReadSettingsAsync(connection, null, cancellationToken).ConfigureAwait(false);
        var prior = await CopilotDatabase.ReadAsync<CopilotIndexStatus>(connection, null, "configuration", "index", cancellationToken).ConfigureAwait(false)
            ?? EmptyStatus();
        await SaveStatusAsync(connection, prior with { IsIndexing = true }, cancellationToken).ConfigureAwait(false);
        var warnings = new List<string>();
        var statuses = new List<CopilotSourceStatus>();
        try
        {
            var roots = EffectiveRoots(settings);
            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!root.Enabled)
                {
                    statuses.Add(new CopilotSourceStatus(root.Kind, root.Path, false, Directory.Exists(root.Path), 0, null));
                    continue;
                }

                if (!Directory.Exists(root.Path))
                {
                    statuses.Add(new CopilotSourceStatus(root.Kind, root.Path, true, false, 0, "Source directory is unavailable; retained usage remains available."));
                    warnings.Add($"Copilot source unavailable: {root.Path}");
                    continue;
                }

                IReadOnlyList<CopilotSourceFile> files;
                try
                {
                    files = CopilotDiscovery.EnumerateFiles(root);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    statuses.Add(new CopilotSourceStatus(root.Kind, root.Path, true, false, 0, "Source directory could not be read; retained usage remains available."));
                    warnings.Add($"Could not scan Copilot source: {root.Path}");
                    continue;
                }

                var failed = false;
                foreach (var file in files.DistinctBy(file => file.Path, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        await ImportFileAsync(connection, root, file, force, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
                    {
                        // Retention can remove a file between discovery and opening it. The ledger remains intact.
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                    {
                        failed = true;
                        warnings.Add($"Could not read Copilot usage file: {Path.GetFileName(file.Path)}");
                        _fileError(logger, Path.GetFileName(file.Path), null);
                    }
                }

                statuses.Add(new CopilotSourceStatus(root.Kind, root.Path, true, !failed, files.Count, failed ? "Some usage files could not be read; retained usage remains available." : null));
            }

            var remembered = await CopilotDatabase.ReadAllAsync<CopilotSourceStatus>(connection, null, "sources", cancellationToken).ConfigureAwait(false);
            var activeIds = statuses.Select(status => SourceId(status.Kind, status.Path)).ToHashSet(StringComparer.Ordinal);
            statuses.AddRange(remembered.Where(status => !activeIds.Contains(SourceId(status.Kind, status.Path))).Select(status => status with { Enabled = false, Error = null }));
            var checkpoints = await CopilotDatabase.ReadAllAsync<CopilotFileCheckpoint>(connection, null, "checkpoints", cancellationToken).ConfigureAwait(false);
            warnings.AddRange(checkpoints.SelectMany(checkpoint => checkpoint.Warnings));
            var successful = statuses.Where(status => status.Enabled).All(status => status.Available);
            var completed = new CopilotIndexStatus(true, false, successful ? clock.GetUtcNow() : prior.LastSuccessfulScanUtc, 0, null, null,
                checkpoints.Sum(checkpoint => checkpoint.SkippedRecords), 0, statuses, [.. warnings.Distinct(StringComparer.Ordinal).Take(50)]);
            using var transaction = connection.BeginTransaction();
            foreach (var status in statuses)
            {
                await CopilotDatabase.SaveAsync(connection, transaction, "sources", SourceId(status.Kind, status.Path), status, cancellationToken).ConfigureAwait(false);
            }

            await CopilotDatabase.SaveAsync(connection, transaction, "configuration", "index", completed, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await SaveStatusAsync(connection, prior with { IsIndexing = false, Warnings = ["The latest Copilot scan was interrupted; retained usage remains available."] }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    internal static CopilotIndexStatus EmptyStatus() => new(false, false, null, 0, null, null, 0, 0, [], []);

    internal static string SourceId(string kind, string path) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}|{Path.GetFullPath(path).ToUpperInvariant()}")));

    internal static IReadOnlyList<CopilotSourceRoot> EffectiveRoots(CopilotSettings settings)
    {
        var roots = new Dictionary<string, CopilotSourceRoot>(StringComparer.Ordinal);
        foreach (var source in settings.AutoDiscover ? CopilotDiscovery.GetDefaults() : [])
        {
            roots[SourceId(source.Kind, source.Path)] = source with { Path = Path.GetFullPath(source.Path) };
        }

        foreach (var source in settings.Sources)
        {
            roots[SourceId(source.Kind, source.Path)] = source with { Path = Path.GetFullPath(source.Path) };
        }

        return [.. roots.Values.OrderBy(source => source.Kind, StringComparer.Ordinal).ThenBy(source => source.Path, StringComparer.OrdinalIgnoreCase)];
    }

    private async Task ImportFileAsync(SqliteConnection connection, CopilotSourceRoot root, CopilotSourceFile file, bool force, CancellationToken cancellationToken)
    {
        var metadata = new FileInfo(file.Path);
        var sourceId = SourceId(root.Kind, root.Path);
        var checkpointId = $"{sourceId}|{Path.GetFullPath(file.Path)}";
        var checkpoint = await CopilotDatabase.ReadAsync<CopilotFileCheckpoint>(connection, null, "checkpoints", checkpointId, cancellationToken).ConfigureAwait(false);
        if (!force && checkpoint is not null && checkpoint.Length == metadata.Length && checkpoint.LastWriteUtcTicks == metadata.LastWriteTimeUtc.Ticks)
        {
            return;
        }

        using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        var modified = metadata.LastWriteTimeUtc.Ticks;
        var state = new CopilotParserState(file);
        var parsed = new List<CopilotParsedUsage>();
        await foreach (var line in JsonLines.ReadAsync(stream, 0, cancellationToken).ConfigureAwait(false))
        {
            if (!string.IsNullOrWhiteSpace(line.Text) && CopilotParser.Parse(line.Text, state) is { } fact)
            {
                parsed.Add(fact);
            }
        }

        var warnings = new List<string>(state.Warnings);
        using var transaction = connection.BeginTransaction();
        foreach (var record in parsed)
        {
            var existing = await CopilotDatabase.ReadAsync<CopilotParsedUsage>(connection, transaction, "facts", record.Fact.Id, cancellationToken).ConfigureAwait(false);
            var value = existing is null ? record : Merge(existing, record, warnings);
            await CopilotDatabase.SaveAsync(connection, transaction, "facts", value.Fact.Id, value, cancellationToken).ConfigureAwait(false);
            using var provenance = connection.CreateCommand();
            provenance.Transaction = transaction;
            provenance.CommandText = "INSERT INTO observations(fact_id,source_id,path,observed_at) VALUES($fact,$source,$path,$observed) ON CONFLICT(fact_id,source_id,path) DO UPDATE SET observed_at=excluded.observed_at";
            provenance.Parameters.AddWithValue("$fact", value.Fact.Id);
            provenance.Parameters.AddWithValue("$source", sourceId);
            provenance.Parameters.AddWithValue("$path", file.Path);
            provenance.Parameters.AddWithValue("$observed", clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
            await provenance.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var updated = new CopilotFileCheckpoint(file.Path, sourceId, length, modified, state.SkippedRecords, [.. warnings.Distinct(StringComparer.Ordinal).Take(30)]);
        await CopilotDatabase.SaveAsync(connection, transaction, "checkpoints", checkpointId, updated, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static CopilotParsedUsage Merge(CopilotParsedUsage existing, CopilotParsedUsage incoming, List<string> warnings)
    {
        var first = existing.Fact;
        var next = incoming.Fact;
        var identity = existing with
        {
            CorrelationIds = [.. existing.CorrelationIds.Concat(incoming.CorrelationIds).Distinct(StringComparer.Ordinal)],
            ParentSessionId = existing.ParentSessionId ?? incoming.ParentSessionId,
            AccountingSequence = existing.AccountingSequence ?? incoming.AccountingSequence,
        };
        if (first.InputTokens != next.InputTokens || first.OutputTokens != next.OutputTokens
            || first.CachedInputTokens is not null && next.CachedInputTokens is not null && first.CachedInputTokens != next.CachedInputTokens
            || first.CacheWriteInputTokens is not null && next.CacheWriteInputTokens is not null && first.CacheWriteInputTokens != next.CacheWriteInputTokens)
        {
            warnings.Add("Conflicting Copilot token counters share an identity; the existing counters were retained.");
            return identity;
        }

        var enriched = identity with
        {
            Fact = first with
            {
                Model = incoming.HasResponseModel && !existing.HasResponseModel ? next.Model : first.Model ?? next.Model,
                Client = first.Client == "shared" ? next.Client : first.Client,
                CachedInputTokens = first.CachedInputTokens ?? next.CachedInputTokens,
                CacheWriteInputTokens = first.CacheWriteInputTokens ?? next.CacheWriteInputTokens,
                ReasoningOutputTokens = first.ReasoningOutputTokens ?? next.ReasoningOutputTokens,
                ServiceTier = first.ServiceTier ?? next.ServiceTier,
                CacheWriteLifetime = first.CacheWriteLifetime ?? next.CacheWriteLifetime,
            },
            HasResponseModel = existing.HasResponseModel || incoming.HasResponseModel,
        };
        if (enriched.Fact.CachedInputTokens.GetValueOrDefault() > enriched.Fact.InputTokens - enriched.Fact.CacheWriteInputTokens.GetValueOrDefault())
        {
            warnings.Add("Conflicting Copilot cache breakdowns share an identity; the existing breakdown was retained.");
            return identity;
        }

        return enriched;
    }

    private static async Task SaveStatusAsync(SqliteConnection connection, CopilotIndexStatus status, CancellationToken cancellationToken)
    {
        using var transaction = connection.BeginTransaction();
        await CopilotDatabase.SaveAsync(connection, transaction, "configuration", "index", status, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal sealed record CopilotFileCheckpoint(string Path, string SourceId, long Length, long LastWriteUtcTicks, int SkippedRecords, IReadOnlyList<string> Warnings);
}
