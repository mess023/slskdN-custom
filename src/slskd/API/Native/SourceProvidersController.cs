// <copyright file="SourceProvidersController.cs" company="slskdN Team">
//     Copyright (c) slskdN Team. All rights reserved.
// </copyright>
namespace slskd.API.Native;

using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using slskd.Core.Security;
using slskd.Signals.Swarm;
using slskd.VirtualSoulfind.Core;
using slskd.VirtualSoulfind.v2.Backends;
using slskd.VirtualSoulfind.v2.Configuration;

/// <summary>
/// Provides a read-only source provider capability catalogue.
/// </summary>
[ApiController]
[Route("api/source-providers")]
[Route("api/v{version:apiVersion}/source-providers")]
[ApiVersion("0")]
[Produces("application/json")]
[ValidateCsrfForCookiesOnly]
public class SourceProvidersController : ControllerBase
{
    private static readonly IReadOnlyList<ProviderDefinition> ProviderDefinitions =
    [
        new(
            ContentBackendType.LocalLibrary,
            "Local Library",
            "Already indexed or shared files on this slskdN node.",
            "local",
            ContentDomain.Music,
            ["search", "download", "checksum", "metadata", "preview"],
            false,
            "No peer traffic. Uses local catalogue/share data only."),
        new(
            ContentBackendType.Soulseek,
            "Soulseek",
            "Public Soulseek search and peer transfer path.",
            "public-network",
            ContentDomain.Music,
            ["search", "download", "preview", "metadata"],
            true,
            "Rate-limited searches, queue/speed filtering, and user-visible profile caps."),
        new(
            ContentBackendType.NativeMesh,
            "Native Mesh",
            "Trusted slskdN overlay peers with content descriptors.",
            "trusted-mesh",
            ContentDomain.Music,
            ["search", "download", "checksum", "preview"],
            false,
            "Trusted overlay only; no public peer probing."),
        new(
            ContentBackendType.MeshDht,
            "Mesh DHT",
            "DHT-backed hints for verified content candidates.",
            "trusted-mesh",
            ContentDomain.Music,
            ["search", "checksum", "preview"],
            false,
            "DHT operations must use rate limits and trust thresholds."),
        new(
            ContentBackendType.Http,
            "HTTP",
            "User-configured HTTP or HTTPS repositories.",
            "configured-network",
            null,
            ["download", "checksum", "preview"],
            true,
            "Requires allowlisted hosts, size limits, and SSRF-safe fetches."),
        new(
            ContentBackendType.WebDav,
            "WebDAV",
            "User-configured WebDAV repositories.",
            "configured-network",
            null,
            ["search", "download", "checksum", "preview", "auth"],
            true,
            "Requires allowlisted hosts, size limits, and configured credentials when needed."),
        new(
            ContentBackendType.S3,
            "S3",
            "User-configured S3-compatible object storage.",
            "configured-network",
            null,
            ["search", "download", "checksum", "auth"],
            true,
            "Requires bucket allowlists, object size limits, and configured credentials."),
        new(
            ContentBackendType.Lan,
            "LAN",
            "User-configured local network shares.",
            "configured-lan",
            null,
            ["search", "download", "checksum", "preview"],
            true,
            "Restricted to allowed local-network roots."),
        new(
            ContentBackendType.Torrent,
            "Private Torrent",
            "Explicitly configured private torrent or magnet sources.",
            "high-risk",
            null,
            ["search", "download", "checksum"],
            true,
            "High-risk provider. Must remain disabled until configured and explicitly enabled."),
    ];

    private static readonly IReadOnlyList<ProfileProviderPolicyResponse> ProfilePolicies =
    [
        new(
            "lossless-exact",
            "Lossless Exact",
            ["LocalLibrary", "Soulseek", "NativeMesh", "MeshDht"],
            false,
            "Prefer exact local and public-network music matches before trusted mesh fallback."),
        new(
            "fast-good-enough",
            "Fast Good Enough",
            ["LocalLibrary", "Soulseek"],
            false,
            "Prefer quick local or public-network candidates with bounded result collection."),
        new(
            "album-complete",
            "Album Complete",
            ["LocalLibrary", "Soulseek", "NativeMesh", "MeshDht"],
            false,
            "Prefer folder-level candidates and trusted hash evidence after public search."),
        new(
            "rare-hunt",
            "Rare Hunt",
            ["LocalLibrary", "Soulseek", "NativeMesh", "MeshDht", "Http", "WebDav", "S3"],
            false,
            "Allow broader configured-source review while keeping every acquisition manual."),
        new(
            "conservative-network",
            "Conservative Network",
            ["LocalLibrary", "Soulseek"],
            false,
            "Keep public-network breadth narrow and avoid configured-source fallback by default."),
        new(
            "mesh-preferred",
            "Mesh Preferred",
            ["LocalLibrary", "NativeMesh", "MeshDht", "Soulseek"],
            false,
            "Prefer trusted mesh candidates, then fall back to public Soulseek compatibility."),
        new(
            "metadata-strict",
            "Metadata Strict",
            ["LocalLibrary", "Soulseek", "NativeMesh", "MeshDht"],
            false,
            "Prefer sources that can be explained and verified before import."),
    ];

    private readonly IReadOnlySet<ContentBackendType> registeredBackends;
    private readonly IOptionsSnapshot<slskd.Options> options;

    public SourceProvidersController(
        IEnumerable<IContentBackend> contentBackends,
        IOptionsSnapshot<slskd.Options> options)
    {
        registeredBackends = contentBackends
            .Select(backend => backend.Type)
            .ToHashSet();
        this.options = options;
    }

    /// <summary>
    /// Gets known source providers and their capability/risk metadata.
    /// </summary>
    [HttpGet]
    [Authorize(Policy = AuthPolicy.Any)]
    public IActionResult Get()
    {
        var v2Enabled = options.Value.VirtualSoulfindV2.Enabled;
        var providers = ProviderDefinitions
            .Select(definition => ToResponse(definition, v2Enabled))
            .OrderBy(provider => provider.SortOrder)
            .ThenBy(provider => provider.Name)
            .ToList();

        return Ok(new SourceProviderCatalogResponse(v2Enabled, providers, ProfilePolicies));
    }

    private SourceProviderResponse ToResponse(ProviderDefinition definition, bool v2Enabled)
    {
        var registered = registeredBackends.Contains(definition.Type);
        var configKey = $"virtual_soulfind_v2.backends.{GetConfigSection(definition.Type)}";
        var (enabled, configured, missingConfigReason) = GetConfiguredState(definition.Type, options.Value.VirtualSoulfindV2.Backends);
        var active = v2Enabled && registered && enabled && configured;
        var disabledReason = active
            ? null
            : GetDisabledReason(v2Enabled, registered, enabled, configKey, missingConfigReason);

        return new SourceProviderResponse(
            Id: definition.Type.ToString(),
            Name: definition.Name,
            Description: definition.Description,
            RiskLevel: definition.RiskLevel,
            Domain: definition.Domain?.ToString() ?? "Any",
            Capabilities: definition.Capabilities,
            RequiresConfiguration: definition.RequiresConfiguration,
            Registered: registered,
            Enabled: enabled,
            Active: active,
            NetworkPolicy: definition.NetworkPolicy,
            DisabledReason: disabledReason,
            ConfigKey: configKey,
            RestartRequired: options.Value.Flags.NoConfigWatch, // yaml edits are only picked up live while the config watcher runs
            SortOrder: GetSortOrder(definition.Type));
    }

    /// <summary>
    ///     Reads the live yaml state of a provider: its <c>enabled</c> flag, whether the minimum
    ///     configuration is present, and the reason text when it is not.
    /// </summary>
    private static (bool Enabled, bool Configured, string MissingConfigReason) GetConfiguredState(ContentBackendType type, BackendLimits backends)
    {
        return type switch
        {
            ContentBackendType.LocalLibrary => (backends.LocalLibrary.Enabled, true, string.Empty),
            ContentBackendType.Soulseek => (backends.Soulseek.Enabled, true, string.Empty),
            ContentBackendType.NativeMesh => (backends.NativeMesh.Enabled, true, string.Empty),
            ContentBackendType.MeshDht => (backends.MeshDht.Enabled, true, string.Empty),
            ContentBackendType.Http => (
                backends.Http.Enabled,
                backends.Http.DomainAllowlist.Count > 0,
                "Enabled but no allowlisted hosts configured."),
            ContentBackendType.WebDav => (
                backends.WebDav.Enabled,
                backends.WebDav.DomainAllowlist.Count > 0,
                "Enabled but no allowlisted hosts configured."),
            ContentBackendType.S3 => (
                backends.S3.Enabled,
                (!string.IsNullOrWhiteSpace(backends.S3.Endpoint) || !string.IsNullOrWhiteSpace(backends.S3.Region)) && backends.S3.BucketAllowlist.Count > 0,
                "Enabled but no endpoint/region or bucket allowlist configured."),
            ContentBackendType.Lan => (backends.Lan.Enabled, true, string.Empty), // empty allowed_networks falls back to private ranges
            ContentBackendType.Torrent => (
                backends.Torrent.Enabled,
                CanFetchTorrent(backends.Torrent),
                "Enabled, but private_only is on and invite_list has no usable host:port peer (allowed_peer_sources must be InviteList or Both)."),
            _ => (false, false, "Unknown provider."),
        };
    }

    /// <summary>
    ///     Mirrors <see cref="MonoTorrentBitTorrentBackend.FetchByInfoHashOrMagnetAsync"/>: with
    ///     <c>private_only</c> it only dials invite_list peers, so without a usable one every fetch is refused.
    /// </summary>
    private static bool CanFetchTorrent(TorrentBackendLimits torrent)
        => !torrent.PrivateOnly
            || MonoTorrentBitTorrentBackend.BuildManualPeers(
                new PrivateTorrentModeOptions
                {
                    PrivateOnly = true,
                    AllowedPeerSources = torrent.AllowedPeerSources,
                    InviteList = torrent.InviteList,
                },
                sources: null,
                includeInviteList: true).Count > 0;

    private static string GetConfigSection(ContentBackendType type)
    {
        return type switch
        {
            ContentBackendType.LocalLibrary => "local_library",
            ContentBackendType.NativeMesh => "native_mesh",
            ContentBackendType.MeshDht => "mesh_dht",
            ContentBackendType.WebDav => "webdav",
            _ => type.ToString().ToLowerInvariant(),
        };
    }

    private static string GetDisabledReason(bool v2Enabled, bool registered, bool enabled, string configKey, string missingConfigReason)
    {
        if (!v2Enabled)
        {
            return "VirtualSoulfind v2 acquisition planning is disabled.";
        }

        if (!registered)
        {
            return "Provider service is not registered in this build.";
        }

        if (!enabled)
        {
            return $"Disabled in configuration ({configKey}.enabled).";
        }

        return missingConfigReason;
    }

    private static int GetSortOrder(ContentBackendType type)
    {
        return type switch
        {
            ContentBackendType.LocalLibrary => 0,
            ContentBackendType.Soulseek => 10,
            ContentBackendType.NativeMesh => 20,
            ContentBackendType.MeshDht => 30,
            ContentBackendType.Http => 40,
            ContentBackendType.WebDav => 50,
            ContentBackendType.S3 => 60,
            ContentBackendType.Lan => 70,
            ContentBackendType.Torrent => 90,
            _ => 100,
        };
    }

    private sealed record ProviderDefinition(
        ContentBackendType Type,
        string Name,
        string Description,
        string RiskLevel,
        ContentDomain? Domain,
        IReadOnlyList<string> Capabilities,
        bool RequiresConfiguration,
        string NetworkPolicy);
}

public sealed record SourceProviderCatalogResponse(
    bool AcquisitionPlanningEnabled,
    IReadOnlyList<SourceProviderResponse> Providers,
    IReadOnlyList<ProfileProviderPolicyResponse> ProfilePolicies);

public sealed record SourceProviderResponse(
    string Id,
    string Name,
    string Description,
    string RiskLevel,
    string Domain,
    IReadOnlyList<string> Capabilities,
    bool RequiresConfiguration,
    bool Registered,
    bool Enabled,
    bool Active,
    string NetworkPolicy,
    string? DisabledReason,
    string ConfigKey,
    bool RestartRequired,
    int SortOrder);

public sealed record ProfileProviderPolicyResponse(
    string ProfileId,
    string ProfileName,
    IReadOnlyList<string> ProviderPriority,
    bool AutoDownloadEnabled,
    string Notes);
