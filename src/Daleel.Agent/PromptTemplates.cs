using System.Text;
using Daleel.Core.Geo;

namespace Daleel.Agent;

/// <summary>
/// Centralized, Arabic-aware prompt builders for the agent's two LLM roles: the
/// <em>planner</em> (turn a query into a bilingual search strategy) and the
/// <em>analyst</em> (turn collected results into a report).
/// </summary>
/// <remarks>
/// Every planning prompt instructs the model to generate queries in BOTH the market's
/// primary language and English, because the best local results (forums, classifieds,
/// Facebook groups) are usually Arabic while spec sheets and reviews are often English.
/// </remarks>
public static class PromptTemplates
{
    /// <summary>System prompt shared by all planning calls.</summary>
    public const string PlannerSystem =
        "You are Daleel, an Arabic-first market-intelligence research planner. Given a user " +
        "question and a target market, you produce a concrete, bilingual search strategy. You " +
        "know the Arab world's local platforms (OpenSooq, Haraj, Dubizzle, Talabat, Carrefour, " +
        "local Facebook groups) and dialects. You ALWAYS reply with a single JSON object only.";

    /// <summary>System prompt shared by all analysis/synthesis calls.</summary>
    public const string AnalystSystem =
        "You are Daleel, an Arabic-and-English market-intelligence analyst. You read search " +
        "results, social posts, store listings, and reviews, then write clear, decision-ready " +
        "intelligence. You cite concrete prices, store names, and sentiment. Be concise and honest " +
        "about uncertainty.";

    private static string MarketContext(GeoProfile geo)
    {
        var sb = new StringBuilder();
        sb.Append("Market: ").Append(geo.Country).Append(" (").Append(geo.CountryCode).AppendLine(").");
        sb.Append("Languages (priority order): ").AppendLine(string.Join(", ", geo.Languages));
        sb.Append("Currency: ").AppendLine(geo.Currency);
        sb.Append("Key social platforms: ").AppendLine(string.Join(", ", geo.SocialPlatforms));
        if (geo.Marketplaces.Count > 0)
        {
            sb.Append("Local marketplaces: ").AppendLine(string.Join(", ", geo.Marketplaces));
        }
        sb.Append("Main city: ").Append(geo.CenterCity).AppendLine(".");
        return sb.ToString();
    }

    /// <summary>The JSON shape every planning prompt asks for.</summary>
    private const string StrategySchema = """
        Reply with exactly this JSON object:
        {
          "queryType": "ProductResearch|BrandLookup|StoreFinder|DealHunter|OpinionAggregation|Comparison|General",
          "subject": "the product/brand/topic",
          "webQueries": ["bilingual web search strings"],
          "shoppingQueries": ["shopping/marketplace search strings"],
          "socialQueries": ["social platform keywords"],
          "placesQueries": ["store-finder queries, e.g. 'AC stores', 'متاجر مكيفات'"],
          "urlsToRead": ["specific URLs worth deep-reading, may be empty"],
          "reasoning": "one sentence on the plan"
        }
        Generate queries in BOTH the market's primary language and English. No prose outside the JSON.
        """;

    /// <summary>Plan a free-form natural-language question.</summary>
    public static string PlanFreeform(string question, GeoProfile geo)
    {
        var sb = new StringBuilder();
        sb.AppendLine(MarketContext(geo));
        sb.Append("User question: ").AppendLine(question);
        sb.AppendLine();
        sb.AppendLine("Classify the question and design the search strategy.");
        sb.AppendLine(StrategySchema);
        return sb.ToString();
    }

    /// <summary>Plan a brand-intelligence sweep.</summary>
    public static string PlanBrand(string brand, GeoProfile geo)
    {
        var sb = new StringBuilder();
        sb.AppendLine(MarketContext(geo));
        sb.Append("Research the brand \"").Append(brand).AppendLine("\" in this market:");
        sb.AppendLine("brand presence, official + reseller stores, current deals/promotions, social sentiment, and competitors.");
        sb.AppendLine(StrategySchema);
        return sb.ToString();
    }

    /// <summary>Plan a store-finder query for a product/brand.</summary>
    public static string PlanStores(string subject, GeoProfile geo)
    {
        var sb = new StringBuilder();
        sb.AppendLine(MarketContext(geo));
        sb.Append("Find places to buy \"").Append(subject).AppendLine("\" in this market (physical + online).");
        sb.AppendLine("Emphasize placesQueries (store-type searches) in both languages.");
        sb.AppendLine(StrategySchema);
        return sb.ToString();
    }

    /// <summary>Plan a deal-hunting query.</summary>
    public static string PlanDeals(string subject, GeoProfile geo)
    {
        var sb = new StringBuilder();
        sb.AppendLine(MarketContext(geo));
        sb.Append("Find current deals, discounts, and promotions for \"").Append(subject).AppendLine("\".");
        sb.AppendLine("Include local promo terms (عروض, تخفيضات, خصم) and retailer sale pages.");
        sb.AppendLine(StrategySchema);
        return sb.ToString();
    }

    /// <summary>Plan a product-category research query (e.g. "best AC in Jordan").</summary>
    public static string PlanProduct(string category, GeoProfile geo)
    {
        var sb = new StringBuilder();
        sb.AppendLine(MarketContext(geo));
        sb.Append("Research the best \"").Append(category).AppendLine("\" options in this market.");
        sb.AppendLine("Cover: top models/brands, customer opinions on forums + social, prices, and where to buy.");
        sb.AppendLine(StrategySchema);
        return sb.ToString();
    }

    /// <summary>Build the analyst prompt that turns gathered context into a final report.</summary>
    public static string Analyze(string task, GeoProfile geo, string gatheredContext)
    {
        var sb = new StringBuilder();
        sb.AppendLine(MarketContext(geo));
        sb.Append("Task: ").AppendLine(task);
        sb.AppendLine();
        sb.AppendLine("Gathered research context (search results, listings, opinions, store data):");
        sb.AppendLine("----------------------------------------");
        sb.AppendLine(gatheredContext);
        sb.AppendLine("----------------------------------------");
        sb.AppendLine();
        sb.AppendLine("Write a clear, decision-ready summary answering the task. Reference concrete prices, " +
                      "store names, and customer sentiment where the context supports it. If the context is " +
                      "thin, say so rather than inventing specifics.");
        return sb.ToString();
    }
}
