// <copyright file="AutomationModels.cs" company="slskdN Team">
//     Copyright (c) slskdN Team. All rights reserved.
// </copyright>
namespace slskd.Automations;

using System;
using System.Collections.Generic;
using System.Text.Json;

/// <summary>
///     Create/update payload for an automation.
/// </summary>
public class AutomationRequest
{
    public string Name { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public Dictionary<string, JsonElement> Parameters { get; set; } = new();

    /// <summary>0 = manual only.</summary>
    public int IntervalMinutes { get; set; }

    public bool Enabled { get; set; }
}

/// <summary>
///     A persisted automation.
/// </summary>
public class Automation : AutomationRequest
{
    public Guid Id { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? LastRunAt { get; set; }

    public string? LastRunStatus { get; set; }

    public string? LastRunSummary { get; set; }

    public DateTimeOffset? NextRunAt { get; set; }
}

/// <summary>
///     Result of one (dry) run of an automation.
/// </summary>
public class AutomationRun
{
    public Guid Id { get; set; }

    public Guid AutomationId { get; set; }

    public string AutomationName { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public bool DryRun { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset FinishedAt { get; set; }

    public string Status { get; set; } = AutomationRunStatus.Succeeded;

    public string Summary { get; set; } = string.Empty;

    public JsonElement Details { get; set; }
}

public static class AutomationRunStatus
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
}

/// <summary>
///     Outcome of an action; details are serialized to JSON when the run is stored.
/// </summary>
public sealed record AutomationActionResult(string Status, string Summary, object Details);

// Min/Max: inclusive range of an "int" parameter, enforced on create and update.
public sealed record AutomationParameterSchema(string Key, string Label, string Type, bool Required, object? Default, string Description, int? Min = null, int? Max = null);

// Default: the value the option has when it is absent from slskd.yml (serialized as "default"); Min: the lowest value slskd.yml accepts.
public sealed record AutomationLinkedOption(string YamlPath, string Label, string Type, object? Default, string Description, int? Min = null);

public sealed record AutomationTemplate(
    string Type,
    string Title,
    string Description,
    string Icon,
    int DefaultIntervalMinutes,
    string NetworkImpact,
    string FileImpact,
    string ApprovalGate,
    IReadOnlyList<AutomationParameterSchema> ParameterSchema,
    IReadOnlyList<AutomationLinkedOption> LinkedOptions);

public static class AutomationTemplates
{
    public const string WishlistRetry = "wishlist-retry";
    public const string LibraryHealthScan = "library-health-scan";
    public const string LocalDiagnostics = "local-diagnostics";
    public const string StaleCacheReminders = "stale-cache-reminders";

    public static readonly IReadOnlyList<AutomationTemplate> All = new[]
    {
        new AutomationTemplate(
            WishlistRetry,
            "Wishlist Retry",
            "Re-runs the wishlist searches that have waited longest.",
            "redo",
            120,
            "Public peers possible",
            "Items with auto-download on download immediately",
            "No approval step",
            new[]
            {
                new AutomationParameterSchema("limit", "Searches per run", "int", false, 3, "Maximum number of wishlist searches started per run.", 1, 50),
                new AutomationParameterSchema("includeDisabled", "Include disabled items", "bool", false, false, "Also search wishlist items that are disabled."),
            },
            new[]
            {
                // Defaults and the minimum mirror Options.WishlistOptions. wishlist.auto_download is not linked: nothing reads it,
                // each item's own auto-download flag decides whether a search downloads.
                new AutomationLinkedOption("wishlist.enabled", "Wishlist enabled", "bool", true, "Runs the background wishlist cycle, which searches every enabled item in turn. Wishlist Retry runs whether this is on or off."),
                new AutomationLinkedOption("wishlist.interval_seconds", "Wishlist interval (seconds)", "int", 3600, "Seconds the background wishlist cycle waits between passes; once logged in, the cycle uses the interval the Soulseek server sends instead. Wishlist Retry skips items searched within this many seconds.", 300),
            }),
        new AutomationTemplate(
            LibraryHealthScan,
            "Library Health Scan",
            "Starts a Library Health scan of a library folder.",
            "heartbeat",
            1440,
            "Local",
            "Read only until fixed",
            "Fix confirmation",
            new[]
            {
                new AutomationParameterSchema("libraryPath", "Library path", "path", true, null, "Folder to scan."),
            },
            Array.Empty<AutomationLinkedOption>()),
        new AutomationTemplate(
            LocalDiagnostics,
            "Local Diagnostics",
            "Reports Soulseek connection state, share cache status and downloads folder health.",
            "stethoscope",
            5,
            "Local",
            "Writes a probe file in the downloads folder (real runs only)",
            "None required",
            Array.Empty<AutomationParameterSchema>(),
            Array.Empty<AutomationLinkedOption>()),
        new AutomationTemplate(
            StaleCacheReminders,
            "Share Cache Reminder",
            "Rescans shares when the share cache is older than the configured age.",
            "bell outline",
            1440,
            "Local",
            "Read only",
            "None required",
            new[]
            {
                new AutomationParameterSchema("maxShareCacheAgeHours", "Max share cache age (hours)", "int", false, 24, "Rescan when the last share scan is older than this.", 1, 8760),
            },
            Array.Empty<AutomationLinkedOption>()),
    };

    public static AutomationTemplate? Find(string? type)
    {
        foreach (var template in All)
        {
            if (string.Equals(template.Type, type, StringComparison.Ordinal))
            {
                return template;
            }
        }

        return null;
    }
}
