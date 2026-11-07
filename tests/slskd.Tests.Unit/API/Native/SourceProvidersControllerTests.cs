// <copyright file="SourceProvidersControllerTests.cs" company="slskdN Team">
//     Copyright (c) slskdN Team. All rights reserved.
// </copyright>
namespace slskd.Tests.Unit.API.Native;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Moq;
using slskd.API.Native;
using slskd.VirtualSoulfind.Core;
using slskd.VirtualSoulfind.v2.Backends;
using slskd.VirtualSoulfind.v2.Configuration;
using Xunit;

public class SourceProvidersControllerTests
{
    [Fact]
    public void Get_ReturnsKnownProvidersWithRegistrationAndActiveState()
    {
        var controller = CreateController(
            enabled: true,
            ContentBackendType.LocalLibrary,
            ContentBackendType.Soulseek,
            ContentBackendType.Torrent);

        var result = controller.Get();

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<SourceProviderCatalogResponse>(ok.Value);
        Assert.True(response.AcquisitionPlanningEnabled);
        Assert.NotEmpty(response.ProfilePolicies);

        var local = AssertProvider(response, "LocalLibrary");
        Assert.True(local.Registered);
        Assert.True(local.Active);
        Assert.Contains("download", local.Capabilities);

        var soulseek = AssertProvider(response, "Soulseek");
        Assert.True(soulseek.Registered);
        Assert.True(soulseek.Active);
        Assert.Equal("public-network", soulseek.RiskLevel);
        Assert.Contains("Rate-limited", soulseek.NetworkPolicy);

        var torrent = AssertProvider(response, "Torrent");
        Assert.True(torrent.Registered);
        Assert.False(torrent.Active);
        Assert.True(torrent.RequiresConfiguration);
        Assert.Equal("high-risk", torrent.RiskLevel);
        Assert.False(torrent.Enabled);
        Assert.Equal("Disabled in configuration (virtual_soulfind_v2.backends.torrent.enabled).", torrent.DisabledReason);
        Assert.Equal("virtual_soulfind_v2.backends.torrent", torrent.ConfigKey);
        Assert.False(torrent.RestartRequired);
    }

    [Fact]
    public void Get_WhenHttpEnabledWithoutAllowlist_ReportsEnabledButInactive()
    {
        var controller = CreateController(
            enabled: true,
            new BackendLimits { Http = new HttpBackendLimits { Enabled = true } },
            ContentBackendType.Http);

        var response = GetCatalog(controller);
        var http = AssertProvider(response, "Http");

        Assert.True(http.Enabled);
        Assert.False(http.Active);
        Assert.Equal("Enabled but no allowlisted hosts configured.", http.DisabledReason);
        Assert.Equal("virtual_soulfind_v2.backends.http", http.ConfigKey);
    }

    [Fact]
    public void Get_WhenHttpEnabledWithAllowlist_ReportsActive()
    {
        var controller = CreateController(
            enabled: true,
            new BackendLimits { Http = new HttpBackendLimits { Enabled = true, DomainAllowlist = ["example.org"] } },
            ContentBackendType.Http);

        var response = GetCatalog(controller);
        var http = AssertProvider(response, "Http");

        Assert.True(http.Enabled);
        Assert.True(http.Active);
        Assert.Null(http.DisabledReason);
        Assert.False(http.RestartRequired);
    }

    [Theory]
    [InlineData(true, PrivatePeerSource.Both, new string[0], false)]
    [InlineData(true, PrivatePeerSource.Both, new[] { "peer.example" }, false)] // no port: the fetch drops it
    [InlineData(true, PrivatePeerSource.Overlay, new[] { "peer.example:6881" }, false)] // the resolver fetch has no overlay peers
    [InlineData(true, PrivatePeerSource.InviteList, new[] { "peer.example:6881" }, true)]
    [InlineData(false, PrivatePeerSource.Both, new string[0], true)]
    public void Get_TorrentIsActiveOnlyWhenItsFetchHasAPeer(bool privateOnly, PrivatePeerSource peerSources, string[] inviteList, bool active)
    {
        var controller = CreateController(
            enabled: true,
            new BackendLimits
            {
                Torrent = new TorrentBackendLimits
                {
                    Enabled = true,
                    PrivateOnly = privateOnly,
                    AllowedPeerSources = peerSources,
                    InviteList = [.. inviteList],
                },
            },
            ContentBackendType.Torrent);

        var torrent = AssertProvider(GetCatalog(controller), "Torrent");

        Assert.True(torrent.Enabled);
        Assert.Equal(active, torrent.Active);
        Assert.Equal(
            active ? null : "Enabled, but private_only is on and invite_list has no usable host:port peer (allowed_peer_sources must be InviteList or Both).",
            torrent.DisabledReason);
    }

    [Fact]
    public void Get_WhenSoulseekDisabledInConfiguration_ReportsConfigReason()
    {
        var controller = CreateController(
            enabled: true,
            new BackendLimits { Soulseek = new SoulseekBackendLimits { Enabled = false } },
            ContentBackendType.Soulseek);

        var response = GetCatalog(controller);
        var soulseek = AssertProvider(response, "Soulseek");

        Assert.True(soulseek.Registered);
        Assert.False(soulseek.Enabled);
        Assert.False(soulseek.Active);
        Assert.Equal("Disabled in configuration (virtual_soulfind_v2.backends.soulseek.enabled).", soulseek.DisabledReason);
    }

    [Fact]
    public void Get_WebDavConfigKeyUsesFrontendSectionName()
    {
        var controller = CreateController(
            enabled: true,
            new BackendLimits { WebDav = new WebDavBackendLimits { Enabled = true, DomainAllowlist = ["dav.example.org"] } },
            ContentBackendType.WebDav);

        var webdav = AssertProvider(GetCatalog(controller), "WebDav");

        Assert.Equal("virtual_soulfind_v2.backends.webdav", webdav.ConfigKey);
        Assert.True(webdav.Active);
    }

    [Fact]
    public void Get_ReturnsProfileProviderPoliciesWithAutomationDisabled()
    {
        var controller = CreateController(
            enabled: true,
            ContentBackendType.LocalLibrary,
            ContentBackendType.Soulseek,
            ContentBackendType.NativeMesh,
            ContentBackendType.MeshDht);

        var result = controller.Get();

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<SourceProviderCatalogResponse>(ok.Value);
        var meshPreferred = Assert.Single(response.ProfilePolicies, policy => policy.ProfileId == "mesh-preferred");
        Assert.Equal(new[] { "LocalLibrary", "NativeMesh", "MeshDht", "Soulseek" }, meshPreferred.ProviderPriority);
        Assert.False(meshPreferred.AutoDownloadEnabled);
        Assert.All(response.ProfilePolicies, policy => Assert.False(policy.AutoDownloadEnabled));
    }

    [Fact]
    public void Get_WhenAcquisitionPlanningDisabled_ReturnsVisibleDisabledProviders()
    {
        var controller = CreateController(
            enabled: false,
            ContentBackendType.LocalLibrary,
            ContentBackendType.Soulseek);

        var result = controller.Get();

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<SourceProviderCatalogResponse>(ok.Value);
        Assert.False(response.AcquisitionPlanningEnabled);
        Assert.All(response.Providers, provider => Assert.False(provider.Active));
        Assert.All(response.Providers, provider => Assert.Equal("VirtualSoulfind v2 acquisition planning is disabled.", provider.DisabledReason));
    }

    [Fact]
    public void Get_WhenProviderNotRegistered_ShowsRegistrationReason()
    {
        var controller = CreateController(enabled: true, ContentBackendType.LocalLibrary);

        var result = controller.Get();

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<SourceProviderCatalogResponse>(ok.Value);
        var soulseek = AssertProvider(response, "Soulseek");
        Assert.False(soulseek.Registered);
        Assert.False(soulseek.Active);
        Assert.Equal("Provider service is not registered in this build.", soulseek.DisabledReason);
    }

    private static SourceProviderCatalogResponse GetCatalog(SourceProvidersController controller)
    {
        var ok = Assert.IsType<OkObjectResult>(controller.Get());
        return Assert.IsType<SourceProviderCatalogResponse>(ok.Value);
    }

    private static SourceProvidersController CreateController(bool enabled, params ContentBackendType[] registeredTypes)
        => CreateController(enabled, new BackendLimits(), registeredTypes);

    [Fact]
    public void Get_WhenConfigWatchDisabled_ReportsRestartRequired()
    {
        var controller = CreateController(enabled: true, new BackendLimits(), noConfigWatch: true, ContentBackendType.LocalLibrary);

        var response = GetCatalog(controller);

        Assert.All(response.Providers, provider => Assert.True(provider.RestartRequired));
    }

    private static SourceProvidersController CreateController(bool enabled, BackendLimits backendLimits, params ContentBackendType[] registeredTypes)
        => CreateController(enabled, backendLimits, noConfigWatch: false, registeredTypes);

    private static SourceProvidersController CreateController(bool enabled, BackendLimits backendLimits, bool noConfigWatch, params ContentBackendType[] registeredTypes)
    {
        var backends = registeredTypes.Select(type =>
        {
            var backend = new Mock<IContentBackend>();
            backend.SetupGet(item => item.Type).Returns(type);
            backend.SetupGet(item => item.SupportedDomain).Returns(ContentDomain.Music);
            return backend.Object;
        });

        var options = new Mock<IOptionsSnapshot<slskd.Options>>();
        options
            .SetupGet(snapshot => snapshot.Value)
            .Returns(new slskd.Options
            {
                Flags = new slskd.Options.FlagsOptions { NoConfigWatch = noConfigWatch },
                VirtualSoulfindV2 = new slskd.VirtualSoulfind.v2.Configuration.VirtualSoulfindOptions
                {
                    Enabled = enabled,
                    Backends = backendLimits,
                },
            });

        return new SourceProvidersController(backends, options.Object);
    }

    private static SourceProviderResponse AssertProvider(SourceProviderCatalogResponse response, string id)
    {
        return Assert.Single(response.Providers, provider => provider.Id == id);
    }
}
