using System.Text.Json.Serialization;
using Daleel.Core.Llm;
using Daleel.Web.Data;

namespace Daleel.Web.Profiles;

/// <summary>
/// Turns gathered research context into a structured <see cref="Brand"/> / <see cref="Store"/>
/// profile via the LLM. Pure synthesis (no network of its own) so it's unit-testable with a fake
/// <see cref="ILlmClient"/>. Follows the codebase's standard "ask for JSON, extract + deserialize"
/// pattern (<see cref="LlmJson"/>) rather than a provider-specific structured-output mode.
/// </summary>
public sealed class ProfileSynthesizer
{
    private const string BrandSystem =
        "You are a market analyst. From the supplied research context (and your own knowledge), " +
        "produce a concise, factual brand profile. Respond with ONLY a JSON object matching the " +
        "requested schema — no prose, no markdown fences. Use null/empty when unsure; never invent " +
        "specific facts you have no basis for.";

    private const string StoreSystem =
        "You are a retail analyst. From the supplied research context (and your own knowledge), " +
        "produce a concise, factual store/retailer profile. Respond with ONLY a JSON object matching " +
        "the requested schema — no prose, no markdown fences.";

    private readonly ILlmClient _llm;

    public ProfileSynthesizer(ILlmClient llm) => _llm = llm;

    public async Task<Brand> SynthesizeBrandAsync(
        string brandName, string researchContext, CancellationToken ct = default)
    {
        var prompt =
            $"Brand: {brandName}\n\n" +
            "Schema: { \"countryOfOrigin\": string, \"reputationScore\": number (0-10), " +
            "\"description\": string, \"pros\": string[], \"cons\": string[], " +
            "\"popularModels\": string[], \"priceRange\": string, \"website\": string }\n\n" +
            "Research context:\n" + Trim(researchContext);

        var text = await _llm.CompleteTextAsync(BrandSystem, prompt, ct).ConfigureAwait(false);
        return (LlmJson.Deserialize<BrandDto>(text) ?? new BrandDto()).ToBrand(brandName);
    }

    public async Task<Store> SynthesizeStoreAsync(
        string storeName, string researchContext, CancellationToken ct = default)
    {
        var prompt =
            $"Store: {storeName}\n\n" +
            "Schema: { \"location\": string, \"type\": string, \"brandsCarried\": string[], " +
            "\"rating\": number (0-5), \"website\": string, \"phone\": string, \"email\": string, " +
            "\"address\": string }\n\n" +
            "Extract phone/email/address only if explicitly present in the context; never invent them.\n\n" +
            "Research context:\n" + Trim(researchContext);

        var text = await _llm.CompleteTextAsync(StoreSystem, prompt, ct).ConfigureAwait(false);
        return (LlmJson.Deserialize<StoreDto>(text) ?? new StoreDto()).ToStore(storeName);
    }

    private static string Trim(string s) => s.Length <= 8000 ? s : s[..8000];

    private sealed class BrandDto
    {
        [JsonPropertyName("countryOfOrigin")] public string? CountryOfOrigin { get; set; }
        [JsonPropertyName("reputationScore")] public double? ReputationScore { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("pros")] public List<string>? Pros { get; set; }
        [JsonPropertyName("cons")] public List<string>? Cons { get; set; }
        [JsonPropertyName("popularModels")] public List<string>? PopularModels { get; set; }
        [JsonPropertyName("priceRange")] public string? PriceRange { get; set; }
        [JsonPropertyName("website")] public string? Website { get; set; }

        public Brand ToBrand(string name) => new()
        {
            Name = name,
            CountryOfOrigin = CountryOfOrigin,
            ReputationScore = ReputationScore,
            Description = Description,
            Pros = Pros ?? new List<string>(),
            Cons = Cons ?? new List<string>(),
            PopularModels = PopularModels ?? new List<string>(),
            PriceRange = PriceRange,
            Website = Website
        };
    }

    private sealed class StoreDto
    {
        [JsonPropertyName("location")] public string? Location { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("brandsCarried")] public List<string>? BrandsCarried { get; set; }
        [JsonPropertyName("rating")] public double? Rating { get; set; }
        [JsonPropertyName("website")] public string? Website { get; set; }
        [JsonPropertyName("phone")] public string? Phone { get; set; }
        [JsonPropertyName("email")] public string? Email { get; set; }
        [JsonPropertyName("address")] public string? Address { get; set; }

        public Store ToStore(string name) => new()
        {
            Name = name,
            Location = Location,
            Type = Type,
            BrandsCarried = BrandsCarried ?? new List<string>(),
            Rating = Rating,
            Website = Website,
            Phone = Phone,
            Email = Email,
            Address = Address
        };
    }
}
