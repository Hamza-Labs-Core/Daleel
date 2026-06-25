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
    /// <summary>
    /// Halal-compliance guard appended to every system prompt. The deterministic
    /// <c>ContentFilter</c> is the real enforcement layer; this simply asks the model to avoid
    /// generating the content in the first place, so the filter rarely has to remove anything.
    /// </summary>
    public const string HalalGuard =
        " IMPORTANT: Only include halal-compliant results. Exclude any products, stores, or content " +
        "related to: alcohol, pork/non-halal meat, gambling, adult content, tobacco, or interest-based " +
        "financial products (riba). If a result contains any of these, skip it entirely.";

    /// <summary>System prompt shared by all planning calls.</summary>
    public const string PlannerSystem =
        "You are Daleel, an Arabic-first market-intelligence research planner. Given a user " +
        "question and a target market, you produce a concrete, bilingual search strategy. You " +
        "know the Arab world's local platforms (OpenSooq, Haraj, Dubizzle, Talabat, Carrefour, " +
        "local Facebook groups) and dialects. You ALWAYS reply with a single JSON object only." +
        HalalGuard;

    /// <summary>System prompt shared by all analysis/synthesis calls.</summary>
    public const string AnalystSystem =
        "You are Daleel, an Arabic-and-English market-intelligence analyst. You read search " +
        "results, social posts, store listings, and reviews, then write clear, decision-ready " +
        "intelligence. You cite concrete prices, store names, and sentiment. Be concise and honest " +
        "about uncertainty." +
        HalalGuard;

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
    /// <remarks>
    /// Source discovery is intentionally left to the search engine: it already knows which
    /// sites rank for a query in a given <c>gl</c>/<c>hl</c>, so we never name specific stores
    /// or marketplaces. The plan just describes the <em>shape</em> of searches to run.
    /// </remarks>
    public static string PlanProduct(string category, GeoProfile geo)
    {
        var sb = new StringBuilder();
        sb.AppendLine(MarketContext(geo));
        sb.Append("The user wants to BUY \"").Append(category).Append("\" in ").Append(geo.Country)
          .AppendLine(" — they need actual, purchasable listings with prices and links, not just advice.");
        sb.AppendLine("Design the search to surface CONCRETE LISTINGS from whatever local sites rank in this market:");
        sb.AppendLine("- shoppingQueries: price/marketplace-style queries in both the market language and English " +
            "(e.g. the category + 'للبيع' / 'price').");
        sb.AppendLine("- webQueries: a mix of local classifieds/marketplace pages, brand catalog pages, store " +
            "sections, and one or two buying-guide/review queries.");
        sb.AppendLine("- placesQueries: physical stores selling it near the main city, in both languages.");
        sb.AppendLine("- socialQueries / webQueries: also surface REAL user experiences — include social- and " +
            "forum-scoped queries and opinion phrasing (e.g. 'site:facebook.com', 'تجربتي مع', 'رأي', 'مشاكل').");
        sb.AppendLine("Do NOT assume which stores exist — let the search reveal the local sellers for this country.");
        sb.Append("Prioritise sellers physically in ").Append(geo.Country)
          .AppendLine(" or with local operations there; list local options first.");
        sb.AppendLine("Set queryType to ProductResearch.");
        sb.AppendLine(StrategySchema);
        return sb.ToString();
    }

    /// <summary>Plan a focused, exact-model deep search (price sources + spec sheet).</summary>
    public static string PlanModel(string model, GeoProfile geo)
    {
        var sb = new StringBuilder();
        sb.AppendLine(MarketContext(geo));
        sb.Append("Find EVERY local place selling the exact model \"").Append(model).Append("\" in ")
          .Append(geo.Country).AppendLine(", plus its full specification sheet.");
        sb.AppendLine("- shoppingQueries: the exact model number/name to surface all priced offers.");
        sb.AppendLine("- webQueries: the model's official brand spec page and local store/marketplace listings.");
        sb.AppendLine("- urlsToRead: the brand's official product page for this model, if identifiable.");
        sb.AppendLine("Set queryType to ProductResearch.");
        sb.AppendLine(StrategySchema);
        return sb.ToString();
    }

    /// <summary>System prompt for summarising a structured product search (grid-backed).</summary>
    public const string ProductAnalystSystem =
        "You are Daleel, a shopping assistant. You are given the actual product listings, stores, brands, and " +
        "review sources gathered for a buy-intent query. Write a short, decision-ready summary. " +
        "CRITICAL RULES: (1) For each distinct model, aggregate ALL its listings into ONE entry with its multiple " +
        "price sources — never list the same model several times for several stores. (2) Only mention stores and " +
        "listings confirmed to be in the target country or that serve it directly; do not present international " +
        "stores as options unless certain they deliver there. (3) A brand page without prices is still useful — " +
        "call it a 'Brand catalog'. A store with no visible products is a 'Store — check for availability'. " +
        "(4) Group concrete options into budget / mid-range / premium and reference real prices from the context — " +
        "never invent listings or prices. (5) After identifying the products, assess each brand's reputation in the " +
        "target country — reliability, after-sales service, local warranty, spare-parts availability — and flag any " +
        "brand with no local service centre, since a cheap product from such a brand is a poor deal." +
        HalalGuard;

    /// <summary>
    /// System prompt for the structured product-extraction pass. The LLM acts here as a
    /// <em>parser</em> (not a writer): it reads the gathered search snippets, shopping hits, store
    /// data and social posts and pulls out the concrete products on offer, with their per-store
    /// prices and links — the structured data the deterministic parsers miss when a market's
    /// shopping/scrape APIs return thin results.
    /// </summary>
    public const string ProductExtractionSystem =
        "You are Daleel, a precise product-extraction engine for a shopping assistant. Given the raw " +
        "research context for a buy-intent query (search results, shopping hits, store listings, social " +
        "posts), you EXTRACT the concrete products being sold and their prices. You never write prose, " +
        "advice, or summaries — only structured data. CRITICAL RULES: (1) Extract ONLY products that are " +
        "actually evidenced in the context (a name, a price, a seller, or a link); never invent products, " +
        "prices, models, or links. (2) Output ONE entry per distinct MODEL, with every place it is sold " +
        "gathered into that entry's offers array — never repeat the same model once per store. (3) Prices " +
        "are numbers only (strip currency symbols/thousands separators); omit a price you cannot find rather " +
        "than guessing. (4) Keep brand names, model numbers and product names in their ORIGINAL form (do not " +
        "translate them). (5) Prefer sellers in the target country; include an offer's link verbatim when the " +
        "context provides one. You ALWAYS reply with a single JSON object only." +
        HalalGuard;

    /// <summary>System prompt for distilling per-model pros/cons from reviews.</summary>
    public const string ModelDetailSystem =
        "You are Daleel, a product analyst. Given reviews, specs and listings for a single product model, you " +
        "distil an honest, concise pros/cons summary. You ALWAYS reply with a single JSON object only." +
        HalalGuard;

    /// <summary>System prompt for assessing brand reputation in a specific market.</summary>
    public const string BrandReputationSystem =
        "You are Daleel, a market analyst. You assess how brands are regarded in a SPECIFIC country, with a focus " +
        "on what matters for a purchase: reliability, after-sales service, local warranty, and spare-parts " +
        "availability. You flag brands that have NO local service centre, since a cheap product from such a brand " +
        "is a poor deal. You ALSO surface what REAL people say: from the social posts, forum threads and reviews in " +
        "the context (especially Arabic ones — تجربتي, رأي, مشاكل), quote actual user opinions with their source, " +
        "translating non-English quotes to English while keeping the original. Highlight recurring positive and " +
        "negative themes. Base everything on the provided context and known market facts; be honest about " +
        "uncertainty and never fabricate quotes. You ALWAYS reply with a single JSON object only." +
        HalalGuard;

    /// <summary>Build the prompt asking the LLM to rate the reputation of each brand in-market.</summary>
    public static string BrandReputations(IReadOnlyList<string> brands, GeoProfile geo, string gatheredContext)
    {
        var sb = new StringBuilder();
        sb.Append("Country: ").Append(geo.Country).Append(" (").Append(geo.CountryCode).AppendLine(").");
        sb.Append("Assess the reputation of these brands IN ").Append(geo.Country).AppendLine(":");
        sb.AppendLine(string.Join(", ", brands));
        sb.AppendLine();
        sb.AppendLine("For each brand consider: overall reliability, after-sales service quality, presence of LOCAL " +
            "service centres and spare parts, and warranty terms in this country. Flag any brand with no local service.");
        sb.AppendLine("Context (search results, social posts, reviews):");
        sb.AppendLine("----------------------------------------");
        sb.AppendLine(gatheredContext);
        sb.AppendLine("----------------------------------------");
        sb.AppendLine("""
            Reply with exactly this JSON object:
            {
              "brands": [
                {
                  "brand": "string",
                  "score": 4.2,
                  "pros": ["short praise", "..."],
                  "complaints": ["short complaint", "..."],
                  "hasLocalService": true,
                  "serviceNote": "e.g. authorized service centre in the capital, or 'no local service found'",
                  "warranty": "e.g. 2-year local warranty, or null if unknown",
                  "summary": "one or two sentence reputation verdict for this country",
                  "reviews": [
                    {
                      "quote": "the user's opinion in English (translate if needed)",
                      "originalText": "original-language text if you translated, else null",
                      "source": "platform/group, e.g. 'Facebook group'",
                      "url": "link to the post if present in context, else null",
                      "sentiment": "positive|negative|neutral",
                      "date": "ISO date if known, else null",
                      "language": "ar|en|..."
                    }
                  ]
                }
              ]
            }
            score is 1-5 (omit or null if you cannot judge). hasLocalService is true/false/null. Only include reviews
            that are actually present in the context — never invent quotes; use an empty array if there are none.
            No prose outside the JSON.
            """);
        return sb.ToString();
    }

    /// <summary>Build the prompt asking the LLM for a model's pros/cons + one-line verdict.</summary>
    public static string ModelProsCons(string modelName, string gatheredContext)
    {
        var sb = new StringBuilder();
        sb.Append("Model: ").AppendLine(modelName);
        sb.AppendLine("Context (specs, listings, reviews):");
        sb.AppendLine("----------------------------------------");
        sb.AppendLine(gatheredContext);
        sb.AppendLine("----------------------------------------");
        sb.AppendLine("""
            Reply with exactly this JSON object:
            {
              "pros": ["short pro", "..."],
              "cons": ["short con", "..."],
              "summary": "one or two sentence verdict"
            }
            Base everything on the context; if it's thin, return fewer points rather than inventing. No prose outside the JSON.
            """);
        return sb.ToString();
    }

    /// <summary>
    /// Builds the prompt that asks the LLM to extract structured products + per-store offers from
    /// the gathered context. The JSON shape feeds straight into the listing aggregator, so a model's
    /// <c>offers</c> become its <see cref="Daleel.Core.Models.PriceOffer"/>s in the product grid.
    /// </summary>
    public static string ExtractProducts(string query, GeoProfile geo, string gatheredContext)
    {
        var sb = new StringBuilder();
        sb.AppendLine(MarketContext(geo));
        sb.Append("Buy-intent query: ").AppendLine(query);
        sb.Append("Extract the concrete products a shopper in ").Append(geo.Country)
          .AppendLine(" could buy, drawn ONLY from the context below. Prefer local sellers; quote real prices and links.");
        sb.AppendLine("Gathered research context (search results, shopping hits, store data, social posts):");
        sb.AppendLine("----------------------------------------");
        sb.AppendLine(gatheredContext);
        sb.AppendLine("----------------------------------------");
        sb.Append("Prices are in ").Append(geo.Currency)
          .AppendLine(" unless the context states otherwise.");
        sb.AppendLine("""
            Reply with exactly this JSON object:
            {
              "products": [
                {
                  "name": "full product name as written",
                  "brand": "manufacturer, e.g. Samsung / LG / Gree",
                  "model": "model number/name if known, else null",
                  "imageUrl": "image link if present in context, else null",
                  "specs": { "key": "value" },
                  "offers": [
                    {
                      "source": "store/marketplace name, e.g. Smart Buy",
                      "price": 320,
                      "currency": "JOD",
                      "url": "direct link to this offer if present, else null",
                      "condition": "new|used|refurbished, else null"
                    }
                  ]
                }
              ]
            }
            One entry per distinct model; gather all its sellers into offers. Only include products evidenced in
            the context — never invent products, prices, models or links. Use an empty products array if the context
            has no concrete products. No prose outside the JSON.
            """);
        return sb.ToString();
    }

    /// <summary>Maps a BCP-47 code to an English language name for the "respond in" directive.</summary>
    public static string LanguageName(string? code) => (code ?? "en").ToLowerInvariant() switch
    {
        "ar" => "Arabic",
        "en" => "English",
        "fr" => "French",
        _ => "English"
    };

    /// <summary>Build the analyst prompt that turns gathered context into a final report.</summary>
    public static string Analyze(string task, GeoProfile geo, string gatheredContext, string? language = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(MarketContext(geo));
        sb.Append("Respond in ").Append(LanguageName(language))
          .AppendLine(". Keep product names, brand names, and model numbers in their original form (do not translate them).");
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
