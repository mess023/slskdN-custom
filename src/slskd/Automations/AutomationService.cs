// <copyright file="AutomationService.cs" company="slskdN Team">
//     Copyright (c) slskdN Team. All rights reserved.
// </copyright>
namespace slskd.Automations;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

public interface IAutomationService
{
    IReadOnlyList<AutomationTemplate> GetTemplates();

    Task<IReadOnlyList<Automation>> ListAsync();

    Task<Automation?> GetAsync(Guid id);

    /// <exception cref="ArgumentException">Thrown when the request is invalid.</exception>
    Task<Automation> CreateAsync(AutomationRequest request);

    /// <exception cref="ArgumentException">Thrown when the request is invalid.</exception>
    Task<Automation?> UpdateAsync(Guid id, AutomationRequest request);

    Task<bool> DeleteAsync(Guid id);

    /// <summary>Runs (or dry-runs) one automation now and records the run.</summary>
    /// <returns>The run, or null when the automation does not exist (also when it was deleted while the run was in progress).</returns>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="ct"/> is cancelled; nothing is recorded.</exception>
    Task<AutomationRun?> RunAsync(Guid id, bool dryRun, CancellationToken ct = default);

    /// <summary>Lists recorded runs.</summary>
    /// <param name="automationId">Only runs of this automation; null for all.</param>
    /// <param name="limit">Maximum number of runs, clamped to 1-1000.</param>
    /// <param name="since">Only runs started at or after this instant; null for no lower bound.</param>
    /// <returns>Runs, newest first.</returns>
    Task<IReadOnlyList<AutomationRun>> ListRunsAsync(Guid? automationId, int limit, DateTimeOffset? since = null);

    /// <summary>Runs every enabled automation whose next run is at or before <paramref name="now"/>.</summary>
    Task<IReadOnlyList<AutomationRun>> RunDueAsync(DateTimeOffset now, CancellationToken ct = default);
}

/// <summary>
///     CRUD, validation and execution of automations, persisted in a small SQLite file.
/// </summary>
public class AutomationService : IAutomationService
{
    internal const int MaxRunsPerAutomation = 200;
    internal const int MaxRunsTotal = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string connectionString;
    private readonly AutomationActions actions;
    private readonly ILogger<AutomationService> logger;

    // Real runs of one automation never overlap; different automations and dry runs may.
    // ponytail: one semaphore per automation id, never removed; fine for a handful of automations.
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> runLocks = new();

    public AutomationService(string connectionString, AutomationActions actions, ILogger<AutomationService> logger)
    {
        this.connectionString = connectionString;
        this.actions = actions;
        this.logger = logger;
        InitializeSchema();
    }

    public IReadOnlyList<AutomationTemplate> GetTemplates() => AutomationTemplates.All;

    public async Task<IReadOnlyList<Automation>> ListAsync()
    {
        using var conn = await OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM automations ORDER BY createdAt";
        return await ReadAllAsync(cmd, ReadAutomation);
    }

    public async Task<Automation?> GetAsync(Guid id)
    {
        using var conn = await OpenAsync();
        return await GetAsync(conn, id);
    }

    public async Task<Automation> CreateAsync(AutomationRequest request)
    {
        Validate(request);
        var now = DateTimeOffset.UtcNow;
        var automation = new Automation { Id = Guid.NewGuid(), CreatedAt = now, UpdatedAt = now };
        Apply(automation, request, now);

        using var conn = await OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO automations (id, name, type, parameters, intervalMinutes, enabled, createdAt, updatedAt, lastRunAt, lastRunStatus, lastRunSummary, nextRunAt)
                            VALUES (@id, @name, @type, @parameters, @intervalMinutes, @enabled, @createdAt, @updatedAt, @lastRunAt, @lastRunStatus, @lastRunSummary, @nextRunAt)";
        BindAutomation(cmd, automation);
        await cmd.ExecuteNonQueryAsync();
        return automation;
    }

    public async Task<Automation?> UpdateAsync(Guid id, AutomationRequest request)
    {
        Validate(request);
        using var conn = await OpenAsync();
        var automation = await GetAsync(conn, id);
        if (automation == null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        automation.UpdatedAt = now;
        Apply(automation, request, now);
        await SaveAsync(conn, automation);
        return automation;
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        // Automation first: a run finishing in between records nothing (its insert requires the automation),
        // and a run recorded just before is removed with the others.
        using var conn = await OpenAsync();
        using var deleteAutomation = conn.CreateCommand();
        deleteAutomation.CommandText = "DELETE FROM automations WHERE id = @id";
        deleteAutomation.Parameters.AddWithValue("@id", id.ToString());
        var deleted = await deleteAutomation.ExecuteNonQueryAsync() > 0;

        using var deleteRuns = conn.CreateCommand();
        deleteRuns.CommandText = "DELETE FROM automation_runs WHERE automationId = @id";
        deleteRuns.Parameters.AddWithValue("@id", id.ToString());
        await deleteRuns.ExecuteNonQueryAsync();
        return deleted;
    }

    public async Task<AutomationRun?> RunAsync(Guid id, bool dryRun, CancellationToken ct = default)
    {
        var automation = await GetAsync(id);
        return automation == null ? null : await ExecuteAsync(automation, dryRun, ct, dueAt: null);
    }

    public async Task<IReadOnlyList<AutomationRun>> RunDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var due = (await ListAsync()).Where(a => IsDue(a, now)).ToList();
        var runs = new List<AutomationRun>();
        foreach (var automation in due)
        {
            ct.ThrowIfCancellationRequested();
            var run = await ExecuteAsync(automation, dryRun: false, ct, dueAt: now);
            if (run != null)
            {
                runs.Add(run);
            }
        }

        return runs;
    }

    private static bool IsDue(Automation a, DateTimeOffset now) => a.Enabled && a.NextRunAt.HasValue && a.NextRunAt.Value <= now;

    public async Task<IReadOnlyList<AutomationRun>> ListRunsAsync(Guid? automationId, int limit, DateTimeOffset? since = null)
    {
        using var conn = await OpenAsync();
        using var cmd = conn.CreateCommand();

        // startedAt is stored as round-trip UTC text ("O"), so text order is time order.
        cmd.CommandText = @"SELECT * FROM automation_runs
                            WHERE (@automationId IS NULL OR automationId = @automationId) AND (@since IS NULL OR startedAt >= @since)
                            ORDER BY startedAt DESC LIMIT @limit";
        cmd.Parameters.AddWithValue("@automationId", (object?)automationId?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@since", (object?)Format(since) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, MaxRunsTotal));
        return await ReadAllAsync(cmd, ReadRun);
    }

    internal static DateTimeOffset? ComputeNextRunAt(bool enabled, int intervalMinutes, DateTimeOffset? lastRunAt, DateTimeOffset updatedAt)
        => enabled && intervalMinutes > 0 ? (lastRunAt ?? updatedAt).AddMinutes(intervalMinutes) : null;

    internal static void Validate(AutomationRequest request)
    {
        if (request == null)
        {
            throw new ArgumentException("Request body is required");
        }

        request.Name = request.Name?.Trim() ?? string.Empty;
        if (request.Name.Length is < 1 or > 100)
        {
            throw new ArgumentException("name must be 1-100 characters");
        }

        var template = AutomationTemplates.Find(request.Type)
            ?? throw new ArgumentException($"type must be one of: {string.Join(", ", AutomationTemplates.All.Select(t => t.Type))}");

        if (request.IntervalMinutes < 0)
        {
            throw new ArgumentException("intervalMinutes must be 0 (manual) or >= 1");
        }

        request.Parameters ??= new();
        foreach (var key in request.Parameters.Keys)
        {
            if (template.ParameterSchema.All(p => p.Key != key))
            {
                throw new ArgumentException($"parameters.{key} is not a parameter of {template.Type}");
            }
        }

        foreach (var schema in template.ParameterSchema)
        {
            if (!request.Parameters.TryGetValue(schema.Key, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                if (schema.Required)
                {
                    throw new ArgumentException($"parameters.{schema.Key} is required");
                }

                request.Parameters.Remove(schema.Key);
                continue;
            }

            var ok = schema.Type switch
            {
                "int" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
                "bool" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                _ => value.ValueKind == JsonValueKind.String && (!schema.Required || !string.IsNullOrWhiteSpace(value.GetString())),
            };

            if (!ok)
            {
                throw new ArgumentException($"parameters.{schema.Key} must be a {(schema.Type == "path" ? "non-empty path string" : schema.Type)}");
            }

            if (schema.Type == "int" && (value.GetInt32() < schema.Min || value.GetInt32() > schema.Max))
            {
                throw new ArgumentException($"parameters.{schema.Key} must be between {schema.Min} and {schema.Max}");
            }
        }
    }

    private static void Apply(Automation automation, AutomationRequest request, DateTimeOffset now)
    {
        automation.Name = request.Name;
        automation.Type = request.Type;
        automation.Parameters = request.Parameters;
        automation.IntervalMinutes = request.IntervalMinutes;
        automation.Enabled = request.Enabled;
        automation.NextRunAt = ComputeNextRunAt(request.Enabled, request.IntervalMinutes, automation.LastRunAt, now);
    }

    // The automation is re-read (for real runs inside its lock) and skipped (null) when it was deleted meanwhile; for a scheduled run (dueAt set) also when it was disabled or already ran since the snapshot.
    private async Task<AutomationRun?> ExecuteAsync(Automation automation, bool dryRun, CancellationToken ct, DateTimeOffset? dueAt)
    {
        // A dry run has no side effects, so it never waits for a real run.
        var runLock = dryRun ? null : runLocks.GetOrAdd(automation.Id, _ => new SemaphoreSlim(1, 1));
        if (runLock != null)
        {
            await runLock.WaitAsync(ct);
        }

        try
        {
            // Re-read inside the lock: a run that waited for the lock must not run an automation deleted meanwhile.
            var fresh = await GetAsync(automation.Id);
            if (fresh == null || (dueAt.HasValue && !IsDue(fresh, dueAt.Value)))
            {
                return null;
            }

            automation = fresh;

            var run = new AutomationRun
            {
                Id = Guid.NewGuid(),
                AutomationId = automation.Id,
                AutomationName = automation.Name,
                Type = automation.Type,
                DryRun = dryRun,
                StartedAt = DateTimeOffset.UtcNow,
            };

            AutomationActionResult result;
            try
            {
                result = await actions.ExecuteAsync(automation, dryRun, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Cancellation of ct (shutdown, aborted request) propagates instead: nothing is recorded and lastRunAt/nextRunAt stay as they were.
                logger.LogWarning(ex, "Automation {Name} ({Type}) failed", automation.Name, automation.Type);
                result = new(AutomationRunStatus.Failed, ex.Message, new { error = ex.GetType().Name });
            }

            run.FinishedAt = DateTimeOffset.UtcNow;
            run.Status = result.Status;
            run.Summary = result.Summary;
            run.Details = JsonSerializer.SerializeToElement(result.Details, JsonOptions);

            // One statement: the run is recorded only if the automation was not deleted while the action ran.
            using var conn = await OpenAsync();
            using var insert = conn.CreateCommand();
            insert.CommandText = @"INSERT INTO automation_runs (id, automationId, automationName, type, dryRun, startedAt, finishedAt, status, summary, details)
                                   SELECT @id, @automationId, @automationName, @type, @dryRun, @startedAt, @finishedAt, @status, @summary, @details
                                   WHERE EXISTS (SELECT 1 FROM automations WHERE id = @automationId)";
            insert.Parameters.AddWithValue("@id", run.Id.ToString());
            insert.Parameters.AddWithValue("@automationId", run.AutomationId.ToString());
            insert.Parameters.AddWithValue("@automationName", run.AutomationName);
            insert.Parameters.AddWithValue("@type", run.Type);
            insert.Parameters.AddWithValue("@dryRun", run.DryRun ? 1 : 0);
            insert.Parameters.AddWithValue("@startedAt", Format(run.StartedAt));
            insert.Parameters.AddWithValue("@finishedAt", Format(run.FinishedAt));
            insert.Parameters.AddWithValue("@status", run.Status);
            insert.Parameters.AddWithValue("@summary", run.Summary);
            insert.Parameters.AddWithValue("@details", run.Details.GetRawText());
            if (await insert.ExecuteNonQueryAsync(CancellationToken.None) == 0)
            {
                return null; // deleted during the run
            }

            using var trim = conn.CreateCommand();
            trim.CommandText = @"DELETE FROM automation_runs WHERE automationId = @automationId AND id NOT IN
                                     (SELECT id FROM automation_runs WHERE automationId = @automationId ORDER BY startedAt DESC LIMIT @perAutomation);
                                 DELETE FROM automation_runs WHERE id NOT IN
                                     (SELECT id FROM automation_runs ORDER BY startedAt DESC LIMIT @total);";
            trim.Parameters.AddWithValue("@automationId", run.AutomationId.ToString());
            trim.Parameters.AddWithValue("@perAutomation", MaxRunsPerAutomation);
            trim.Parameters.AddWithValue("@total", MaxRunsTotal);
            await trim.ExecuteNonQueryAsync(CancellationToken.None);

            if (!dryRun)
            {
                var current = await GetAsync(conn, automation.Id);
                if (current != null)
                {
                    current.LastRunAt = run.FinishedAt;
                    current.LastRunStatus = run.Status;
                    current.LastRunSummary = run.Summary;
                    current.NextRunAt = ComputeNextRunAt(current.Enabled, current.IntervalMinutes, current.LastRunAt, current.UpdatedAt);
                    await SaveAsync(conn, current);
                }
            }

            return run;
        }
        finally
        {
            runLock?.Release();
        }
    }

    private void InitializeSchema()
    {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS automations (
                id TEXT PRIMARY KEY, name TEXT NOT NULL, type TEXT NOT NULL, parameters TEXT NOT NULL,
                intervalMinutes INTEGER NOT NULL, enabled INTEGER NOT NULL, createdAt TEXT NOT NULL, updatedAt TEXT NOT NULL,
                lastRunAt TEXT NULL, lastRunStatus TEXT NULL, lastRunSummary TEXT NULL, nextRunAt TEXT NULL);
            CREATE TABLE IF NOT EXISTS automation_runs (
                id TEXT PRIMARY KEY, automationId TEXT NOT NULL, automationName TEXT NOT NULL, type TEXT NOT NULL,
                dryRun INTEGER NOT NULL, startedAt TEXT NOT NULL, finishedAt TEXT NOT NULL,
                status TEXT NOT NULL, summary TEXT NOT NULL, details TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS idx_automation_runs_automation ON automation_runs (automationId, startedAt);";
        cmd.ExecuteNonQuery();
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task<Automation?> GetAsync(SqliteConnection conn, Guid id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM automations WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id.ToString());
        return (await ReadAllAsync(cmd, ReadAutomation)).FirstOrDefault();
    }

    private static async Task SaveAsync(SqliteConnection conn, Automation automation)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE automations SET name = @name, type = @type, parameters = @parameters, intervalMinutes = @intervalMinutes, enabled = @enabled,
                            updatedAt = @updatedAt, lastRunAt = @lastRunAt, lastRunStatus = @lastRunStatus, lastRunSummary = @lastRunSummary, nextRunAt = @nextRunAt
                            WHERE id = @id";
        BindAutomation(cmd, automation);
        await cmd.ExecuteNonQueryAsync();
    }

    private static void BindAutomation(SqliteCommand cmd, Automation a)
    {
        cmd.Parameters.AddWithValue("@id", a.Id.ToString());
        cmd.Parameters.AddWithValue("@name", a.Name);
        cmd.Parameters.AddWithValue("@type", a.Type);
        cmd.Parameters.AddWithValue("@parameters", JsonSerializer.Serialize(a.Parameters, JsonOptions));
        cmd.Parameters.AddWithValue("@intervalMinutes", a.IntervalMinutes);
        cmd.Parameters.AddWithValue("@enabled", a.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@createdAt", Format(a.CreatedAt));
        cmd.Parameters.AddWithValue("@updatedAt", Format(a.UpdatedAt));
        cmd.Parameters.AddWithValue("@lastRunAt", (object?)Format(a.LastRunAt) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lastRunStatus", (object?)a.LastRunStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lastRunSummary", (object?)a.LastRunSummary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@nextRunAt", (object?)Format(a.NextRunAt) ?? DBNull.Value);
    }

    private static async Task<IReadOnlyList<T>> ReadAllAsync<T>(SqliteCommand cmd, Func<SqliteDataReader, T> map)
    {
        var list = new List<T>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(map(reader));
        }

        return list;
    }

    private static Automation ReadAutomation(SqliteDataReader r) => new()
    {
        Id = Guid.Parse(r.GetString(r.GetOrdinal("id"))),
        Name = r.GetString(r.GetOrdinal("name")),
        Type = r.GetString(r.GetOrdinal("type")),
        Parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(r.GetString(r.GetOrdinal("parameters")), JsonOptions) ?? new(),
        IntervalMinutes = r.GetInt32(r.GetOrdinal("intervalMinutes")),
        Enabled = r.GetInt32(r.GetOrdinal("enabled")) != 0,
        CreatedAt = Parse(r.GetString(r.GetOrdinal("createdAt"))),
        UpdatedAt = Parse(r.GetString(r.GetOrdinal("updatedAt"))),
        LastRunAt = ParseNullable(r, "lastRunAt"),
        LastRunStatus = r.IsDBNull(r.GetOrdinal("lastRunStatus")) ? null : r.GetString(r.GetOrdinal("lastRunStatus")),
        LastRunSummary = r.IsDBNull(r.GetOrdinal("lastRunSummary")) ? null : r.GetString(r.GetOrdinal("lastRunSummary")),
        NextRunAt = ParseNullable(r, "nextRunAt"),
    };

    private static AutomationRun ReadRun(SqliteDataReader r) => new()
    {
        Id = Guid.Parse(r.GetString(r.GetOrdinal("id"))),
        AutomationId = Guid.Parse(r.GetString(r.GetOrdinal("automationId"))),
        AutomationName = r.GetString(r.GetOrdinal("automationName")),
        Type = r.GetString(r.GetOrdinal("type")),
        DryRun = r.GetInt32(r.GetOrdinal("dryRun")) != 0,
        StartedAt = Parse(r.GetString(r.GetOrdinal("startedAt"))),
        FinishedAt = Parse(r.GetString(r.GetOrdinal("finishedAt"))),
        Status = r.GetString(r.GetOrdinal("status")),
        Summary = r.GetString(r.GetOrdinal("summary")),
        Details = JsonSerializer.Deserialize<JsonElement>(r.GetString(r.GetOrdinal("details"))),
    };

    private static string? Format(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? ParseNullable(SqliteDataReader r, string column)
    {
        var ordinal = r.GetOrdinal(column);
        return r.IsDBNull(ordinal) ? null : Parse(r.GetString(ordinal));
    }
}
