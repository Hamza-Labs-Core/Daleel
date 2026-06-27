using Amazon.S3;
using Amazon.S3.Model;
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
    public async Task StoreImage_WithoutPublicHost_HotLinksOriginal_NeverRewritesToS3Endpoint()
    {
        // No R2_PUBLIC_URL: rewriting to "{serviceUrl}/{bucket}/{key}" would 403 a plain <img> GET, so the
        // service must hand back the original URL unchanged (and never touch the network) instead.
        var s3 = new AmazonS3Client("ak", "sk", new AmazonS3Config
        {
            ServiceURL = "https://acc.r2.cloudflarestorage.com",
            ForcePathStyle = true
        });
        using var svc = new R2StorageService(s3, new HttpClient(), "bucket", publicBaseUrl: "",
            NullLogger<R2StorageService>.Instance);

        var source = "https://cdn.store.com/img/galaxy-s24.png";
        (await svc.StoreImageAsync(source, "products")).Should().Be(source);
        (await svc.StoreJsonAsync("{}", "site-data/x.json")).Should().BeNull();
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

    [Fact]
    public async Task NullService_DataViewerMethods_AreEmptyAndNull()
    {
        var svc = new NullR2StorageService();
        (await svc.ListObjectsAsync("site-data/")).Should().BeSameAs(R2Listing.Empty);
        (await svc.ReadTextAsync("site-data/x.json")).Should().BeNull();
        svc.DownloadUrl("site-data/x.json").Should().BeNull();
    }

    [Fact]
    public void DownloadUrl_OnConfiguredService_IsPresignedAndScopedToKey()
    {
        using var svc = MakeService();
        var url = svc.DownloadUrl("brand-data/acme/logo.png");

        url.Should().NotBeNull();
        url!.Should().Contain("brand-data/acme/logo.png")
            .And.Contain("X-Amz-Signature"); // SigV4 query-string presign
    }

    [Fact]
    public void MapListing_FoldsCommonPrefixesAndDropsTheFolderMarkerObject()
    {
        var lastMod = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var response = new ListObjectsV2Response
        {
            // The zero-byte object whose key equals the prefix is R2's "folder marker" — it must not
            // appear as a file in the listing.
            S3Objects = new List<S3Object>
            {
                new() { Key = "site-data/", Size = 0, LastModified = lastMod },
                new() { Key = "site-data/specs.json", Size = 2048, LastModified = lastMod },
            },
            CommonPrefixes = new List<string> { "site-data/lg/", "site-data/samsung/" },
            IsTruncated = true,
            NextContinuationToken = "next-page"
        };

        var listing = R2StorageService.MapListing(response, "site-data/");

        listing.Prefixes.Should().ContainInOrder("site-data/lg/", "site-data/samsung/");
        listing.Objects.Should().ContainSingle();
        listing.Objects[0].Key.Should().Be("site-data/specs.json");
        listing.Objects[0].Size.Should().Be(2048);
        listing.Objects[0].LastModified.Offset.Should().Be(TimeSpan.Zero); // pinned to UTC
        listing.NextContinuationToken.Should().Be("next-page");
    }

    [Fact]
    public void MapListing_WhenNotTruncated_HasNoContinuationToken()
    {
        var response = new ListObjectsV2Response { IsTruncated = false, NextContinuationToken = "ignored" };
        R2StorageService.MapListing(response, null).NextContinuationToken.Should().BeNull();
    }
}
