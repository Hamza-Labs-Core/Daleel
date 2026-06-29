namespace Daleel.Core.Models;

/// <summary>
/// What KIND of thing the user is ultimately after — orthogonal to <see cref="QueryType"/>, which
/// describes the task <em>shape</em> (research, lookup, comparison…). Intent decides how the
/// extraction pass reads the gathered context: a phone is a product with prices and specs, a
/// plumber is a service with tiers and contact details, a restaurant is a place with hours and a
/// map pin. The planner classifies this up-front; the extractor picks its prompt from it.
/// </summary>
/// <remarks>
/// All three intents reuse the existing structured shapes — <see cref="ProductModel"/> (its
/// <c>Specs</c> dictionary is free-form, so it carries service tiers / place attributes just as
/// well as product specs) and <c>StoreInfo</c> (already has lat/lng, rating, reviews, address,
/// phone). So adding an intent never requires a new result type, only a different extraction prompt.
/// </remarks>
public enum SearchIntentType
{
    /// <summary>A buyable item: "best AC in Jordan", "iPhone 15 price". Price, specs, seller links, images.</summary>
    Product,

    /// <summary>A service to hire: "plumber in Amman", "house cleaning". Provider, pricing tiers, reviews, availability, contact.</summary>
    Service,

    /// <summary>A physical place to visit: "best shawarma in Amman", "gyms near me". Location, hours, reviews, photos, contact, map.</summary>
    Place
}
