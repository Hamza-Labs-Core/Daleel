using System.Net;
using Daleel.Core.Geo;
using Daleel.Search.Providers;
using FluentAssertions;
using Xunit;

namespace Daleel.Search.Tests;

public class GooglePlacesProviderTests
{
    [Fact]
    public void Haversine_AmmanToZero_IsRoughlyKnownDistance()
    {
        var amman = new GeoPoint(31.9539, 35.9106);
        var alsoAmman = new GeoPoint(31.9539, 35.9106);
        GooglePlacesProvider.Haversine(amman, alsoAmman).Should().Be(0);
    }

    [Fact]
    public void Haversine_KnownCityPair_IsApproximatelyCorrect()
    {
        // Amman → Dubai is ~2000 km.
        var amman = new GeoPoint(31.9539, 35.9106);
        var dubai = new GeoPoint(25.2048, 55.2708);

        var meters = GooglePlacesProvider.Haversine(amman, dubai);
        (meters / 1000).Should().BeApproximately(1900, 200);
    }

    [Fact]
    public async Task SearchStoresAsync_ParsesPlacesAndComputesDistance()
    {
        const string json = """
        {
          "places": [
            {
              "id": "abc123",
              "displayName": { "text": "متجر المكيفات" },
              "formattedAddress": "Amman, Jordan",
              "internationalPhoneNumber": "+962 6 000 0000",
              "websiteUri": "https://store.jo",
              "location": { "latitude": 31.96, "longitude": 35.91 },
              "googleMapsUri": "https://maps.google.com/?cid=1",
              "rating": 4.6,
              "userRatingCount": 120,
              "priceLevel": "PRICE_LEVEL_MODERATE",
              "reviews": [
                { "rating": 5, "text": { "text": "ممتاز", "languageCode": "ar" },
                  "authorAttribution": { "displayName": "Ahmad" }, "publishTime": "2026-01-01T00:00:00Z" }
              ]
            }
          ]
        }
        """;

        var handler = new StubHttpMessageHandler(json);
        var provider = new GooglePlacesProvider(
            apiKey: "test-key",
            httpClient: handler.Client(GooglePlacesProvider.DefaultBaseUrl),
            delay: (_, _) => Task.CompletedTask);

        var stores = await provider.SearchStoresAsync("مكيفات", new GeoPoint(31.95, 35.91));

        stores.Should().ContainSingle();
        var store = stores[0];
        store.Name.Should().Be("متجر المكيفات");
        store.Phone.Should().Be("+962 6 000 0000");
        store.Rating.Should().Be(4.6);
        store.ReviewCount.Should().Be(120);
        store.PriceLevel.Should().Be(2);
        store.DistanceMeters.Should().NotBeNull();
        store.Reviews.Should().ContainSingle();
        store.Reviews[0].Text.Should().Be("ممتاز");
        store.Reviews[0].Author.Should().Be("Ahmad");
    }

    [Fact]
    public async Task SearchStoresAsync_SendsApiKeyAndFieldMaskHeaders()
    {
        var handler = new StubHttpMessageHandler("""{"places":[]}""");
        var provider = new GooglePlacesProvider(
            apiKey: "secret-key",
            httpClient: handler.Client(GooglePlacesProvider.DefaultBaseUrl),
            delay: (_, _) => Task.CompletedTask);

        await provider.SearchStoresAsync("x", new GeoPoint(0, 0));

        var req = handler.Requests.Should().ContainSingle().Subject;
        req.Headers.GetValues("X-Goog-Api-Key").Should().Contain("secret-key");
        req.Headers.Contains("X-Goog-FieldMask").Should().BeTrue();
    }
}
