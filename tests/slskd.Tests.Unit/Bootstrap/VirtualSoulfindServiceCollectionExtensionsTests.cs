// <copyright file="VirtualSoulfindServiceCollectionExtensionsTests.cs" company="slskdN Team">
//     Copyright (c) slskdN Team. All rights reserved.
// </copyright>

namespace slskd.Tests.Unit.Bootstrap;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using slskd.Bootstrap;
using slskd.Core;
using slskd.Tests.Unit;
using slskd.VirtualSoulfind.v2.Backends;
using slskd.VirtualSoulfind.v2.Configuration;
using slskd.VirtualSoulfind.ShadowIndex;
using Xunit;
using VirtualSoulfindV2Options = slskd.VirtualSoulfind.v2.Configuration.VirtualSoulfindOptions;

public sealed class VirtualSoulfindServiceCollectionExtensionsTests
{
    [Fact]
    public async Task AddSlskdVirtualSoulfindServices_RegistersOneConfiguredDhtRateLimiter()
    {
        var options = new slskd.Options
        {
            VirtualSoulfind = new slskd.Core.VirtualSoulfindOptions
            {
                ShadowIndex = new ShadowIndexOptions
                {
                    MaxDhtOperationsPerMinute = 1,
                },
            },
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptionsMonitor<slskd.Options>>(new TestOptionsMonitor<slskd.Options>(options));
        services.AddSlskdVirtualSoulfindServices(options);
        await using var provider = services.BuildServiceProvider();

        var registrations = provider.GetServices<IDhtRateLimiter>().ToList();
        var limiter = Assert.Single(registrations);

        Assert.True(await limiter.TryAcquireAsync(CancellationToken.None));
        Assert.False(await limiter.TryAcquireAsync(CancellationToken.None));
    }

    [Fact]
    public void BuildTorrentBackendOptions_MapsDisabledByDefaultAndPrivatePeerPolicy()
    {
        var disabled = VirtualSoulfindServiceCollectionExtensions.BuildTorrentBackendOptions(new VirtualSoulfindV2Options());
        Assert.False(disabled.Enabled);

        var configured = VirtualSoulfindServiceCollectionExtensions.BuildTorrentBackendOptions(new VirtualSoulfindV2Options
        {
            Enabled = true,
            Backends = new BackendLimits
            {
                Torrent = new TorrentBackendLimits
                {
                    Enabled = true,
                    MinSeeders = 5,
                    PrivateOnly = false,
                    DisableDht = true,
                    DisablePex = false,
                    AllowedPeerSources = PrivatePeerSource.InviteList,
                    InviteList = ["peer.example:6881"],
                },
            },
        });

        Assert.True(configured.Enabled);
        Assert.Equal(5, configured.MinimumSeeders);
        Assert.NotNull(configured.PrivateMode);
        Assert.False(configured.PrivateMode.PrivateOnly);
        Assert.True(configured.PrivateMode.DisableDht);
        Assert.False(configured.PrivateMode.DisablePex);
        Assert.Equal(PrivatePeerSource.InviteList, configured.PrivateMode.AllowedPeerSources);
        Assert.Equal(new[] { "peer.example:6881" }, configured.PrivateMode.InviteList);
    }

    [Fact]
    public async Task AddSlskdVirtualSoulfindServices_ExposesTorrentSettingsFromApplicationOptions()
    {
        var options = new slskd.Options
        {
            VirtualSoulfindV2 = new VirtualSoulfindV2Options
            {
                Backends = new BackendLimits
                {
                    Torrent = new TorrentBackendLimits
                    {
                        Enabled = true,
                        InviteList = ["peer.example:6881"],
                    },
                },
            },
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptionsMonitor<slskd.Options>>(new TestOptionsMonitor<slskd.Options>(options));
        services.AddSlskdVirtualSoulfindServices(options);
        await using var provider = services.BuildServiceProvider();

        var configured = provider.GetRequiredService<IOptionsMonitor<TorrentBackendOptions>>().CurrentValue;

        Assert.True(configured.Enabled);
        Assert.True(configured.PrivateMode.PrivateOnly);
        Assert.Equal(new[] { "peer.example:6881" }, configured.PrivateMode.InviteList);
    }

    [Fact]
    public async Task AddSlskdVirtualSoulfindServices_BackendOptionsFollowApplicationOptionsLive()
    {
        var monitor = new TestOptionsMonitor<slskd.Options>(new slskd.Options());
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptionsMonitor<slskd.Options>>(monitor);
        services.AddSlskdVirtualSoulfindServices(monitor.CurrentValue);
        await using var provider = services.BuildServiceProvider();

        var http = provider.GetRequiredService<IOptionsMonitor<HttpBackendOptions>>();
        var webdav = provider.GetRequiredService<IOptionsMonitor<WebDavBackendOptions>>();
        var s3 = provider.GetRequiredService<IOptionsMonitor<S3BackendOptions>>();
        var lan = provider.GetRequiredService<IOptionsMonitor<LanBackendOptions>>();
        var nativeMesh = provider.GetRequiredService<IOptionsMonitor<NativeMeshBackendOptions>>();
        var meshDht = provider.GetRequiredService<IOptionsMonitor<MeshDhtBackendOptions>>();
        var soulseek = provider.GetRequiredService<IOptionsMonitor<SoulseekBackendOptions>>();

        Assert.False(http.CurrentValue.Enabled);
        Assert.True(soulseek.CurrentValue.Enabled);
        Assert.Empty(s3.CurrentValue.BucketAllowlist); // passed through, not null

        HttpBackendOptions changed = null;
        using var _ = http.OnChange((value, _) => changed = value);

        monitor.Set(new slskd.Options
        {
            VirtualSoulfindV2 = new VirtualSoulfindV2Options
            {
                Backends = new BackendLimits
                {
                    Soulseek = new SoulseekBackendLimits { Enabled = false },
                    Http = new HttpBackendLimits { Enabled = true, DomainAllowlist = ["example.org"], MaxFileSizeBytes = 42, RequireHttps = true },
                    WebDav = new WebDavBackendLimits { Enabled = true, BearerToken = "tok" },
                    S3 = new S3BackendLimits { Enabled = true, Endpoint = "http://minio:9000", BucketAllowlist = ["music"] },
                    Lan = new LanBackendLimits { Enabled = true, AllowedHostnames = ["nas"] },
                    NativeMesh = new MeshSourceBackendLimits { Enabled = true, MaxCandidatesPerItem = 7 },
                    MeshDht = new MeshSourceBackendLimits { Enabled = true, MinTrustScore = 0.9f },
                },
            },
        });

        Assert.NotNull(changed);
        Assert.True(changed.Enabled);
        Assert.True(http.CurrentValue.Enabled);
        Assert.Equal(new[] { "example.org" }, http.CurrentValue.DomainAllowlist);
        Assert.Equal(42, http.CurrentValue.MaxFileSizeBytes);
        Assert.True(http.CurrentValue.RequireHttps);
        Assert.False(soulseek.CurrentValue.Enabled);
        Assert.Equal("tok", webdav.CurrentValue.BearerToken);
        Assert.Equal(new[] { "music" }, s3.CurrentValue.BucketAllowlist);
        Assert.Equal(new[] { "nas" }, lan.CurrentValue.AllowedHostnames);
        Assert.Equal(7, nativeMesh.CurrentValue.MaxCandidatesPerItem);
        Assert.Equal(0.9f, meshDht.CurrentValue.MinimumTrustScore, 3);
    }
}
