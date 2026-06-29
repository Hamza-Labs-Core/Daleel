using Daleel.Core.Models;
using Daleel.Web.Persistence;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests.Persistence;

/// <summary>
/// Pure mapping tests (no R2/DB): the projection from extracted models to self-contained
/// <see cref="Daleel.Core.Persistence.EntityDocument"/>s must embed every relation ID and pick the
/// right id/key per intent, since the R2 document is the source of truth and must read on its own.
/// </summary>
public class EntityDocumentMapperTests
{
    private static ProductModel Model() => new()
    {
        Name = "Gree Pular 24000",
        Brand = "Gree",
        Model = "Pular-24",
        ImageUrl = "https://img/gree.jpg",
        Specs = new Dictionary<string, string> { ["btu"] = "24000", ["energy"] = "A+" },
        Pros = new[] { "quiet" },
        Cons = new[] { "pricey" },
        ReviewSummary = "solid mid-range unit",
        Offers = new[]
        {
            new PriceOffer { Source = "Smart Buy", Price = 320, Currency = "JOD", Url = "https://sb/gree", Condition = "new" }
        }
    };

    private static ProductSearchResult Result(ProductModel m) => new()
    {
        Query = "best AC",
        Geo = "jordan",
        Country = "Jordan",
        Models = new[] { m }
    };

    [Fact]
    public void Product_EmbedsAllRelationIdsAndProductKey()
    {
        var m = Model();
        var doc = EntityDocumentMapper.ToDocument(m, SearchIntentType.Product, Result(m), "job_123", DateTimeOffset.UnixEpoch);

        doc.Intent.Should().Be(SearchIntentType.Product);
        doc.Id.Should().Be(StableId.ForEntity(SearchIntentType.Product, "Gree", "Pular-24", "Gree Pular 24000"));
        doc.Id.Should().StartWith("p_");

        // Embedded relation IDs — the document is self-contained.
        doc.SearchId.Should().Be("job_123");
        doc.BrandId.Should().Be(StableId.ForBrand("Gree"));
        doc.ProductKey.Should().Be(Daleel.Web.Data.ProductProfile.KeyFor("Gree", "Pular-24", "Gree Pular 24000"));
        // A product has many sellers, so no single store relation.
        doc.StoreId.Should().BeNull();

        // Content carried across.
        doc.Specs.Should().ContainKey("btu").WhoseValue.Should().Be("24000");
        doc.Offers.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Source = "Smart Buy", Price = 320m, Currency = "JOD", Condition = "new" });
        doc.Pros.Should().Contain("quiet");
        doc.Summary.Should().Be("solid mid-range unit");
        doc.Geo.Should().Be("jordan");

        // Deterministic R2 key.
        doc.ObjectKey.Should().Be($"entities/product/{doc.Id}.json");
    }

    [Fact]
    public void Service_UsesServiceIdAndStoreRelationNoProductKey()
    {
        var m = Model() with { Name = "Aqua Plumbing", Brand = null, Model = null };
        var doc = EntityDocumentMapper.ToDocument(m, SearchIntentType.Service, Result(m), "job_1", DateTimeOffset.UnixEpoch);

        doc.Intent.Should().Be(SearchIntentType.Service);
        doc.Id.Should().StartWith("sv_");
        doc.Id.Should().Be(StableId.ForService("Aqua Plumbing"));
        // A provider IS a single entity → carries a store relation; it is not a catalogue product.
        doc.StoreId.Should().Be(StableId.ForStore("Aqua Plumbing"));
        doc.ProductKey.Should().BeNull();
        doc.ObjectKey.Should().Be($"entities/service/{doc.Id}.json");
    }

    [Fact]
    public void Place_UsesPlaceIdAndStoreRelation()
    {
        var m = Model() with { Name = "Reem Al Bawadi", Brand = null, Model = null };
        var doc = EntityDocumentMapper.ToDocument(m, SearchIntentType.Place, Result(m), "job_1", DateTimeOffset.UnixEpoch);

        doc.Intent.Should().Be(SearchIntentType.Place);
        doc.Id.Should().StartWith("pl_");
        doc.StoreId.Should().Be(StableId.ForStore("Reem Al Bawadi"));
        doc.ProductKey.Should().BeNull();
        doc.ObjectKey.Should().Be($"entities/place/{doc.Id}.json");
    }

    [Fact]
    public void ToDocuments_MapsEveryModel()
    {
        var result = new ProductSearchResult
        {
            Geo = "jordan",
            Models = new[] { Model(), Model() with { Name = "LG Dual", Brand = "LG", Model = "DC-1" } }
        };

        var docs = EntityDocumentMapper.ToDocuments(result, SearchIntentType.Product, "s1", DateTimeOffset.UnixEpoch);
        docs.Should().HaveCount(2);
        docs.Select(d => d.Id).Should().OnlyHaveUniqueItems();
    }
}
