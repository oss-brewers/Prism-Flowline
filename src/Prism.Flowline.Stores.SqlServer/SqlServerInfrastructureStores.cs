using System.Text.Json;
using Prism.Flowline.Api;
using Prism.Flowline.Abstractions;
using Prism.Flowline.Core;
using Prism.Flowline.Persistence;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.EntityFrameworkCore;

namespace Prism.Flowline.Stores.SqlServer;

// Serialization envelope for the approval row; an implementation detail of the SQL store.
internal sealed record ApprovalDocument(ApprovalRequest Request, IReadOnlyList<ApprovalDecision> Decisions);

public sealed class SqlServerLogStore(IDbContextFactory<PrismDbContext> factory) : ILogStore
{
    public async ValueTask AppendAsync(InstanceLogEntry entry, CancellationToken cancellationToken)
    {
        await using PrismDbContext db = await factory.CreateDbContextAsync(cancellationToken);

        // The sequence is where the instance had got to, not an identity: the runner logs with the
        // one it has reached and the LLM executor with zero, so a key of (instance, sequence)
        // refused the second entry of nearly every run. The suffix makes each entry its own row.
        db.JsonRows.Add(Row("log", $"{entry.InstanceId}:{entry.Sequence}:{Guid.NewGuid():N}", entry));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlyList<InstanceLogEntry>> QueryAsync(string instanceId, string? level, string? executorId, int limit, CancellationToken cancellationToken)
    {
        await using PrismDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.JsonRows.AsNoTracking().Where(row => row.Kind == "log" && row.Key.StartsWith(instanceId + ":"))
            .Select(row => new { row.Payload, row.UpdatedAt }).ToListAsync(cancellationToken);

        // Oldest first, as the in-memory store reads, with the write time breaking a tie between
        // entries logged in the same instant so they keep the order they were written in.
        IEnumerable<InstanceLogEntry> query = rows
            .Select(row => (Entry: JsonSerializer.Deserialize<InstanceLogEntry>(row.Payload), row.UpdatedAt))
            .Where(item => item.Entry is not null)
            .OrderBy(item => item.Entry!.LoggedAt)
            .ThenBy(item => item.UpdatedAt)
            .Select(item => item.Entry!);
        if (!string.IsNullOrWhiteSpace(level)) query = query.Where(entry => entry.Level.Equals(level, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(executorId)) query = query.Where(entry => entry.ExecutorId == executorId);
        return query.Take(Math.Clamp(limit, 1, 1000)).ToArray();
    }

    private static JsonRow Row(string kind, string key, object value) => new()
    {
        Kind = kind, Key = key, Payload = JsonSerializer.Serialize(value), UpdatedAt = DateTimeOffset.UtcNow
    };
}

public sealed class SqlServerAuditStore(IDbContextFactory<PrismDbContext> factory) : IAuditStore
{
    public async ValueTask WriteAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        await using PrismDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        db.JsonRows.Add(new JsonRow
        {
            Kind = "audit", Key = Guid.NewGuid().ToString("N"), Payload = JsonSerializer.Serialize(entry), UpdatedAt = entry.OccurredAt
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlyList<AuditEntry>> QueryAsync(string? instanceId, int limit, CancellationToken cancellationToken)
    {
        await using PrismDbContext db = await factory.CreateDbContextAsync(cancellationToken);

        // The instance is filtered in the database, before the limit. Taking the newest entries
        // across every instance and filtering afterwards returned too few, or none, for an instance
        // that other instances had out-written.
        IQueryable<JsonRow> rows = instanceId is { Length: > 0 }
            ? db.JsonRows.FromSql(
                $"SELECT * FROM [PrismJsonRows] WHERE [Kind] = N'audit' AND JSON_VALUE([Payload], '$.InstanceId') = {instanceId}")
            : db.JsonRows.Where(row => row.Kind == "audit");

        List<string> payloads = await rows.AsNoTracking()
            .OrderByDescending(row => row.UpdatedAt).Take(Math.Clamp(limit, 1, 1000)).Select(row => row.Payload)
            .ToListAsync(cancellationToken);
        return payloads.Select(payload => JsonSerializer.Deserialize<AuditEntry>(payload)!).ToArray();
    }
}

public sealed class SqlServerBlobStore(IDbContextFactory<PrismDbContext> factory) : IBlobStore
{
    public async ValueTask<string> UploadAsync(string key, byte[] content, CancellationToken cancellationToken)
    {
        string uri = $"sqlblob://{key}";
        await using PrismDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        JsonRow? current = await db.JsonRows.SingleOrDefaultAsync(row => row.Kind == "blob" && row.Key == uri, cancellationToken);
        if (current is null)
        {
            db.JsonRows.Add(new JsonRow { Kind = "blob", Key = uri, Payload = Convert.ToBase64String(content), UpdatedAt = DateTimeOffset.UtcNow });
        }
        else
        {
            current.Payload = Convert.ToBase64String(content);
            current.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
        return uri;
    }

    public async ValueTask<byte[]> DownloadAsync(string uri, CancellationToken cancellationToken)
    {
        await using PrismDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        JsonRow row = await db.JsonRows.AsNoTracking().SingleOrDefaultAsync(item => item.Kind == "blob" && item.Key == uri, cancellationToken)
            ?? throw new KeyNotFoundException($"Blob '{uri}' not found.");
        return Convert.FromBase64String(row.Payload);
    }

    public async ValueTask DeleteAsync(string uri, CancellationToken cancellationToken)
    {
        await using PrismDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        JsonRow? row = await db.JsonRows.SingleOrDefaultAsync(item => item.Kind == "blob" && item.Key == uri, cancellationToken);
        if (row is not null) { db.JsonRows.Remove(row); await db.SaveChangesAsync(cancellationToken); }
    }
}

public sealed class SqlServerGatePolicyStore(IDbContextFactory<PrismDbContext> factory) : IGatePolicyStore
{
    private const string HostScope = "*";

    public async ValueTask<ApprovalGate?> FindAsync(string? tenantId, string workflowName, string workflowVersion, string executorId, string? instanceId, CancellationToken cancellationToken)
    {
        // Highest scope first, falling through to the next when nothing is stored at that level.
        var keys = new List<string>(3);
        if (instanceId is { Length: > 0 }) keys.Add($"gate-instance:{instanceId}:{executorId}");
        if (tenantId is { Length: > 0 }) keys.Add(PolicyKey(tenantId, workflowName, workflowVersion, executorId));
        keys.Add(PolicyKey(null, workflowName, workflowVersion, executorId));

        await using PrismDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        List<JsonRow> rows = await db.JsonRows.AsNoTracking()
            .Where(item => item.Kind == "gate" && keys.Contains(item.Key))
            .ToListAsync(cancellationToken);

        foreach (string key in keys)
        {
            JsonRow? row = rows.Find(item => item.Key == key);
            if (row is not null) return JsonSerializer.Deserialize<ApprovalGate>(row.Payload);
        }

        return null;
    }

    public async ValueTask<IReadOnlyDictionary<string, ApprovalGate>> ListAsync(string? tenantId, string workflowName, string workflowVersion, CancellationToken cancellationToken)
    {
        string prefix = PolicyKey(tenantId, workflowName, workflowVersion, string.Empty);

        await using PrismDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        List<JsonRow> rows = await db.JsonRows.AsNoTracking()
            .Where(item => item.Kind == "gate" && item.Key.StartsWith(prefix))
            .ToListAsync(cancellationToken);

        var gates = new Dictionary<string, ApprovalGate>(StringComparer.Ordinal);
        foreach (JsonRow row in rows)
        {
            ApprovalGate? gate = JsonSerializer.Deserialize<ApprovalGate>(row.Payload);
            if (gate is not null) gates[row.Key[prefix.Length..]] = gate;
        }

        return gates;
    }

    public ValueTask SetAsync(string? tenantId, string workflowName, string workflowVersion, string executorId, ApprovalGate gate, CancellationToken cancellationToken)
        => SetCoreAsync(PolicyKey(tenantId, workflowName, workflowVersion, executorId), gate, cancellationToken);

    public async ValueTask<bool> RemoveAsync(string? tenantId, string workflowName, string workflowVersion, string executorId, CancellationToken cancellationToken)
    {
        string key = PolicyKey(tenantId, workflowName, workflowVersion, executorId);
        await using PrismDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        JsonRow? row = await db.JsonRows.SingleOrDefaultAsync(item => item.Kind == "gate" && item.Key == key, cancellationToken);
        if (row is null) return false;
        db.JsonRows.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public ValueTask SetInstanceOverrideAsync(string instanceId, string executorId, ApprovalGate gate, CancellationToken cancellationToken)
        => SetCoreAsync($"gate-instance:{instanceId}:{executorId}", gate, cancellationToken);

    private static string PolicyKey(string? tenantId, string workflowName, string workflowVersion, string executorId)
        => $"gate:{(string.IsNullOrEmpty(tenantId) ? HostScope : tenantId)}:{workflowName}:{workflowVersion}:{executorId}";

    private async ValueTask SetCoreAsync(string key, ApprovalGate gate, CancellationToken cancellationToken)
    {
        await using PrismDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        JsonRow? row = await db.JsonRows.SingleOrDefaultAsync(item => item.Kind == "gate" && item.Key == key, cancellationToken);
        if (row is null) db.JsonRows.Add(new JsonRow { Kind = "gate", Key = key, Payload = JsonSerializer.Serialize(gate), UpdatedAt = DateTimeOffset.UtcNow });
        else { row.Payload = JsonSerializer.Serialize(gate); row.UpdatedAt = DateTimeOffset.UtcNow; }
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class SqlServerApprovalStore(IDbContextFactory<PrismDbContext> factory) : IApprovalStore
{
    public async ValueTask<ApprovalRequest> CreateAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        await using PrismDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        db.JsonRows.Add(Document(request, []));
        await db.SaveChangesAsync(cancellationToken);
        return request;
    }

    public async ValueTask<ApprovalRequest?> GetAsync(string approvalId, CancellationToken cancellationToken)
        => (await ReadAsync(approvalId, cancellationToken))?.Request;

    public async ValueTask<IReadOnlyList<ApprovalRequest>> ListForInstanceAsync(string instanceId, CancellationToken cancellationToken)
    {
        List<ApprovalDocument> docs = await Documents(cancellationToken);
        return docs.Where(doc => doc.Request.InstanceId == instanceId).Select(doc => doc.Request).OrderBy(doc => doc.CreatedAt).ToArray();
    }

    public async ValueTask<IReadOnlyList<ApprovalRequest>> QueryPendingAsync(string? tenantId, IReadOnlyList<string>? assignees, int limit, CancellationToken cancellationToken)
    {
        IEnumerable<ApprovalRequest> requests = (await Documents(cancellationToken)).Select(doc => doc.Request).Where(request => request.State == ApprovalState.Pending);
        if (!string.IsNullOrWhiteSpace(tenantId)) requests = requests.Where(request => request.TenantId == tenantId);
        if (assignees is { Count: > 0 }) requests = requests.Where(request => request.Assignees.Count == 0 || request.Assignees.Any(assignees.Contains));
        return requests.OrderBy(request => request.ExpiresAt).Take(Math.Clamp(limit, 1, 500)).ToArray();
    }

    public async ValueTask<IReadOnlyList<ApprovalRequest>> ClaimExpiredAsync(DateTimeOffset now, int max, CancellationToken cancellationToken)
        => (await Documents(cancellationToken)).Select(doc => doc.Request).Where(request => request.State == ApprovalState.Pending && request.ExpiresAt <= now)
            .OrderBy(request => request.ExpiresAt).Take(max).ToArray();

    public ValueTask<bool> TryRecordDecisionAsync(string approvalId, ApprovalDecision decision, ApprovalState? newState, CancellationToken cancellationToken)
        => TryChangeAsync(approvalId, document =>
        {
            // One vote per decider, compared as the in-memory store compares them.
            if (document.Request.State != ApprovalState.Pending ||
                document.Decisions.Any(item => string.Equals(item.DeciderId, decision.DeciderId, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            return document with
            {
                Request = newState is null ? document.Request : document.Request with { State = newState.Value },
                Decisions = [.. document.Decisions, decision]
            };
        }, cancellationToken);

    public async ValueTask<IReadOnlyList<ApprovalDecision>> GetDecisionsAsync(string approvalId, CancellationToken cancellationToken)
        => (await ReadAsync(approvalId, cancellationToken))?.Decisions ?? [];

    public ValueTask<bool> TrySetStateAsync(string approvalId, ApprovalState state, CancellationToken cancellationToken)
        => TryChangeAsync(approvalId, document => document with
        {
            Request = document.Request with
            {
                State = state,
                EscalatedOnce = document.Request.EscalatedOnce || state == ApprovalState.Pending
            }
        }, cancellationToken);

    public async ValueTask CancelForInstanceAsync(string instanceId, CancellationToken cancellationToken)
    {
        // Pending ones only, checked inside the same conditional write: an approval decided while
        // the instance was being cancelled keeps the decision it was given.
        foreach (ApprovalRequest request in await ListForInstanceAsync(instanceId, cancellationToken))
        {
            await TryChangeAsync(request.ApprovalId, document => document.Request.State == ApprovalState.Pending
                ? document with { Request = document.Request with { State = ApprovalState.Cancelled } }
                : null, cancellationToken);
        }
    }

    /// <summary>
    /// Read, change and write one approval under its rowversion, re-reading when another writer got
    /// there first. <paramref name="change"/> returns null to decline the write.
    /// </summary>
    /// <remarks>
    /// Votes are kept inside the approval's document, so every vote is a read-modify-write of one
    /// row. Without the retry, two approvers voting together had one of them thrown at — or, on a
    /// quorum, told their vote was refused when it was only unlucky. Each round lets at least one
    /// writer through, so the bound is only a backstop against a store that never settles.
    /// </remarks>
    private async ValueTask<bool> TryChangeAsync(
        string approvalId, Func<ApprovalDocument, ApprovalDocument?> change, CancellationToken cancellationToken)
    {
        const int attempts = 64;
        for (int attempt = 1; ; attempt++)
        {
            await using PrismDbContext db = await factory.CreateDbContextAsync(cancellationToken);
            JsonRow? row = await db.JsonRows.SingleOrDefaultAsync(item => item.Kind == "approval" && item.Key == approvalId, cancellationToken);
            if (row is null) return false;

            ApprovalDocument? changed = change(JsonSerializer.Deserialize<ApprovalDocument>(row.Payload)!);
            if (changed is null) return false;

            row.Payload = JsonSerializer.Serialize(changed);
            row.UpdatedAt = DateTimeOffset.UtcNow;

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateConcurrencyException) when (attempt < attempts)
            {
                // Another writer changed the approval between the read and the write; read it again.
            }
        }
    }

    private async Task<ApprovalDocument?> ReadAsync(string id, CancellationToken cancellationToken)
    {
        await using PrismDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        JsonRow? row = await db.JsonRows.AsNoTracking().SingleOrDefaultAsync(item => item.Kind == "approval" && item.Key == id, cancellationToken);
        return row is null ? null : JsonSerializer.Deserialize<ApprovalDocument>(row.Payload);
    }

    private async Task<List<ApprovalDocument>> Documents(CancellationToken cancellationToken)
    {
        await using PrismDbContext db = await factory.CreateDbContextAsync(cancellationToken);
        return (await db.JsonRows.AsNoTracking().Where(item => item.Kind == "approval").Select(item => item.Payload).ToListAsync(cancellationToken))
            .Select(payload => JsonSerializer.Deserialize<ApprovalDocument>(payload)!).ToList();
    }

    private static JsonRow Document(ApprovalRequest request, IReadOnlyList<ApprovalDecision> decisions) => new()
    {
        Kind = "approval", Key = request.ApprovalId, Payload = JsonSerializer.Serialize(new ApprovalDocument(request, decisions)), UpdatedAt = request.CreatedAt
    };
}

/// <remarks>
/// <para>
/// The index is oldest first, ordered by commit time and then by key. Ids are monotonic, so the key
/// keeps the order total when two checkpoints share a commit time; without it the framework's
/// resume point — the last element — was whichever the database happened to return last.
/// </para>
/// <para>
/// A checkpoint's parent is a row of its own (<c>checkpoint-parent</c>, same key, the parent's id as
/// payload) written in the same save, so parent filtering needs no new column and checkpoints
/// written before it existed still read.
/// </para>
/// </remarks>
public sealed class SqlServerCheckpointStore(IDbContextFactory<PrismDbContext> factory) : ICheckpointStore<JsonElement>
{
    internal const string Kind = "checkpoint";
    private const string ParentKind = "checkpoint-parent";

    public async ValueTask<CheckpointInfo> CreateCheckpointAsync(string sessionId, JsonElement value, CheckpointInfo? parent = null)
    {
        string id = IdGenerator.NewId();
        string key = $"{sessionId}:{id}";
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await using PrismDbContext db = await factory.CreateDbContextAsync();
        db.JsonRows.Add(new JsonRow { Kind = Kind, Key = key, Payload = value.GetRawText(), UpdatedAt = now });
        if (parent is not null)
        {
            db.JsonRows.Add(new JsonRow { Kind = ParentKind, Key = key, Payload = parent.CheckpointId, UpdatedAt = now });
        }

        await db.SaveChangesAsync();
        return new CheckpointInfo(sessionId, id);
    }

    public async ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(string sessionId, CheckpointInfo? withParent = null)
    {
        string prefix = sessionId + ":";
        await using PrismDbContext db = await factory.CreateDbContextAsync();

        IQueryable<JsonRow> rows = db.JsonRows.AsNoTracking().Where(row => row.Kind == Kind && row.Key.StartsWith(prefix));
        if (withParent is not null)
        {
            string parentId = withParent.CheckpointId;
            IQueryable<string> children = db.JsonRows
                .Where(row => row.Kind == ParentKind && row.Key.StartsWith(prefix) && row.Payload == parentId)
                .Select(row => row.Key);
            rows = rows.Where(row => children.Contains(row.Key));
        }

        List<string> keys = await rows.OrderBy(row => row.UpdatedAt).ThenBy(row => row.Key).Select(row => row.Key).ToListAsync();
        return keys.Select(key => new CheckpointInfo(sessionId, key[prefix.Length..])).ToArray();
    }

    public async ValueTask<JsonElement> RetrieveCheckpointAsync(string sessionId, CheckpointInfo key)
    {
        ArgumentNullException.ThrowIfNull(key);

        await using PrismDbContext db = await factory.CreateDbContextAsync();
        JsonRow row = await db.JsonRows.AsNoTracking().SingleOrDefaultAsync(item => item.Kind == Kind && item.Key == $"{sessionId}:{key.CheckpointId}")
            ?? throw new KeyNotFoundException($"Checkpoint '{key.CheckpointId}' not found for session '{sessionId}'.");
        return JsonSerializer.Deserialize<JsonElement>(row.Payload);
    }
}

public sealed class SqlServerCheckpointDescriber(IDbContextFactory<PrismDbContext> factory) : ICheckpointDescriber
{
    public bool Exists(string sessionId, string checkpointId)
    {
        using PrismDbContext db = factory.CreateDbContext();
        return db.JsonRows.Any(row => row.Kind == SqlServerCheckpointStore.Kind && row.Key == $"{sessionId}:{checkpointId}");
    }

    public IReadOnlyList<(string CheckpointId, int SizeBytes, DateTimeOffset CommittedAt)> Describe(string sessionId)
    {
        using PrismDbContext db = factory.CreateDbContext();
        return db.JsonRows.AsNoTracking()
            .Where(row => row.Kind == SqlServerCheckpointStore.Kind && row.Key.StartsWith(sessionId + ":"))
            .OrderBy(row => row.UpdatedAt)
            .ThenBy(row => row.Key)
            .AsEnumerable()
            .Select(row => (row.Key[(sessionId.Length + 1)..], row.Payload.Length, row.UpdatedAt))
            .ToArray();
    }
}
