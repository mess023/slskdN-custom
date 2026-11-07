// <copyright file="AutomationActions.cs" company="slskdN Team">
//     Copyright (c) slskdN Team. All rights reserved.
// </copyright>
namespace slskd.Automations;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using slskd.Common.Security;
using slskd.LibraryHealth;
using slskd.Shares;
using slskd.Wishlist;

/// <summary>
///     The real work behind each automation template. One method per type; every method
///     supports a side-effect-free dry run.
/// </summary>
public class AutomationActions
{
    private const int MaxCountedFiles = 5000;
    private static readonly string[] AudioExtensions = { ".flac", ".mp3", ".m4a", ".ogg" };

    private readonly IWishlistService wishlist;
    private readonly ILibraryHealthService libraryHealth;
    private readonly IShareService shares;
    private readonly IStateMonitor<State> state;
    private readonly IOptionsMonitor<slskd.Options> options;
    private readonly ILogger<AutomationActions> logger;

    public AutomationActions(
        IWishlistService wishlist,
        ILibraryHealthService libraryHealth,
        IShareService shares,
        IStateMonitor<State> state,
        IOptionsMonitor<slskd.Options> options,
        ILogger<AutomationActions> logger)
    {
        this.wishlist = wishlist;
        this.libraryHealth = libraryHealth;
        this.shares = shares;
        this.state = state;
        this.options = options;
        this.logger = logger;
    }

    public Task<AutomationActionResult> ExecuteAsync(Automation automation, bool dryRun, CancellationToken ct) => automation.Type switch
    {
        AutomationTemplates.WishlistRetry => WishlistRetryAsync(automation.Parameters, dryRun, ct),
        AutomationTemplates.LibraryHealthScan => LibraryHealthScanAsync(automation.Parameters, dryRun, ct),
        AutomationTemplates.LocalDiagnostics => LocalDiagnosticsAsync(dryRun),
        AutomationTemplates.StaleCacheReminders => StaleCacheRemindersAsync(automation.Parameters, dryRun),
        _ => throw new ArgumentException($"Unknown automation type '{automation.Type}'"),
    };

    private async Task<AutomationActionResult> WishlistRetryAsync(Dictionary<string, JsonElement> parameters, bool dryRun, CancellationToken ct)
    {
        var limit = Math.Max(0, GetInt(parameters, "limit", 3));
        var includeDisabled = GetBool(parameters, "includeDisabled", false);

        // The background wishlist cycle searches every enabled item anyway; items searched within
        // wishlist.interval_seconds are left to it.
        var intervalSeconds = options.CurrentValue.Wishlist.IntervalSeconds;
        var cutoff = DateTime.UtcNow.AddSeconds(-intervalSeconds);

        var all = await wishlist.ListAsync();
        var candidates = all.Where(i => includeDisabled || i.Enabled).ToList();
        var eligible = candidates
            .Where(i => i.LastSearchedAt is null || i.LastSearchedAt <= cutoff)
            .OrderBy(i => i.LastSearchedAt ?? DateTime.MinValue)
            .ToList();
        var picked = eligible.Take(limit).ToList();

        var recentlySearched = candidates.Count - eligible.Count;
        var disabled = all.Count - candidates.Count;
        var overLimit = eligible.Count - picked.Count;
        var skipped = new List<string>();
        if (recentlySearched > 0)
        {
            skipped.Add($"{recentlySearched} searched in the last {intervalSeconds} s");
        }

        if (disabled > 0)
        {
            skipped.Add($"{disabled} disabled");
        }

        if (overLimit > 0)
        {
            skipped.Add($"{overLimit} over the limit of {limit}");
        }

        string SkippedText() => skipped.Count == 0 ? string.Empty : "; skipped: " + string.Join(", ", skipped);

        var items = picked.Select(i => new { id = i.Id, searchText = i.SearchText, enabled = i.Enabled, lastSearchedAt = i.LastSearchedAt }).ToList();
        var details = new { items, total = all.Count, intervalSeconds, skipped = new { recentlySearched, disabled, overLimit } };

        if (picked.Count == 0)
        {
            return new(AutomationRunStatus.Skipped, $"No wishlist items eligible for a search{SkippedText()}", details);
        }

        if (dryRun)
        {
            return new(AutomationRunStatus.Succeeded, $"Would run {picked.Count} of {all.Count} wishlist searches{SkippedText()}", details);
        }

        var started = 0;
        var alreadyRunning = 0;
        var errors = new List<string>();
        foreach (var item in picked)
        {
            try
            {
                // null: the item's search is already running (background cycle, "search now" or another automation).
                if (await wishlist.RunSearchAsync(item.Id, ct) == null)
                {
                    alreadyRunning++;
                }
                else
                {
                    started++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Cancellation of ct (shutdown, aborted request) propagates, so the run is not recorded.
                errors.Add($"{item.SearchText}: {ex.Message}");
            }
        }

        if (alreadyRunning > 0)
        {
            skipped.Insert(0, $"{alreadyRunning} already being searched");
        }

        var status = started > 0 ? AutomationRunStatus.Succeeded : errors.Count > 0 ? AutomationRunStatus.Failed : AutomationRunStatus.Skipped;
        return new(
            status,
            $"Started {started} of {picked.Count} wishlist searches ({errors.Count} failed){SkippedText()}",
            new { items, total = all.Count, intervalSeconds, skipped = new { recentlySearched, disabled, overLimit, alreadyRunning }, started, failed = errors.Count, errors });
    }

    private async Task<AutomationActionResult> LibraryHealthScanAsync(Dictionary<string, JsonElement> parameters, bool dryRun, CancellationToken ct)
    {
        var requested = GetString(parameters, "libraryPath", string.Empty);

        // Same guard as LibraryHealthService.StartScanAsync, applied before any filesystem access.
        var shareRoots = options.CurrentValue.Shares.Directories
            .Select(share => new Share(share).LocalPath)
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .ToList();
        var path = PathGuard.NormalizeAbsolutePathWithinRoots(requested, shareRoots);
        if (path == null)
        {
            return new(AutomationRunStatus.Failed, "Library Health scans must target a configured share directory", new { libraryPath = requested, withinShareRoots = false });
        }

        if (!Directory.Exists(path))
        {
            return new(AutomationRunStatus.Failed, $"Path missing: {path}", new { libraryPath = path, exists = false });
        }

        if (dryRun)
        {
            var count = Directory
                .EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Where(f => AudioExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Take(MaxCountedFiles)
                .Count();

            var suffix = count >= MaxCountedFiles ? "+" : string.Empty;
            return new(AutomationRunStatus.Succeeded, $"Would scan {count}{suffix} audio files in {path}", new { libraryPath = path, exists = true, audioFiles = count, truncated = count >= MaxCountedFiles });
        }

        try
        {
            var scanId = await libraryHealth.StartScanAsync(new LibraryHealthScanRequest { LibraryPath = path }, ct);
            return new(AutomationRunStatus.Succeeded, $"Started library health scan {scanId} for {path}", new { libraryPath = path, scanId });
        }
        catch (LibraryHealthScanAlreadyRunningException ex)
        {
            return new(AutomationRunStatus.Skipped, "A library health scan is already running", new { libraryPath = path, scanId = ex.ScanId });
        }
    }

    private async Task<AutomationActionResult> LocalDiagnosticsAsync(bool dryRun)
    {
        var current = state.CurrentValue;
        var lastScan = await GetLastShareScanAsync();
        var downloads = options.CurrentValue.Directories.Downloads;
        var downloadsExists = Directory.Exists(downloads);

        // Only a real run tests writability (it creates and deletes an empty probe file); a dry run writes nothing.
        bool? downloadsWritable = dryRun ? null : IsWritable(downloads);

        var problems = new List<string>();
        if (!current.Server.IsLoggedIn)
        {
            problems.Add($"Soulseek not logged in ({current.Server.State})");
        }

        if (!current.Shares.Ready)
        {
            problems.Add("share cache not ready");
        }

        if (!downloadsExists)
        {
            problems.Add("downloads directory missing");
        }
        else if (downloadsWritable == false)
        {
            problems.Add("downloads directory not writable");
        }

        var summary = problems.Count == 0
            ? $"Logged in as {current.User.Username}; {current.Shares.Files} shared files; " + (dryRun ? "downloads folder exists (write test skipped in dry run)" : "downloads writable")
            : "Problems: " + string.Join(", ", problems);

        return new(problems.Count == 0 ? AutomationRunStatus.Succeeded : AutomationRunStatus.Failed, summary, new
        {
            soulseek = new { state = current.Server.State.ToString(), connected = current.Server.IsConnected, loggedIn = current.Server.IsLoggedIn, address = current.Server.Address, username = current.User.Username },
            shares = new { ready = current.Shares.Ready, scanning = current.Shares.Scanning, files = current.Shares.Files, directories = current.Shares.Directories, lastScanAt = lastScan },
            downloads = new { path = downloads, exists = downloadsExists, writable = downloadsWritable },
            problems,
        });
    }

    private async Task<AutomationActionResult> StaleCacheRemindersAsync(Dictionary<string, JsonElement> parameters, bool dryRun)
    {
        var maxAge = TimeSpan.FromHours(Math.Max(0, GetInt(parameters, "maxShareCacheAgeHours", 24)));
        var lastScan = await GetLastShareScanAsync();
        var age = lastScan.HasValue ? DateTimeOffset.UtcNow - lastScan.Value : (TimeSpan?)null;
        var stale = !age.HasValue || age.Value > maxAge;
        var ageText = age.HasValue ? $"{age.Value.TotalHours:F1} h" : "never scanned";
        var details = new { lastScanAt = lastScan, ageHours = age?.TotalHours, maxAgeHours = maxAge.TotalHours, stale };

        if (!stale)
        {
            return new(AutomationRunStatus.Skipped, $"Share cache is fresh ({ageText}); no rescan needed", details);
        }

        if (dryRun)
        {
            return new(AutomationRunStatus.Succeeded, $"Share cache is stale ({ageText}); a rescan would be triggered", details);
        }

        if (state.CurrentValue.Shares.Scanning)
        {
            return new(AutomationRunStatus.Skipped, "A share scan is already in progress", details);
        }

        // Scans can take minutes; the controller equivalent also fires and forgets.
        _ = shares.ScanAsync().ContinueWith(t => logger.LogWarning(t.Exception, "Share rescan triggered by automation failed"), TaskContinuationOptions.OnlyOnFaulted);
        return new(AutomationRunStatus.Succeeded, $"Share cache is stale ({ageText}); rescan triggered", details);
    }

    private async Task<DateTimeOffset?> GetLastShareScanAsync()
    {
        var scans = await shares.ListScansAsync();
        var latest = scans.Where(s => s.EndedAt.HasValue).Select(s => s.EndedAt!.Value).DefaultIfEmpty(0).Max();
        return latest > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(latest) : null;
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return false;
            }

            var probe = Path.Combine(directory, $".slskdn-write-probe-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int GetInt(Dictionary<string, JsonElement> p, string key, int fallback)
        => p.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : fallback;

    private static bool GetBool(Dictionary<string, JsonElement> p, string key, bool fallback)
        => p.TryGetValue(key, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : fallback;

    private static string GetString(Dictionary<string, JsonElement> p, string key, string fallback)
        => p.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
}
