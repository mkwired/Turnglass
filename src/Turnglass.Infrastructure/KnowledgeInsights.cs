using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Turnglass.Core;

namespace Turnglass.Infrastructure;

public sealed partial class KnowledgeService
{
    private static readonly TurnglassJsonContext _exportJsonContext = new(new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });

    public async Task<InsightPage> ListInsightsAsync(InsightQuery query, bool approvedOnly = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Limit, 100);
        if (query.Category is not null)
        {
            ValidateChoice(query.Category, ["preference", "lesson", "decision", "workflow"], nameof(query));
        }

        if (query.Status is not null)
        {
            ValidateChoice(query.Status, ["draft", "approved", "dismissed", "superseded"], nameof(query));
        }

        var scope = KnowledgeSource.Hash(JsonStorage.Serialize(new InsightCursorScope(query.Query, query.Project, query.Category, query.Status, approvedOnly)));
        var cursor = DecodeInsightCursor(query.Cursor, scope);
        using var connection = await knowledge.OpenAsync(cancellationToken).ConfigureAwait(false);
        var all = await KnowledgeDatabase.ReadAllAsync<Insight>(connection, null, "insights", cancellationToken).ConfigureAwait(false);
        var selected = all.Where(item => (!approvedOnly || item.Status == "approved")
            && (query.Status is null || item.Status == query.Status)
            && (query.Category is null || item.Category == query.Category)
            && (approvedOnly ? item.Scope == "global" || item.Scope == "project" && query.Project is not null && item.Project == query.Project : query.Project is null || item.Scope == "global" || item.Project == query.Project)
            && (string.IsNullOrWhiteSpace(query.Query) || item.Statement.Contains(query.Query, StringComparison.OrdinalIgnoreCase) || item.Rationale.Contains(query.Query, StringComparison.OrdinalIgnoreCase))
            && (cursor is null || item.CreatedAt < cursor.Timestamp || item.CreatedAt == cursor.Timestamp && StringComparer.Ordinal.Compare(item.Id, cursor.Id) > 0))
            .OrderByDescending(item => item.CreatedAt).ThenBy(item => item.Id, StringComparer.Ordinal).Take(query.Limit + 1).ToArray();
        var page = selected.Take(query.Limit).ToArray();
        var hydrated = await HydrateAsync(page, cancellationToken).ConfigureAwait(false);
        var next = selected.Length > query.Limit ? Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonStorage.Serialize(new InsightCursor(scope, page[^1].CreatedAt, page[^1].Id)))) : null;
        return new InsightPage(hydrated, next);
    }

    public async Task<Insight?> GetInsightAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        using var connection = await knowledge.OpenAsync(cancellationToken).ConfigureAwait(false);
        var insight = await KnowledgeDatabase.ReadAsync<Insight>(connection, null, "insights", id, cancellationToken).ConfigureAwait(false);
        return insight is null ? null : (await HydrateAsync([insight], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<IReadOnlyList<Insight>> ReviewInsightsAsync(InsightReviewRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Actions is null || request.Actions.Count is < 1 or > 100 || request.Actions.Select(action => action.Id).Distinct(StringComparer.Ordinal).Count() != request.Actions.Count)
        {
            throw new ArgumentException("Review between 1 and 100 distinct insights in one batch.", nameof(request));
        }

        using var connection = await knowledge.OpenAsync(cancellationToken).ConfigureAwait(false);
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var indexLock = await database.AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction();
        var all = await KnowledgeDatabase.ReadAllAsync<Insight>(connection, transaction, "insights", cancellationToken).ConfigureAwait(false);
        var original = all.ToDictionary(item => item.Id, StringComparer.Ordinal);
        // Validate every expected revision against one snapshot before applying any action.
        foreach (var action in request.Actions)
        {
            var insight = RequireInsight(original, action.Id);
            RequireRevision(insight, action.ExpectedRevision);
            ValidateChoice(action.Action, ["approve", "dismiss", "edit", "merge", "supersede"], nameof(request));
            if (action.Action is "merge" or "supersede")
            {
                if (string.IsNullOrWhiteSpace(action.TargetId) || action.TargetId == action.Id || action.TargetRevision is null)
                {
                    throw new ArgumentException("Merge and supersede require a distinct target and its expected revision.", nameof(request));
                }

                RequireRevision(RequireInsight(original, action.TargetId), action.TargetRevision.Value);
                if (request.Actions.Any(other => other.Id == action.TargetId || other.Id != action.Id && other.TargetId == action.TargetId))
                {
                    throw new ArgumentException("Each source and target may participate only once in a batch.", nameof(request));
                }
            }
        }

        using var source = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var sourceTransaction = source.BeginTransaction(deferred: true);
        var sessions = await KnowledgeSource.SessionsAsync(source, sourceTransaction, cancellationToken).ConfigureAwait(false);
        var results = new Dictionary<string, Insight>(StringComparer.Ordinal);
        var now = clock.GetUtcNow();
        foreach (var action in request.Actions)
        {
            var insight = original[action.Id];
            if (insight.Status is "dismissed" or "superseded")
            {
                throw new KnowledgeConflictException("Dismissed or superseded insights cannot be reopened. Create a new supported draft instead.");
            }

            var updated = insight;
            switch (action.Action)
            {
                case "approve":
                    updated = insight with { Status = "approved" };
                    break;
                case "dismiss":
                    updated = insight with { Status = "dismissed" };
                    break;
                case "edit":
                    if (action.Draft is null)
                    {
                        throw new ArgumentException("Edit requires the complete revised draft.", nameof(request));
                    }

                    var evidence = await ValidateDraftAsync(action.Draft, source, sourceTransaction, sessions, cancellationToken).ConfigureAwait(false);
                    ValidateRelated(action.Draft, all, insight.Id);
                    updated = insight with { Statement = action.Draft.Statement.Trim(), Category = action.Draft.Category, Scope = action.Draft.Scope, Project = action.Draft.Project, SessionId = action.Draft.SessionId, Basis = action.Draft.Basis, Rationale = action.Draft.Rationale.Trim(), Evidence = evidence, RelatedInsightIds = action.Draft.RelatedInsightIds?.Distinct(StringComparer.Ordinal).ToArray() ?? [] };
                    break;
                case "merge":
                case "supersede":
                    var target = original[action.TargetId!];
                    if (target.Status is "dismissed" or "superseded" || action.Action == "supersede" && target.Status != "approved")
                    {
                        throw new KnowledgeConflictException("The replacement must be approved; merge targets must be draft or approved.");
                    }

                    if (target.Scope != insight.Scope || target.Project != insight.Project || target.SessionId != insight.SessionId)
                    {
                        throw new ArgumentException("Linked review actions require matching scopes, projects, and sessions.", nameof(request));
                    }

                    if (action.Action == "merge")
                    {
                        target = target with { Evidence = [.. target.Evidence.Concat(insight.Evidence).DistinctBy(item => (item.SessionId, item.EventId, item.Quote))] };
                    }

                    target = Revise(target with { RelatedInsightIds = [.. target.RelatedInsightIds.Append(insight.Id).Distinct(StringComparer.Ordinal)] }, action.Action == "merge" ? "merged-evidence" : "replacement", now, action.Note);
                    results[target.Id] = target;
                    updated = insight with { Status = "superseded", RelatedInsightIds = [.. insight.RelatedInsightIds.Append(target.Id).Distinct(StringComparer.Ordinal)] };
                    break;
            }

            results[updated.Id] = Revise(updated, action.Action, now, action.Note);
        }

        foreach (var item in results.Values)
        {
            await SaveInsightAsync(connection, transaction, item, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await HydrateAsync([.. results.Values], cancellationToken).ConfigureAwait(false);
    }

    public async Task<InsightExport> ExportInsightsAsync(InsightExportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateChoice(request.Format, ["markdown", "json"], nameof(request));
        if (request.Ids is null || request.Ids.Count is < 1 or > 100)
        {
            throw new ArgumentException("Select between 1 and 100 approved insights to export.", nameof(request));
        }

        using var connection = await knowledge.OpenAsync(cancellationToken).ConfigureAwait(false);
        var all = await KnowledgeDatabase.ReadAllAsync<Insight>(connection, null, "insights", cancellationToken).ConfigureAwait(false);
        var selected = new List<Insight>();
        foreach (var id in request.Ids.Distinct(StringComparer.Ordinal))
        {
            var insight = all.FirstOrDefault(item => item.Id == id) ?? throw new ArgumentException("An export selection does not exist.", nameof(request));
            if (insight.Status != "approved")
            {
                throw new KnowledgeConflictException("Only explicitly approved insights can be exported.");
            }

            selected.Add(insight);
        }

        var hydrated = await HydrateAsync(selected, cancellationToken).ConfigureAwait(false);
        if (request.Format == "json")
        {
            return new InsightExport("codex-approved-insights.json", "application/json", JsonSerializer.Serialize(hydrated, _exportJsonContext.IReadOnlyListInsight));
        }

        var builder = new StringBuilder("# Approved Codex insights\n\n");
        foreach (var item in hydrated)
        {
            builder.Append("## ").Append(EscapeMarkdown(item.Statement)).Append("\n\n")
                .Append("- Category: ").Append(item.Category).Append("\n- Basis: ").Append(item.Basis).Append("\n- Scope: ").Append(item.Scope)
                .Append("\n- Project: ").Append(EscapeMarkdown(item.Project ?? "all projects")).Append("\n- Session: ").Append(EscapeMarkdown(item.SessionId ?? "all matching sessions"))
                .Append("\n- Insight: ").Append(item.Id).Append(" (revision ").Append(item.Revision.ToString(CultureInfo.InvariantCulture)).Append(")\n\n")
                .Append(EscapeMarkdown(item.Rationale)).Append("\n\nEvidence:\n\n");
            foreach (var evidence in item.Evidence)
            {
                builder.Append("- Session ").Append(EscapeMarkdown(evidence.SessionId)).Append(", event ").Append(EscapeMarkdown(evidence.EventId)).Append(", ordinal ").Append(evidence.Ordinal.ToString(CultureInfo.InvariantCulture))
                    .Append(", ").Append(evidence.Timestamp.ToString("O", CultureInfo.InvariantCulture)).Append(" (").Append(evidence.Availability).Append(")\n  > ")
                    .Append(EscapeMarkdown(evidence.Quote).Replace("\n", "\n  > ", StringComparison.Ordinal)).Append('\n');
            }

            builder.Append('\n');
        }

        return new InsightExport("codex-approved-insights.md", "text/markdown", builder.ToString());
    }

    private static async Task<IReadOnlyList<InsightEvidence>> ValidateDraftAsync(InsightDraft draft, SqliteConnection source, SqliteTransaction transaction, IReadOnlyList<SessionRecord> sessions, CancellationToken cancellationToken, SavedPacket? packet = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.Statement);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.Rationale);
        if (draft.Statement.Length > 4000 || draft.Rationale.Length > 8000 || draft.Evidence is null || draft.Evidence.Count is < 1 or > 20)
        {
            throw new ArgumentException("Findings require a statement up to 4,000 characters, rationale up to 8,000 characters, and 1–20 citations.", nameof(draft));
        }

        ValidateChoice(draft.Category, ["preference", "lesson", "decision", "workflow"], nameof(draft));
        ValidateChoice(draft.Scope, ["global", "project", "session"], nameof(draft));
        ValidateChoice(draft.Basis, ["explicit", "inferred"], nameof(draft));
        if (draft.Scope == "global" && (draft.Project is not null || draft.SessionId is not null)
            || draft.Scope == "project" && (string.IsNullOrWhiteSpace(draft.Project) || draft.SessionId is not null)
            || draft.Scope == "session" && string.IsNullOrWhiteSpace(draft.SessionId))
        {
            throw new ArgumentException("Global scope has no project or session; project scope requires only project; session scope requires a session.", nameof(draft));
        }

        var result = new List<InsightEvidence>();
        var human = false;
        var packetEvidence = packet is null;
        var scopedEvidence = draft.Scope == "global";
        foreach (var citation in draft.Evidence)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(citation.Quote);
            if (citation.Quote.Length > 8000)
            {
                throw new ArgumentException("Evidence quotes must not exceed 8,000 characters.", nameof(draft));
            }

            var session = sessions.FirstOrDefault(item => item.Id == citation.SessionId) ?? throw new ArgumentException("A cited session is missing from the index.", nameof(draft));
            if (packet is not null && Family(session.Id, sessions) != Family(packet.Packet.Session!.Id, sessions))
            {
                throw new ArgumentException("Additional evidence must come from the reviewed session family.", nameof(draft));
            }

            var events = await KnowledgeSource.EventsAsync(source, transaction, citation.SessionId, cancellationToken).ConfigureAwait(false);
            var item = events.FirstOrDefault(item => item.Id == citation.EventId);
            if (item is null || !KnowledgeSource.Eligible(item, events, session) || !item.Text.Contains(citation.Quote, StringComparison.Ordinal))
            {
                throw new ArgumentException("Every citation must identify eligible original evidence and an exact verbatim quote.", nameof(draft));
            }

            human |= KnowledgeSource.Origin(item, session) == "user";
            packetEvidence |= packet is null || packet.Packet.Session!.Id == citation.SessionId && packet.Packet.Events.Any(part => part.EventId == citation.EventId && part.Text.Contains(citation.Quote, StringComparison.Ordinal));
            scopedEvidence |= draft.Scope == "project" && draft.Project == session.Project || draft.Scope == "session" && draft.SessionId == session.Id && (draft.Project is null || draft.Project == session.Project);
            result.Add(new InsightEvidence(session.Id, item.Id, item.Ordinal, item.Timestamp, citation.Quote, "available"));
        }

        if (!packetEvidence || !scopedEvidence)
        {
            throw new ArgumentException("Citations must support the selected scope and include exact evidence visible in this packet.", nameof(draft));
        }

        if (draft.Category == "preference" && draft.Basis == "explicit" && !human)
        {
            throw new ArgumentException("An explicit preference requires an original human statement, not assistant, tool, injected, or subagent instructions.", nameof(draft));
        }

        return [.. result.DistinctBy(item => (item.SessionId, item.EventId, item.Quote))];
    }

    private async Task<IReadOnlyList<Insight>> HydrateAsync(IReadOnlyList<Insight> insights, CancellationToken cancellationToken)
    {
        using var source = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = source.BeginTransaction(deferred: true);
        var cache = new Dictionary<string, List<LogEvent>>(StringComparer.Ordinal);
        var result = new List<Insight>();
        foreach (var insight in insights)
        {
            var evidence = new List<InsightEvidence>();
            foreach (var citation in insight.Evidence)
            {
                if (!cache.TryGetValue(citation.SessionId, out var events))
                {
                    events = await KnowledgeSource.EventsAsync(source, transaction, citation.SessionId, cancellationToken).ConfigureAwait(false);
                    cache.Add(citation.SessionId, events);
                }

                var item = events.FirstOrDefault(item => item.Id == citation.EventId);
                var availability = item is not null ? item.Text.Contains(citation.Quote, StringComparison.Ordinal) && !item.Inherited && !item.Encrypted ? "available" : "changed" : events.Any(item => item.Ordinal == citation.Ordinal) ? "changed" : "missing";
                evidence.Add(citation with { Availability = availability });
            }

            result.Add(insight with { Evidence = evidence });
        }

        return result;
    }

    private static string Family(string id, IReadOnlyList<SessionRecord> sessions)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (visited.Add(id))
        {
            var parent = sessions.FirstOrDefault(item => item.Id == id)?.ParentId;
            if (parent is null)
            {
                break;
            }

            id = parent;
        }

        return id;
    }

    private static void ValidateRelated(InsightDraft draft, IReadOnlyList<Insight> insights, string? self = null)
    {
        if (draft.RelatedInsightIds is not null && (draft.RelatedInsightIds.Count > 30 || draft.RelatedInsightIds.Any(id => id == self || !insights.Any(item => item.Id == id))))
        {
            throw new ArgumentException("Related insights must reference up to 30 existing, distinct insights other than this insight.", nameof(draft));
        }
    }

    private static bool SameContent(Insight insight, InsightDraft draft) => string.Equals(insight.Statement.Trim(), draft.Statement.Trim(), StringComparison.OrdinalIgnoreCase) && insight.Category == draft.Category && insight.Scope == draft.Scope && insight.Project == draft.Project && insight.SessionId == draft.SessionId && insight.Basis == draft.Basis;

    private static Insight Revise(Insight insight, string action, DateTimeOffset now, string? note) => insight with { Revision = insight.Revision + 1, UpdatedAt = now, History = [.. insight.History, new InsightRevision(insight.Revision + 1, action, now, insight.Statement, note)] };

    private static async Task SaveInsightAsync(SqliteConnection connection, SqliteTransaction transaction, Insight insight, CancellationToken cancellationToken)
    {
        await KnowledgeDatabase.SaveAsync(connection, transaction, "insights", insight.Id, insight, cancellationToken).ConfigureAwait(false);
        await KnowledgeDatabase.SaveAsync(connection, transaction, "revisions", $"{insight.Id}:{insight.Revision.ToString(CultureInfo.InvariantCulture)}", insight, cancellationToken).ConfigureAwait(false);
    }

    private static Insight RequireInsight(Dictionary<string, Insight> insights, string id) => insights.TryGetValue(id, out var insight) ? insight : throw new ArgumentException("The insight does not exist.", nameof(id));

    private static void RequireRevision(Insight insight, int revision)
    {
        if (insight.Revision != revision)
        {
            throw new KnowledgeConflictException("An insight changed since it was loaded. Refresh before reviewing it.");
        }
    }

    private static void ValidateChoice(string value, string[] choices, string parameter)
    {
        if (!choices.Contains(value, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Expected one of: {string.Join(", ", choices)}.", parameter);
        }
    }

    private static InsightCursor? DecodeInsightCursor(string? cursor, string scope)
    {
        if (cursor is null)
        {
            return null;
        }

        try
        {
            if (cursor.Length <= 2048)
            {
                var decoded = JsonStorage.Deserialize<InsightCursor>(Encoding.UTF8.GetString(Convert.FromBase64String(cursor)));
                if (decoded.Scope == scope && !string.IsNullOrWhiteSpace(decoded.Id))
                {
                    return decoded;
                }
            }
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidDataException)
        {
            throw new ArgumentException("Invalid insight cursor.", nameof(cursor), exception);
        }

        throw new ArgumentException("The insight cursor belongs to different filters.", nameof(cursor));
    }

    private static string EscapeMarkdown(string value) => value.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal);
}
