using Daleel.Web.Logging;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Daleel.Web.Tests;

public class R2LoggingOptionsTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    [Fact]
    public void FromConfiguration_AllFieldsPresent_BuildsOptions()
    {
        var options = R2LoggingOptions.FromConfiguration(Config(
            ("R2_ACCESS_KEY", "ak"),
            ("R2_SECRET_KEY", "sk"),
            ("R2_BUCKET_NAME", "daleel-logs"),
            ("R2_ENDPOINT", "https://acc.r2.cloudflarestorage.com")));

        options.Should().NotBeNull();
        options!.AccessKey.Should().Be("ak");
        options.SecretKey.Should().Be("sk");
        options.BucketName.Should().Be("daleel-logs");
        options.ServiceUrl.Should().Be("https://acc.r2.cloudflarestorage.com");
    }

    [Fact]
    public void FromConfiguration_NoEndpoint_DerivesServiceUrlFromCloudflareAccountId()
    {
        var options = R2LoggingOptions.FromConfiguration(Config(
            ("R2_ACCESS_KEY", "ak"),
            ("R2_SECRET_KEY", "sk"),
            ("R2_BUCKET_NAME", "daleel-logs"),
            ("CLOUDFLARE_ACCOUNT_ID", "abc123")));

        options.Should().NotBeNull();
        options!.ServiceUrl.Should().Be("https://abc123.r2.cloudflarestorage.com");
    }

    [Fact]
    public void FromConfiguration_ExplicitEndpoint_WinsOverCloudflareAccountId()
    {
        var options = R2LoggingOptions.FromConfiguration(Config(
            ("R2_ACCESS_KEY", "ak"),
            ("R2_SECRET_KEY", "sk"),
            ("R2_BUCKET_NAME", "daleel-logs"),
            ("CLOUDFLARE_ACCOUNT_ID", "abc123"),
            ("R2_ENDPOINT", "https://custom.example.com")));

        options!.ServiceUrl.Should().Be("https://custom.example.com");
    }

    [Theory]
    [InlineData("R2_ACCESS_KEY")]
    [InlineData("R2_SECRET_KEY")]
    [InlineData("R2_BUCKET_NAME")]
    public void FromConfiguration_MissingRequiredCredential_ReturnsNull(string omit)
    {
        var pairs = new List<(string, string?)>
        {
            ("R2_ACCESS_KEY", "ak"),
            ("R2_SECRET_KEY", "sk"),
            ("R2_BUCKET_NAME", "daleel-logs"),
            ("R2_ENDPOINT", "https://acc.r2.cloudflarestorage.com"),
        };
        pairs.RemoveAll(p => p.Item1 == omit);

        R2LoggingOptions.FromConfiguration(Config(pairs.ToArray())).Should().BeNull();
    }

    [Fact]
    public void FromConfiguration_NoEndpointAndNoCloudflareAccountId_ReturnsNull()
    {
        R2LoggingOptions.FromConfiguration(Config(
            ("R2_ACCESS_KEY", "ak"),
            ("R2_SECRET_KEY", "sk"),
            ("R2_BUCKET_NAME", "daleel-logs"))).Should().BeNull();
    }

    [Fact]
    public void FromConfiguration_Empty_ReturnsNull()
    {
        R2LoggingOptions.FromConfiguration(Config()).Should().BeNull();
    }

    [Fact]
    public void FromConfiguration_WhitespaceValues_TreatedAsUnset()
    {
        R2LoggingOptions.FromConfiguration(Config(
            ("R2_ACCESS_KEY", "   "),
            ("R2_SECRET_KEY", "sk"),
            ("R2_BUCKET_NAME", "daleel-logs"),
            ("R2_ENDPOINT", "https://acc.r2.cloudflarestorage.com"))).Should().BeNull();
    }
}
