// <copyright file="VirtualSoulfindOptions.cs" company="slskdN Team">
//     Copyright (c) slskdN Team. All rights reserved.
// </copyright>
namespace slskd.VirtualSoulfind.v2.Configuration
{
    using System.Collections.Generic;
    using slskd.VirtualSoulfind.v2.Backends;
    using slskd.VirtualSoulfind.v2.Planning;
    using YamlDotNet.Serialization;

    /// <summary>
    ///     Configuration options for VirtualSoulfind v2.
    /// </summary>
    public sealed class VirtualSoulfindOptions
    {
        /// <summary>
        ///     Gets or initializes the default planning mode.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         - <see cref="PlanningMode.SoulseekFriendly"/>: Balanced mode (default).
        ///         - <see cref="PlanningMode.OfflinePlanning"/>: Only use local/cached sources.
        ///         - <see cref="PlanningMode.MeshOnly"/>: Only use mesh network sources.
        ///     </para>
        /// </remarks>
        public PlanningMode DefaultMode { get; init; } = PlanningMode.SoulseekFriendly;

        /// <summary>
        ///     Gets or initializes whether VirtualSoulfind v2 is enabled.
        /// </summary>
        /// <remarks>
        ///     If false, all v2 endpoints return 503 Service Unavailable.
        /// </remarks>
        public bool Enabled { get; init; } = true;

        /// <summary>
        ///     Gets or initializes the maximum number of concurrent plan executions.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         This limits how many acquisition plans can be executing simultaneously.
        ///         Higher values allow more parallelism but consume more resources.
        ///     </para>
        ///     <para>
        ///         Default: 3
        ///     </para>
        /// </remarks>
        public int MaxConcurrentExecutions { get; init; } = 3;

        /// <summary>
        ///     Gets or initializes the maximum number of intents to process per batch.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         The background processor will pick up to this many pending intents
        ///         in each processing cycle.
        ///     </para>
        ///     <para>
        ///         Default: 10
        ///     </para>
        /// </remarks>
        public int ProcessorBatchSize { get; init; } = 10;

        /// <summary>
        ///     Gets or initializes the interval (in milliseconds) between processor runs.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         How often the background processor checks for new intents to process.
        ///     </para>
        ///     <para>
        ///         Default: 5000ms (5 seconds)
        ///     </para>
        /// </remarks>
        public int ProcessorIntervalMs { get; init; } = 5000;

        /// <summary>
        ///     Gets or initializes backend-specific options.
        /// </summary>
        public BackendLimits Backends { get; init; } = new BackendLimits();

        /// <summary>
        ///     Gets or initializes work budget limits for plan execution.
        /// </summary>
        public WorkBudgetLimits WorkBudget { get; init; } = new WorkBudgetLimits();
    }

    /// <summary>
    ///     Per-backend configuration limits.
    /// </summary>
    public sealed class BackendLimits
    {
        /// <summary>
        ///     Gets or initializes local library backend options.
        /// </summary>
        public LocalLibraryBackendLimits LocalLibrary { get; init; } = new LocalLibraryBackendLimits();

        /// <summary>
        ///     Gets or initializes Soulseek backend options.
        /// </summary>
        public SoulseekBackendLimits Soulseek { get; init; } = new SoulseekBackendLimits();

        /// <summary>
        ///     Gets or initializes Mesh DHT backend options.
        /// </summary>
        public MeshBackendLimits Mesh { get; init; } = new MeshBackendLimits();

        /// <summary>
        ///     Gets or initializes native mesh (trusted overlay) backend options.
        /// </summary>
        public MeshSourceBackendLimits NativeMesh { get; init; } = new MeshSourceBackendLimits();

        /// <summary>
        ///     Gets or initializes mesh DHT hint backend options.
        /// </summary>
        public MeshSourceBackendLimits MeshDht { get; init; } = new MeshSourceBackendLimits();

        /// <summary>
        ///     Gets or initializes Torrent backend options.
        /// </summary>
        public TorrentBackendLimits Torrent { get; init; } = new TorrentBackendLimits();

        /// <summary>
        ///     Gets or initializes HTTP backend options.
        /// </summary>
        public HttpBackendLimits Http { get; init; } = new HttpBackendLimits();

        /// <summary>
        ///     Gets or initializes WebDAV backend options. Yaml key is <c>webdav</c>.
        /// </summary>
        [YamlMember(Alias = "webdav")]
        public WebDavBackendLimits WebDav { get; init; } = new WebDavBackendLimits();

        /// <summary>
        ///     Gets or initializes S3 backend options.
        /// </summary>
        public S3BackendLimits S3 { get; init; } = new S3BackendLimits();

        /// <summary>
        ///     Gets or initializes LAN backend options.
        /// </summary>
        public LanBackendLimits Lan { get; init; } = new LanBackendLimits();
    }

    /// <summary>
    ///     Local library backend options.
    /// </summary>
    public sealed class LocalLibraryBackendLimits
    {
        /// <summary>
        ///     Gets or initializes whether the local library backend is enabled.
        /// </summary>
        public bool Enabled { get; init; } = true;
    }

    /// <summary>
    ///     Options shared by the native mesh and mesh DHT backends.
    /// </summary>
    public sealed class MeshSourceBackendLimits
    {
        /// <summary>
        ///     Gets or initializes whether the backend is enabled.
        /// </summary>
        public bool Enabled { get; init; }

        /// <summary>
        ///     Gets or initializes the minimum trust score (0.0 - 1.0) for a candidate.
        /// </summary>
        public float MinTrustScore { get; init; } = 0.3f;

        /// <summary>
        ///     Gets or initializes the maximum candidates returned per item.
        /// </summary>
        public int MaxCandidatesPerItem { get; init; } = 20;
    }

    /// <summary>
    ///     WebDAV backend options.
    /// </summary>
    public sealed class WebDavBackendLimits
    {
        /// <summary>
        ///     Gets or initializes whether the WebDAV backend is enabled.
        /// </summary>
        public bool Enabled { get; init; }

        /// <summary>
        ///     Gets or initializes the allowlisted hosts (SSRF protection).
        /// </summary>
        public List<string> DomainAllowlist { get; init; } = new List<string>();

        /// <summary>
        ///     Gets or initializes the maximum file size in bytes.
        /// </summary>
        public long MaxFileSizeBytes { get; init; } = 500_000_000;

        /// <summary>
        ///     Gets or initializes the validation request timeout in seconds.
        /// </summary>
        public int ValidationTimeoutSeconds { get; init; } = 15;

        /// <summary>
        ///     Gets or initializes the basic auth username.
        /// </summary>
        public string? Username { get; init; }

        /// <summary>
        ///     Gets or initializes the basic auth password.
        /// </summary>
        [Secret]
        public string? Password { get; init; }

        /// <summary>
        ///     Gets or initializes the bearer token (overrides basic auth when set).
        /// </summary>
        [Secret]
        public string? BearerToken { get; init; }
    }

    /// <summary>
    ///     S3 backend options.
    /// </summary>
    public sealed class S3BackendLimits
    {
        /// <summary>
        ///     Gets or initializes whether the S3 backend is enabled.
        /// </summary>
        public bool Enabled { get; init; }

        /// <summary>
        ///     Gets or initializes the endpoint for S3-compatible services. Leave empty for AWS S3.
        /// </summary>
        public string? Endpoint { get; init; }

        /// <summary>
        ///     Gets or initializes the AWS region.
        /// </summary>
        public string? Region { get; init; }

        /// <summary>
        ///     Gets or initializes the access key.
        /// </summary>
        public string? AccessKey { get; init; }

        /// <summary>
        ///     Gets or initializes the secret key.
        /// </summary>
        [Secret]
        public string? SecretKey { get; init; }

        /// <summary>
        ///     Gets or initializes whether to force path-style addressing.
        /// </summary>
        public bool ForcePathStyle { get; init; } = true;

        /// <summary>
        ///     Gets or initializes the allowlisted buckets. Empty denies every bucket.
        /// </summary>
        public List<string> BucketAllowlist { get; init; } = new List<string>();

        /// <summary>
        ///     Gets or initializes the maximum object size in bytes.
        /// </summary>
        public long MaxFileSizeBytes { get; init; } = 500_000_000;

        /// <summary>
        ///     Gets or initializes the validation request timeout in seconds.
        /// </summary>
        public int ValidationTimeoutSeconds { get; init; } = 15;
    }

    /// <summary>
    ///     LAN backend options.
    /// </summary>
    public sealed class LanBackendLimits
    {
        /// <summary>
        ///     Gets or initializes whether the LAN backend is enabled.
        /// </summary>
        public bool Enabled { get; init; }

        /// <summary>
        ///     Gets or initializes the allowed CIDR network ranges. Empty means the private ranges
        ///     (192.168.0.0/16, 10.0.0.0/8, 172.16.0.0/12). Kept empty by default because the
        ///     configuration binder appends yaml items to defaults instead of replacing them.
        /// </summary>
        public List<string> AllowedNetworks { get; init; } = new List<string>();

        /// <summary>
        ///     Gets or initializes the allowed hostnames for non-IP UNC paths.
        /// </summary>
        public List<string> AllowedHostnames { get; init; } = new List<string>();

        /// <summary>
        ///     Gets or initializes the maximum candidates returned per item.
        /// </summary>
        public int MaxCandidatesPerItem { get; init; } = 10;
    }

    /// <summary>
    ///     Soulseek backend limits (H-08 compliance).
    /// </summary>
    public sealed class SoulseekBackendLimits
    {
        /// <summary>
        ///     Gets or initializes whether the Soulseek backend is enabled.
        /// </summary>
        public bool Enabled { get; init; } = true;

        /// <summary>
        ///     Gets or initializes the maximum number of searches per minute.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         H-08 requirement: Prevent excessive search rate that could lead to bans.
        ///     </para>
        ///     <para>
        ///         Default: 10 searches/minute
        ///     </para>
        /// </remarks>
        public int MaxSearchesPerMinute { get; init; } = 10;

        /// <summary>
        ///     Gets or initializes the maximum number of parallel searches.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Default: 2 parallel searches
        ///     </para>
        /// </remarks>
        public int MaxParallelSearches { get; init; } = 2;

        /// <summary>
        ///     Gets or initializes the maximum number of browse requests per minute.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         H-08 requirement: Limit user browsing to prevent abuse.
        ///     </para>
        ///     <para>
        ///         Default: 5 browses/minute
        ///     </para>
        /// </remarks>
        public int MaxBrowsesPerMinute { get; init; } = 5;

        /// <summary>
        ///     Gets or initializes the minimum upload speed (bytes/sec) for trust scoring.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Peers with upload speeds below this threshold get lower trust scores.
        ///     </para>
        ///     <para>
        ///         Default: 50 KB/s
        ///     </para>
        /// </remarks>
        public int MinUploadSpeedBytesPerSec { get; init; } = 50 * 1024;

        /// <summary>
        ///     Gets or initializes the search timeout in milliseconds.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Default: 10000ms (10 seconds)
        ///     </para>
        /// </remarks>
        public int SearchTimeoutMs { get; init; } = 10_000;
    }

    /// <summary>
    ///     Mesh DHT backend limits.
    /// </summary>
    public sealed class MeshBackendLimits
    {
        /// <summary>
        ///     Gets or initializes the maximum number of mesh queries per minute.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Default: 30 queries/minute
        ///     </para>
        /// </remarks>
        public int MaxQueriesPerMinute { get; init; } = 30;

        /// <summary>
        ///     Gets or initializes the query timeout in milliseconds.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Default: 5000ms (5 seconds)
        ///     </para>
        /// </remarks>
        public int QueryTimeoutMs { get; init; } = 5000;

        /// <summary>
        ///     Gets or initializes the minimum trust score for mesh sources.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Sources with trust scores below this threshold are excluded.
        ///     </para>
        ///     <para>
        ///         Default: 0.5 (50%)
        ///     </para>
        /// </remarks>
        public float MinTrustScore { get; init; } = 0.5f;
    }

    /// <summary>
    ///     Torrent backend enablement, discovery policy, and limits.
    /// </summary>
    public sealed class TorrentBackendLimits
    {
        /// <summary>
        ///     Gets or initializes whether torrent discovery and fetching are enabled.
        /// </summary>
        public bool Enabled { get; init; }

        /// <summary>
        ///     Gets or initializes the minimum number of seeders for trust scoring.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Torrents with fewer seeders get lower trust scores.
        ///     </para>
        ///     <para>
        ///         Default: 3 seeders
        ///     </para>
        /// </remarks>
        public int MinSeeders { get; init; } = 3;

        /// <summary>
        ///     Gets or initializes whether to require private peer discovery.
        /// </summary>
        public bool PrivateOnly { get; init; } = true;

        /// <summary>
        ///     Gets or initializes whether BitTorrent DHT discovery is disabled.
        /// </summary>
        public bool DisableDht { get; init; } = true;

        /// <summary>
        ///     Gets or initializes whether peer exchange is disabled.
        /// </summary>
        public bool DisablePex { get; init; } = true;

        /// <summary>
        ///     Gets or initializes the permitted sources for private torrent peers.
        /// </summary>
        public PrivatePeerSource AllowedPeerSources { get; init; } = PrivatePeerSource.Both;

        /// <summary>
        ///     Gets or initializes manually permitted private peer endpoints in host:port form.
        /// </summary>
        public List<string> InviteList { get; init; } = new List<string>();
    }

    /// <summary>
    ///     HTTP backend limits.
    /// </summary>
    public sealed class HttpBackendLimits
    {
        /// <summary>
        ///     Gets or initializes whether the HTTP backend is enabled.
        /// </summary>
        public bool Enabled { get; init; }

        /// <summary>
        ///     Gets or initializes the allowlisted hosts (SSRF protection).
        /// </summary>
        public List<string> DomainAllowlist { get; init; } = new List<string>();

        /// <summary>
        ///     Gets or initializes the maximum file size in bytes.
        /// </summary>
        public long MaxFileSizeBytes { get; init; } = 500_000_000;

        /// <summary>
        ///     Gets or initializes the validation request timeout in seconds.
        /// </summary>
        public int ValidationTimeoutSeconds { get; init; } = 10;

        /// <summary>
        ///     Gets or initializes the maximum number of HTTP requests per minute.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Default: 60 requests/minute
        ///     </para>
        /// </remarks>
        public int MaxRequestsPerMinute { get; init; } = 60;

        /// <summary>
        ///     Gets or initializes the request timeout in milliseconds.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Default: 30000ms (30 seconds)
        ///     </para>
        /// </remarks>
        public int RequestTimeoutMs { get; init; } = 30_000;

        /// <summary>
        ///     Gets or initializes whether to only allow HTTPS URLs.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         If true, HTTP URLs are rejected and only HTTPS is allowed.
        ///     </para>
        ///     <para>
        ///         Default: false (allow both HTTP and HTTPS)
        ///     </para>
        /// </remarks>
        public bool RequireHttps { get; init; } = false;
    }

    /// <summary>
    ///     Work budget limits for plan execution (H-02 integration).
    /// </summary>
    public sealed class WorkBudgetLimits
    {
        /// <summary>
        ///     Gets or initializes the default work budget per plan execution.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         How many work units a single plan execution is allowed to consume.
        ///     </para>
        ///     <para>
        ///         Default: 1000 units
        ///     </para>
        /// </remarks>
        public int DefaultBudgetPerExecution { get; init; } = 1000;

        /// <summary>
        ///     Gets or initializes the maximum work budget per execution.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Even high-priority plans cannot exceed this limit.
        ///     </para>
        ///     <para>
        ///         Default: 5000 units
        ///     </para>
        /// </remarks>
        public int MaxBudgetPerExecution { get; init; } = 5000;
    }
}
