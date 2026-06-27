using Daleel.Web.Storage;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Daleel.Web.Tests;

public class R2OptionsTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    // The shared connection — credentials + a resolvable endpoint — is all that's required to be "configured".
    private static (string, string?)[] Connection() => new (string, string?)[]
    {
        ("R2_ACCESS_KEY", "ak"),
        ("R2_SECRET_KEY", "sk"),
        ("R2_ENDPOINT", "https://acc.r2.cloudflarestorage.com"),
    };

    [Fact]
    public void FromConfiguration_ConnectionOnly_DefaultsEveryBucket()
    {
        var options = R2Options.FromConfiguration(Config(Connection()));

        options.Should().NotBeNull();
        options!.AccessKey.Should().Be("ak");
        options.SecretKey.Should().Be("sk");
        options.ServiceUrl.Should().Be("https://acc.r2.cloudflarestorage.com");

        // Each concern gets its own conventional default bucket — the operator just creates these in Cloudflare.
        options.Logs.BucketName.Should().Be("daleel-logs");
        options.Images.BucketName.Should().Be("daleel-images");
        options.Specs.BucketName.Should().Be("daleel-specs");
        options.Data.BucketName.Should().Be("daleel-data");

        // No public hosts configured → none served publicly (logs never are).
        options.Logs.PublicUrl.Should().BeNull();
        options.Images.PublicUrl.Should().BeNull();
        options.Specs.PublicUrl.Should().BeNull();
        options.Data.PublicUrl.Should().BeNull();
    }

    [Fact]
    public void FromConfiguration_PerBucketNamesAndPublicUrls_AreHonoured()
    {
        var options = R2Options.FromConfiguration(Config(Connection().Concat(new (string, string?)[]
        {
            ("R2_BUCKET_LOGS", "my-logs"),
            ("R2_BUCKET_IMAGES", "my-images"),
            ("R2_BUCKET_SPECS", "my-specs"),
            ("R2_BUCKET_DATA", "my-data"),
            ("R2_PUBLIC_URL_IMAGES", "https://img.example.com"),
            ("R2_PUBLIC_URL_SPECS", "https://specs.example.com"),
            ("R2_PUBLIC_URL_DATA", "https://data.example.com"),
        }).ToArray()));

        options!.Logs.BucketName.Should().Be("my-logs");
        options.Images.BucketName.Should().Be("my-images");
        options.Specs.BucketName.Should().Be("my-specs");
        options.Data.BucketName.Should().Be("my-data");

        options.Images.PublicUrl.Should().Be("https://img.example.com");
        options.Specs.PublicUrl.Should().Be("https://specs.example.com");
        options.Data.PublicUrl.Should().Be("https://data.example.com");
        options.Logs.PublicUrl.Should().BeNull();
    }

    [Fact]
    public void FromConfiguration_LegacyVars_FallBackToLogsBucketAndImageHost()
    {
        // The old single-bucket setup: R2_BUCKET_NAME held the logs bucket, R2_PUBLIC_URL the image host.
        var options = R2Options.FromConfiguration(Config(Connection().Concat(new (string, string?)[]
        {
            ("R2_BUCKET_NAME", "daleel-logs-legacy"),
            ("R2_PUBLIC_URL", "https://legacy-images.example.com"),
        }).ToArray()));

        options!.Logs.BucketName.Should().Be("daleel-logs-legacy");
        options.Images.PublicUrl.Should().Be("https://legacy-images.example.com");
        // The other concerns still default — the legacy vars only seeded logs + images.
        options.Specs.BucketName.Should().Be("daleel-specs");
        options.Data.BucketName.Should().Be("daleel-data");
    }

    [Fact]
    public void FromConfiguration_PerBucketVarsWinOverLegacy()
    {
        var options = R2Options.FromConfiguration(Config(Connection().Concat(new (string, string?)[]
        {
            ("R2_BUCKET_NAME", "legacy-logs"),
            ("R2_BUCKET_LOGS", "explicit-logs"),
            ("R2_PUBLIC_URL", "https://legacy.example.com"),
            ("R2_PUBLIC_URL_IMAGES", "https://explicit.example.com"),
        }).ToArray()));

        options!.Logs.BucketName.Should().Be("explicit-logs");
        options.Images.PublicUrl.Should().Be("https://explicit.example.com");
    }

    [Fact]
    public void FromConfiguration_NoEndpoint_DerivesServiceUrlFromCloudflareAccountId()
    {
        var options = R2Options.FromConfiguration(Config(
            ("R2_ACCESS_KEY", "ak"),
            ("R2_SECRET_KEY", "sk"),
            ("CLOUDFLARE_ACCOUNT_ID", "abc123")));

        options!.ServiceUrl.Should().Be("https://abc123.r2.cloudflarestorage.com");
    }

    [Fact]
    public void FromConfiguration_ExplicitEndpoint_WinsOverCloudflareAccountId()
    {
        var options = R2Options.FromConfiguration(Config(
            ("R2_ACCESS_KEY", "ak"),
            ("R2_SECRET_KEY", "sk"),
            ("CLOUDFLARE_ACCOUNT_ID", "abc123"),
            ("R2_ENDPOINT", "https://custom.example.com")));

        options!.ServiceUrl.Should().Be("https://custom.example.com");
    }

    [Theory]
    [InlineData("R2_ACCESS_KEY")]
    [InlineData("R2_SECRET_KEY")]
    [InlineData("R2_ENDPOINT")]
    public void FromConfiguration_MissingConnectionPart_ReturnsNull(string omit)
    {
        var pairs = Connection().ToList();
        pairs.RemoveAll(p => p.Item1 == omit);

        R2Options.FromConfiguration(Config(pairs.ToArray())).Should().BeNull();
    }

    [Fact]
    public void FromConfiguration_MissingBucketName_StillConfigured_BecauseBucketsDefault()
    {
        // Unlike the old contract, a bucket name is NOT required — every bucket defaults.
        R2Options.FromConfiguration(Config(Connection())).Should().NotBeNull();
    }

    [Fact]
    public void FromConfiguration_NoEndpointAndNoCloudflareAccountId_ReturnsNull()
    {
        R2Options.FromConfiguration(Config(
            ("R2_ACCESS_KEY", "ak"),
            ("R2_SECRET_KEY", "sk"))).Should().BeNull();
    }

    [Fact]
    public void FromConfiguration_Empty_ReturnsNull()
    {
        R2Options.FromConfiguration(Config()).Should().BeNull();
    }

    [Fact]
    public void FromConfiguration_WhitespaceValues_TreatedAsUnset()
    {
        R2Options.FromConfiguration(Config(
            ("R2_ACCESS_KEY", "   "),
            ("R2_SECRET_KEY", "sk"),
            ("R2_ENDPOINT", "https://acc.r2.cloudflarestorage.com"))).Should().BeNull();
    }

    [Theory]
    [InlineData("R2_ACCESS_KEY")]
    [InlineData("R2_SECRET_KEY")]
    [InlineData("CLOUDFLARE_ACCOUNT_ID")]
    public void FromConfiguration_ChangeMePlaceholder_TreatedAsUnset(string placeholderKey)
    {
        // create-secrets.sh seeds secrets with "CHANGE_ME"; a half-provisioned R2 setup must read as
        // "not configured" so the app falls back to file logging instead of dialling R2 with bogus creds.
        var pairs = new List<(string, string?)>
        {
            ("R2_ACCESS_KEY", "ak"),
            ("R2_SECRET_KEY", "sk"),
            ("CLOUDFLARE_ACCOUNT_ID", "abc123"),
        };
        var idx = pairs.FindIndex(p => p.Item1 == placeholderKey);
        pairs[idx] = (placeholderKey, "CHANGE_ME");

        R2Options.FromConfiguration(Config(pairs.ToArray())).Should().BeNull();
    }

    [Fact]
    public void For_ReturnsTheConfigForEachConcern()
    {
        var options = new R2Options("ak", "sk", "https://acc.r2.cloudflarestorage.com",
            new R2BucketConfig("logs-b", null),
            new R2BucketConfig("img-b", "https://img"),
            new R2BucketConfig("spec-b", "https://spec"),
            new R2BucketConfig("data-b", "https://data"));

        options.For(R2Bucket.Logs).BucketName.Should().Be("logs-b");
        options.For(R2Bucket.Images).BucketName.Should().Be("img-b");
        options.For(R2Bucket.Specs).BucketName.Should().Be("spec-b");
        options.For(R2Bucket.Data).BucketName.Should().Be("data-b");
    }
}
