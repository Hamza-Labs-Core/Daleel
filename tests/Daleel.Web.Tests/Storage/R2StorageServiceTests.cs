using Amazon.S3;
using Daleel.Web.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Daleel.Web.Tests.Storage;

/// <summary>
/// The image-storage contract is "always hand back a usable URL". These tests pin the graceful-degradation
/// paths that don't need a live R2/network: the no-op service, and the guard branches that short-circuit to
/// the original URL (empty/invalid/non-http inputs, and URLs we already host). They also lock the object-key
/// scheme, which must be deterministic so re-uploads overwrite rather than duplicate.
/// </summary>
public class R2StorageServiceTests
{
    private const string PublicBase = "https://images.daleel.app";

    private static R2StorageService MakeService()
    {
        // Real S3 client with throwaway creds: the tests below never reach a PutObject, so it's never called.
        var s3 = new AmazonS3Client("ak", "sk", new AmazonS3Config
        {
            ServiceURL = "https://acc.r2.cloudflarestorage.com",
            ForcePathStyle = true
        });
        return new R2StorageService(s3, new HttpClient(), "bucket", PublicBase,
            NullLogger<R2StorageService>.Instance);
    }

    [Fact]
    public async Task NullService_ReportsUnconfigured_AndEchoesInput()
    {
        var svc = new NullR2StorageService();
        svc.IsConfigured.Should().BeFalse();
        (await svc.StoreImageAsync("https://x/y.jpg", "products")).Should().Be("https://x/y.jpg");
        (await svc.StoreImageAsync(null, "products")).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("ftp://host/file.jpg")]
    public async Task StoreImage_ReturnsInputUnchanged_ForUnusableUrls(string? input)
    {
        using var svc = MakeService();
        (await svc.StoreImageAsync(input, "products")).Should().Be(input);
    }

    [Fact]
    public async Task StoreImage_SkipsUrlsWeAlreadyHost()
    {
        using var svc = MakeService();
        var already = $"{PublicBase}/products/abc123.jpg";
        (await svc.StoreImageAsync(already, "products")).Should().Be(already);
    }

    [Fact]
    public void BuildKey_IsDeterministicAndPreservesExtension()
    {
        var uri = new Uri("https://cdn.store.com/img/galaxy-s24.png?v=2");
        var k1 = R2StorageService.BuildKey("products", uri.ToString(), uri);
        var k2 = R2StorageService.BuildKey("products", uri.ToString(), uri);

        k1.Should().Be(k2, "the same source URL must always map to the same object");
        k1.Should().StartWith("products/").And.EndWith(".png");
    }

    [Fact]
    public void BuildKey_FallsBackToJpgWhenNoUsableExtension()
    {
        var uri = new Uri("https://cdn.store.com/image-handler");
        R2StorageService.BuildKey("brands/7", uri.ToString(), uri).Should().StartWith("brands/7/").And.EndWith(".jpg");
    }

    [Fact]
    public void BuildKey_DistinctUrlsProduceDistinctKeys()
    {
        var a = new Uri("https://cdn/a.jpg");
        var b = new Uri("https://cdn/b.jpg");
        R2StorageService.BuildKey("p", a.ToString(), a).Should().NotBe(R2StorageService.BuildKey("p", b.ToString(), b));
    }
}
