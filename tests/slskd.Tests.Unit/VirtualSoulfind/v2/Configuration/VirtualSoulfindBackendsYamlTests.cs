// <copyright file="VirtualSoulfindBackendsYamlTests.cs" company="slskdN Team">
//     Copyright (c) slskdN Team. All rights reserved.
// </copyright>
namespace slskd.Tests.Unit.VirtualSoulfind.v2.Configuration;

using System;
using System.IO;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using slskd;
using slskd.Configuration;
using slskd.Core.API;
using slskd.VirtualSoulfind.v2.Backends;
using slskd.VirtualSoulfind.v2.Configuration;
using Xunit;

/// <summary>
///     Verifies the exact yaml keys the Source Providers UI writes under
///     <c>virtual_soulfind_v2.backends</c> bind through both configuration paths:
///     <c>FromYaml&lt;Options&gt;()</c> (save-time validation) and the <c>IConfiguration</c> yaml source (runtime binding).
/// </summary>
public class VirtualSoulfindBackendsYamlTests
{
    private const string Yaml = """
        virtual_soulfind_v2:
          enabled: true
          backends:
            local_library:
              enabled: false
            soulseek:
              enabled: false
              max_searches_per_minute: 7
            native_mesh:
              enabled: true
              min_trust_score: 0.6
              max_candidates_per_item: 5
            mesh_dht:
              enabled: true
              min_trust_score: 0.7
            http:
              enabled: true
              domain_allowlist: [example.org, cdn.example.net]
              max_file_size_bytes: 1234
              validation_timeout_seconds: 3
              require_https: true
            webdav:
              enabled: true
              domain_allowlist: [dav.example.org]
              username: alice
              password: s3cret
              bearer_token: tok
            s3:
              enabled: true
              endpoint: "http://minio:9000"
              region: eu-west-1
              access_key: ak
              secret_key: sk
              force_path_style: false
              bucket_allowlist: [music]
              max_file_size_bytes: 99
              validation_timeout_seconds: 4
            lan:
              enabled: true
              allowed_networks: [192.168.1.0/24]
              allowed_hostnames: [nas]
              max_candidates_per_item: 2
            torrent:
              enabled: true
              min_seeders: 4
              invite_list: ["peer.example:6881"]
        """;

    [Fact]
    public void FrontendKeys_DeserializeViaFromYaml()
    {
        var options = Yaml.FromYaml<Options>();

        AssertBound(options.VirtualSoulfindV2.Backends);
    }

    /// <summary>
    ///     The Settings dialog saves the whole slskd.yml; legacy keys in it (here <c>integration</c>) make the
    ///     save-time validator rewrite the document before it deserializes it.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("integration: {}\n")]
    public void RequireHttpsAsWrittenByUi_PassesSaveTimeNormalization(string legacyPrefix)
    {
        var yaml = legacyPrefix + """
            virtual_soulfind_v2:
              backends:
                http:
                  enabled: true
                  domain_allowlist:
                    - files.example.org
                  require_https: true
            """;
        var normalize = typeof(OptionsController).GetMethod("NormalizeCompatibilityYaml", BindingFlags.NonPublic | BindingFlags.Static);

        var options = ((string)normalize!.Invoke(null, new object[] { yaml })!).FromYaml<Options>();

        Assert.True(options.VirtualSoulfindV2.Backends.Http.RequireHttps);
        Assert.Equal(new[] { "files.example.org" }, options.VirtualSoulfindV2.Backends.Http.DomainAllowlist);
    }

    [Fact]
    public void FrontendKeys_BindViaYamlConfigurationSource()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "yaml-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var path = Path.Combine(directory, "slskd.yml");
            File.WriteAllText(path, Yaml);

            using var provider = new PhysicalFileProvider(directory);
            var configuration = new ConfigurationBuilder()
                .AddYamlFile(path: Path.GetFileName(path), targetType: typeof(Options), optional: false, provider: provider)
                .Build();

            var options = new Options();
            configuration.GetSection("slskd").Bind(options, o => o.BindNonPublicProperties = true);

            AssertBound(options.VirtualSoulfindV2.Backends);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertBound(BackendLimits backends)
    {
        Assert.False(backends.LocalLibrary.Enabled);

        Assert.False(backends.Soulseek.Enabled);
        Assert.Equal(7, backends.Soulseek.MaxSearchesPerMinute);

        Assert.True(backends.NativeMesh.Enabled);
        Assert.Equal(0.6f, backends.NativeMesh.MinTrustScore, 3);
        Assert.Equal(5, backends.NativeMesh.MaxCandidatesPerItem);

        Assert.True(backends.MeshDht.Enabled);
        Assert.Equal(0.7f, backends.MeshDht.MinTrustScore, 3);

        Assert.True(backends.Http.Enabled);
        Assert.Equal(new[] { "example.org", "cdn.example.net" }, backends.Http.DomainAllowlist);
        Assert.Equal(1234, backends.Http.MaxFileSizeBytes);
        Assert.Equal(3, backends.Http.ValidationTimeoutSeconds);
        Assert.True(backends.Http.RequireHttps);

        Assert.True(backends.WebDav.Enabled);
        Assert.Equal(new[] { "dav.example.org" }, backends.WebDav.DomainAllowlist);
        Assert.Equal("alice", backends.WebDav.Username);
        Assert.Equal("s3cret", backends.WebDav.Password);
        Assert.Equal("tok", backends.WebDav.BearerToken);

        Assert.True(backends.S3.Enabled);
        Assert.Equal("http://minio:9000", backends.S3.Endpoint);
        Assert.Equal("eu-west-1", backends.S3.Region);
        Assert.Equal("ak", backends.S3.AccessKey);
        Assert.Equal("sk", backends.S3.SecretKey);
        Assert.False(backends.S3.ForcePathStyle);
        Assert.Equal(new[] { "music" }, backends.S3.BucketAllowlist);
        Assert.Equal(99, backends.S3.MaxFileSizeBytes);
        Assert.Equal(4, backends.S3.ValidationTimeoutSeconds);

        Assert.True(backends.Lan.Enabled);
        Assert.Equal(new[] { "192.168.1.0/24" }, backends.Lan.AllowedNetworks);
        Assert.Equal(new[] { "nas" }, backends.Lan.AllowedHostnames);
        Assert.Equal(2, backends.Lan.MaxCandidatesPerItem);

        Assert.True(backends.Torrent.Enabled);
        Assert.Equal(4, backends.Torrent.MinSeeders);
        Assert.Equal(new[] { "peer.example:6881" }, backends.Torrent.InviteList);
    }
}
