using System.Text;
using Daleel.Core.Geo;
using Daleel.Core.Intelligence;
using Daleel.Core.Models;

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
    /// <remarks>
    /// Policy: we filter haram <em>content</em> (what a result sells/shows), not a store's financing
    /// model. Banks, financial institutions, and retailers that offer interest-based (riba)
    /// installment plans MUST still appear — the user can pay cash. So riba is intentionally absent
    /// from this list; keep it in sync with <c>ContentFilter.Categories</c>.
    /// </remarks>
    public const string HalalGuard =
        " IMPORTANT: Only include halal-compliant results. Exclude any products, stores, or content " +
        "related to: alcohol/wine, pork/non-halal meat, gambling, adult or immodest content, drugs, or " +
        "tobacco. If a result promotes any of these, skip it entirely. Do NOT exclude a store merely " +
        "because it is a bank or offers interest-based (riba) financing — the user can pay cash, so " +
        "such stores are allowed; only the haram products/content themselves are excluded.";

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
          "intent": "Product|Service|Place",
          "subject": "the product/brand/topic",
          "webQueries": ["bilingual web search strings"],
          "shoppingQueries": ["shopping/marketplace search strings"],
          "socialQueries": ["social platform keywords"],
          "placesQueries": ["store-finder queries, e.g. 'AC stores', 'متاجر مكيفات'"],
          "urlsToRead": ["specific URLs worth deep-reading, may be empty"],
          "reasoning": "one sentence on the plan"
        }
        Generate queries in BOTH the market's primary language and English. No prose outside the JSON.

        Set "intent" to what KIND of thing the user wants (independent of queryType):
        - "Product" — a buyable item (AC, phone, car, furniture). DEFAULT when unsure.
        - "Service" — something to hire/book (plumber, lawyer, cleaning, car repair, course).
        - "Place" — a physical venue to visit (restaurant, café, gym, clinic, hotel, salon).

        queryType MUST be "ProductResearch" whenever the user is choosing or acquiring a product — "best X",
        "top X", "cheapest X", "buy X", "X price/deals", "أفضل X", "سعر X" — even when phrased as a question.
        Reserve "General" for questions that are NOT about acquiring something (news, advice, how-to, facts).
        """;

    /// <summary>Plan a free-form natural-language question.</summary>
    public static string PlanFreeform(string question, GeoProfile geo)
    {
        var sb = new StringBuilder();
        sb.AppendLine(MarketContext(geo));
        sb.Append("User question: ").AppendLine(question);
        sb.AppendLine();
        sb.AppendLine("Classify the question — both its queryType (task shape) AND its intent " +
            "(is the user after a product to buy, a service to hire, or a place to visit?) — and design the search strategy.");
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
        // Short, abbreviated or ambiguous queries (e.g. "AC", "TV", "مكيف") return junk if searched
        // literally — expand them to the full product category, in both languages, before querying.
        sb.AppendLine("FIRST, if the query is an abbreviation, very short, or ambiguous, expand it to the full " +
            "product category in BOTH the market language and English before building queries " +
            "(e.g. \"AC\" → \"air conditioner\" / \"مكيف هواء\" / \"مكيف سبليت\"; \"TV\" → \"television\" / \"تلفزيون\"). " +
            "Use the expanded terms — never search the bare abbreviation alone.");
        sb.AppendLine("Aim for BREADTH: cover as MANY brands as compete in this market and MULTIPLE models per brand " +
            "— generate enough queries (include brand-named and 'best <category> brands' queries) that the results " +
            "span the budget, mid-range and premium ends, not just the top one or two names.");
        sb.AppendLine("Generate 8–10 DIVERSE webQueries (and 4–6 shoppingQueries) so the search casts a wide net — a " +
            "single generic query only surfaces the big global sites; the diverse ones are what reach the local sellers.");
        // Local-store discovery is the single biggest gap in local markets: a small, well-stocked local
        // e-commerce store (a Shopify/WooCommerce shop, a local electronics chain's site) rarely ranks for a
        // bare "<category>" query but DOES rank for store-finding phrasings — and once its domain is in the
        // bundle, the store sub-workflow crawls its full catalogue. So EXPLICITLY ask for store-discovery
        // queries in BOTH languages, without ever naming a specific store (let the engine reveal them).
        sb.Append("CRITICAL — LOCAL STORE DISCOVERY: several webQueries MUST be store-FINDING queries that surface ")
          .Append(geo.Country).AppendLine("-based online shops, e-commerce sites and their category/collection pages, " +
            "in BOTH the market language and English. Use phrasings like: the category + 'online store' / 'متجر إلكتروني' / " +
            "'اونلاين' / 'shop' / 'متجر', the category + 'buy' / 'للبيع' / 'اشتري' + the main city, the category + " +
            "'online shopping " + geo.Country + "', and the category + the country/city name. Also include the market's " +
            "well-known local e-commerce platforms and classifieds where a shopper actually buys this category (name them " +
            "generically from your knowledge of this market — do NOT restrict to global sites like Amazon/AliExpress).");
        sb.AppendLine("Design the search to surface CONCRETE LISTINGS from whatever local sites rank in this market:");
        // Shopping queries must be PLAIN product+price phrases: the shopping engine (Google Shopping via
        // gl=cc) is what returns priced listings WITH thumbnails, and it returns ~nothing for site:-scoped
        // queries. A plan whose shopping queries are all "… site:amazon.com"-shaped yields zero structured
        // listings — and with them go the product images and prices in the result grid (QA job 2 evidence).
        sb.AppendLine("- shoppingQueries: PLAIN price/marketplace-style queries in both the market language and English " +
            "(e.g. the category + 'للبيع' / 'price' / 'سعر' / 'buy'), including a few brand-specific ones. " +
            "NEVER use 'site:' operators here — the shopping engine returns nothing for them; put " +
            "site-scoped searches in webQueries instead.");
        sb.AppendLine("- webQueries: a mix of local classifieds/marketplace pages, brand catalog pages, store " +
            "sections, and SEVERAL buying-guide / 'best <category>' / review / comparison queries (these become the " +
            "'related articles' the user reads).");
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
        "posts, AND review/buying-guide ARTICLES), you first CLASSIFY each source — a real product/store " +
        "listing, an article/review/blog about products, a store page, or an irrelevant page — then EXTRACT " +
        "the concrete products being sold and their prices. You never write prose, advice, or summaries — " +
        "only structured data. CRITICAL RULES: (1) Output the distinct PRODUCT MODELS the context names, " +
        "capped at the ~20 BEST-EVIDENCED models (prefer those with prices, sellers or repeated mentions; " +
        "within the cap still spread across brands and price tiers) — a model named in a buying guide with " +
        "no price and no seller still counts; include it with an empty offers array. Never invent products, " +
        "prices, models, or links. (2) Articles, reviews and round-ups " +
        "are SOURCES, not items: mine them for which products and brands exist, but NEVER output the article " +
        "itself as a product. (3) Output ONE entry per distinct " +
        "MODEL, with every place it is sold gathered into that entry's offers array — never repeat the same " +
        "model once per store. (4) Prices are numbers only (strip currency symbols/thousands separators); omit " +
        "a price you cannot find rather than guessing. (5) Keep brand names, model numbers and product names in " +
        "their ORIGINAL form (do not translate them). (6) Prefer sellers in the target country; include an " +
        "offer's link verbatim when the context provides one. You ALWAYS reply with a single JSON object only." +
        HalalGuard;

    /// <summary>
    /// System prompt for the structured SERVICE-extraction pass. Same parser discipline as
    /// <see cref="ProductExtractionSystem"/>, but the "products" are service providers: the
    /// <c>offers</c> array carries PRICING TIERS (tier name + price) and the <c>specs</c> object
    /// carries availability, contact and coverage details. Reuses the product JSON shape so the
    /// downstream aggregator is unchanged.
    /// </summary>
    public const string ServiceExtractionSystem =
        "You are Daleel, a precise SERVICE-extraction engine for a local-services assistant. Given the raw " +
        "research context for a hire-intent query (search results, provider sites, directory listings, social " +
        "posts AND review/round-up ARTICLES), you first CLASSIFY each source — a real service PROVIDER, an " +
        "article/review about providers, a directory page, or an irrelevant page — then EXTRACT the concrete " +
        "providers a customer could hire and their pricing. You never write prose — only structured data. " +
        "CRITICAL RULES: (1) Output one entry per distinct PROVIDER (the \"name\"/\"brand\"), even one named in a " +
        "round-up with no price — include it with an empty offers array. Never invent providers or prices. " +
        "(2) Model PRICING TIERS as offers: each offer's \"source\" is the tier/package name (e.g. 'Basic visit', " +
        "'Hourly rate') and \"price\" its number; omit a price you cannot find. (3) Put availability, contact " +
        "(phone/booking link), service area/coverage, and any rating into the \"specs\" object as key/value " +
        "strings (e.g. \"availability\": \"24/7\", \"phone\": \"...\", \"area\": \"Amman\"). (4) Mine review/round-up " +
        "articles for which providers exist, but NEVER output an article itself as a provider. (5) Prefer providers " +
        "operating in the target country; include a verbatim link when the context provides one. You ALWAYS reply " +
        "with a single JSON object only." +
        HalalGuard;

    /// <summary>
    /// System prompt for the structured PLACE-extraction pass. The "products" are physical venues: the
    /// <c>offers</c> array is normally empty (a place isn't purchased) and the <c>specs</c> object carries
    /// the location, opening hours, contact, address and Google-Maps link. Reuses the product JSON shape so
    /// the downstream aggregator is unchanged; the venue's rating/reviews/coordinates also surface through
    /// the Places provider's <c>StoreInfo</c>.
    /// </summary>
    public const string PlaceExtractionSystem =
        "You are Daleel, a precise PLACE-extraction engine for a local-discovery assistant. Given the raw " +
        "research context for a visit-intent query (search results, map/place listings, directory pages, social " +
        "posts AND review/round-up ARTICLES), you first CLASSIFY each source — a real physical VENUE, an " +
        "article/review about venues, a directory page, or an irrelevant page — then EXTRACT the concrete places " +
        "a visitor could go to. You never write prose — only structured data. CRITICAL RULES: (1) Output one entry " +
        "per distinct VENUE (the \"name\"), even one named in a round-up — include it. Never invent venues. " +
        "(2) Leave \"offers\" empty unless the context states an explicit entry/menu price; a place is visited, not " +
        "bought. (3) Put the venue's details into the \"specs\" object as key/value strings: \"address\", \"area\", " +
        "\"hours\", \"phone\", \"rating\", and \"mapUrl\" (a Google-Maps or maps link if present in context). " +
        "(4) Mine review/round-up articles for which venues exist, but NEVER output an article itself as a venue. " +
        "(5) Only include venues physically in the target market; include a verbatim link/photo URL when the " +
        "context provides one. You ALWAYS reply with a single JSON object only." +
        HalalGuard;

    /// <summary>Picks the extraction system prompt that matches the classified intent (Product is the default).</summary>
    public static string ExtractionSystem(SearchIntentType intent) => intent switch
    {
        SearchIntentType.Service => ServiceExtractionSystem,
        SearchIntentType.Place => PlaceExtractionSystem,
        _ => ProductExtractionSystem
    };

    /// <summary>
    /// System prompt for the post-aggregation relevance gate. Deterministic shopping hits (SerpAPI →
    /// listing) reach the grid WITHOUT any LLM pass, so loosely-matching items survive — a milk frother
    /// or a "slimming coffee" drink in a coffee-MAKER search. This gate asks ONE question per item:
    /// is the item ITSELF an instance of the product type the user is shopping for?
    /// </summary>
    public const string RelevanceGateSystem =
        "You are Daleel's product-relevance gate. You are given the product TYPE a shopper is looking for " +
        "and a numbered list of extracted items. For EACH item you answer exactly one question: is this item " +
        "ITSELF an instance of that product type? An accessory FOR it, a consumable used WITH it, a spare " +
        "part, or any other kind of product is NOT an instance of it (a milk frother is not a coffee maker; " +
        "a bag of coffee or a 'slimming coffee' drink is not a coffee maker). Judge from the item's name/brand " +
        "only; when you are genuinely unsure, treat the item as matching (keep it). You never write prose — " +
        "you ALWAYS reply with a single JSON object only." +
        HalalGuard;

    /// <summary>
    /// Builds the relevance-gate prompt: the target product type plus the numbered item names. The reply
    /// lists only the indices to DROP, so an item the model overlooks fails open (kept), and the output
    /// stays tiny regardless of how many items are kept.
    /// </summary>
    public static string RelevanceGate(string target, IReadOnlyList<string> items)
    {
        var sb = new StringBuilder();
        sb.Append("The shopper is looking for: ").AppendLine(target);
        sb.AppendLine("For each numbered item, decide: is the item ITSELF one of these? Accessories, consumables, " +
            "parts, and different product types are NOT. Also drop any item that is haram/immodest content.");
        sb.AppendLine("Items:");
        for (var i = 0; i < items.Count; i++)
        {
            sb.Append(i).Append(". ").AppendLine(items[i]);
        }
        sb.AppendLine("""
            Reply with exactly this JSON object:
            {
              "drop": [indices of items that are NOT themselves the target product type, or are haram]
            }
            Only list indices you are CONFIDENT about — when unsure, do not list the item (it stays).
            Use an empty array when every item matches. No prose outside the JSON.
            """);
        return sb.ToString();
    }

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
    public static string ExtractProducts(string query, GeoProfile geo, string gatheredContext,
        ProductSchema? schema = null, SearchIntentType intent = SearchIntentType.Product)
    {
        // Service and Place searches reuse the SAME JSON shape (the "products" array, whose specs object
        // is free-form and whose offers double as pricing tiers) so the downstream aggregator is unchanged
        // — only the extraction GUIDANCE differs by intent.
        if (intent == SearchIntentType.Service)
        {
            return ExtractServices(query, geo, gatheredContext);
        }
        if (intent == SearchIntentType.Place)
        {
            return ExtractPlaces(query, geo, gatheredContext);
        }

        var sb = new StringBuilder();
        sb.AppendLine(MarketContext(geo));
        sb.Append("Buy-intent query: ").AppendLine(query);
        sb.Append("Extract the concrete products a shopper in ").Append(geo.Country)
          .AppendLine(" could buy, drawn ONLY from the context below. Prefer local sellers; quote real prices and links.");
        // Bounded breadth: an uncapped "extract EVERY model" instruction produced multi-minute LLM
        // generations on article-heavy queries (the call flirted with the HTTP client's timeout and
        // burned most of the run's 10-minute budget). ~20 well-evidenced models is more than the grid,
        // compare table or a shopper can use, and keeps the extraction call fast and reliable.
        sb.AppendLine("Extract the distinct models the context evidences, CAPPED at the ~20 BEST-EVIDENCED — prefer " +
            "models with prices/sellers, then well-known models the guides name; within the cap span MULTIPLE brands " +
            "and MULTIPLE models per brand (budget through premium), not just the few most prominent.");
        sb.AppendLine("CLASSIFY before you extract. The context mixes two kinds of source: (a) real PRODUCT/STORE " +
            "listings — a concrete item sold by a real store/marketplace, with a price or a buy link — and (b) ARTICLES, " +
            "reviews, blogs and buying-guides ABOUT products. NEVER output an article, review or round-up ITSELF as a " +
            "product (its title is not a product). But DO mine articles for the models they name: a model named in a " +
            "buying guide with a name but NO price and NO seller still counts — include it with an empty offers array so " +
            "the shopper sees it exists. Attach real prices and seller links to a model's offers whenever the context " +
            "provides them; leave offers empty when it does not. The goal is BREADTH within the cap: spread the " +
            "~20 models across brands and price tiers, not only the few that happen to carry an in-context price.");
        // A tighter, deterministic article guard on top of the classification rule above: an article's HEADLINE
        // (often phrased as a list/opinion and living under a blog/news URL path) is the single most common thing
        // the extractor wrongly emits as a product. Name the tells explicitly so it never leaks into "products".
        sb.AppendLine("HARD RULE — an item is an ARTICLE (a SOURCE, never a product) when ANY of these hold, no matter " +
            "how product-like its title reads: its URL path contains /blog, /news, /article, /review, /guide, /best, " +
            "/top, /vs, or a date; OR its title is a list/opinion headline (starts with or contains 'Best', 'Top', " +
            "'أفضل', 'مراجعة', 'Review', 'Guide', 'How to', 'vs', a year, or a ranked count like '10 '). Mine such a " +
            "source for the models it names, but NEVER output its headline as a product's \"name\".");
        sb.AppendLine("A product's \"name\" is ALWAYS a real product/model name — NEVER a URL, domain, link, store name, " +
            "or article headline. If the only label you can find for an item is a URL or a headline, drop the item.");
        // Capture the FULL detail a store listing exposes — not just name+price. Local store pages carry
        // SKU/model codes, stock status, a spec-rich description and product images; pulling all of it in
        // makes each grid card and detail panel useful instead of a bare name. specs is free-form, so the
        // extra attributes ride along without any schema change downstream.
        sb.AppendLine("EXTRACT MAXIMUM DETAIL per product from its listing: set \"imageUrl\" to the product's image when the " +
            "context provides one, and add these to \"specs\" whenever the listing shows them (use these exact keys): " +
            "\"sku\" (SKU / model code / part number, verbatim), \"availability\" (in stock / out of stock / pre-order), " +
            "and \"description\" (a one- or two-sentence trim of the product description). Keep each offer's real price, " +
            "currency and direct link. Never invent any of these — omit a field the listing does not show.");
        // Schema-aware extraction: when the up-front category analysis identified the specs that matter for
        // this product type, tell the extractor to fill those exact keys (so the compare table and detail
        // views line up across products) instead of returning arbitrary free-form spec keys.
        if (schema is { IsEmpty: false })
        {
            sb.Append("This is a \"").Append(schema.ProductType).AppendLine("\" search. In each product's \"specs\" object, " +
                "PRIORITISE these fields when the context provides a value (use these EXACT keys; omit a field rather than guessing):");
            foreach (var f in schema.Fields)
            {
                sb.Append("  - ").Append(f.Key).Append(" (").Append(f.Label).Append(')');
                if (!string.IsNullOrWhiteSpace(f.Unit)) sb.Append(" in ").Append(f.Unit);
                sb.AppendLine();
            }
        }
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
                  "name": "full product name as written — NEVER a URL, link or domain",
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
                  ],
                  "pros": ["short strength drawn from reviews/specs in the context", "..."],
                  "cons": ["short weakness drawn from reviews/specs in the context", "..."],
                  "summary": "one-line verdict grounded in the context, else null"
                }
              ]
            }
            One entry per distinct model; gather ALL its sellers into offers, and always include the offer's direct
            url when the context provides one. Fill pros/cons/summary ONLY from opinions or specs actually in the
            context (empty arrays / null when there's nothing to say) — never invent products, prices, models, links,
            or opinions. Use an empty products array if the context has no concrete products. Respond ONLY with valid JSON.
            """);
        return sb.ToString();
    }

    /// <summary>
    /// Builds the SERVICE-intent extraction prompt. Returns the SAME JSON shape as
    /// <see cref="ExtractProducts"/> — providers map onto "products", pricing tiers onto "offers",
    /// and availability/contact/area onto the free-form "specs" object — so the existing
    /// <c>ExtractedProductsDto</c> and the listing aggregator handle it unchanged.
    /// </summary>
    private static string ExtractServices(string query, GeoProfile geo, string gatheredContext)
    {
        var sb = new StringBuilder();
        sb.AppendLine(MarketContext(geo));
        sb.Append("Hire-intent query: ").AppendLine(query);
        sb.Append("Extract the concrete service PROVIDERS a customer in ").Append(geo.Country)
          .AppendLine(" could hire, drawn ONLY from the context below. Prefer providers operating locally.");
        sb.AppendLine("Be COMPREHENSIVE: extract EVERY distinct provider the context evidences (a provider named in a " +
            "round-up with no price still counts — include it with an empty offers array). Never invent providers.");
        sb.AppendLine("CLASSIFY before you extract: real provider listings/directory pages vs. ARTICLES about providers. " +
            "Mine articles for which providers exist, but NEVER output an article itself as a provider.");
        sb.AppendLine("Gathered research context (search results, directory/provider pages, social posts, reviews):");
        sb.AppendLine("----------------------------------------");
        sb.AppendLine(gatheredContext);
        sb.AppendLine("----------------------------------------");
        sb.Append("Prices are in ").Append(geo.Currency).AppendLine(" unless the context states otherwise.");
        sb.AppendLine("""
            Reply with exactly this JSON object:
            {
              "products": [
                {
                  "name": "provider/business name as written",
                  "brand": "parent company or chain if any, else null",
                  "model": null,
                  "imageUrl": "logo/photo link if present, else null",
                  "specs": {
                    "availability": "e.g. 24/7, Mon–Fri 9–5, by appointment",
                    "phone": "contact number if present, else omit",
                    "bookingUrl": "booking/contact link if present, else omit",
                    "area": "service area / coverage, e.g. Greater Amman",
                    "rating": "e.g. 4.6 (120 reviews) if present"
                  },
                  "offers": [
                    {
                      "source": "pricing tier/package name, e.g. 'Call-out fee' or 'Hourly rate'",
                      "price": 25,
                      "currency": "JOD",
                      "url": "link to this offer/booking if present, else null",
                      "condition": null
                    }
                  ],
                  "pros": ["short strength drawn from reviews in the context", "..."],
                  "cons": ["short weakness drawn from reviews in the context", "..."],
                  "summary": "one-line verdict grounded in the context, else null"
                }
              ]
            }
            One entry per distinct provider; model pricing tiers as offers (tier name in "source"); put contact,
            availability, area and rating in "specs". Fill pros/cons/summary ONLY from the context (empty / null
            otherwise) — never invent providers, prices, contacts or opinions. Use an empty products array if the
            context names no real providers. Respond ONLY with valid JSON.
            """);
        return sb.ToString();
    }

    /// <summary>
    /// Builds the PLACE-intent extraction prompt. Returns the SAME JSON shape as
    /// <see cref="ExtractProducts"/> — venues map onto "products" with location/hours/contact/map carried in
    /// the free-form "specs" object and "offers" normally empty — so the downstream aggregator is unchanged.
    /// The venue's coordinates/rating/reviews also arrive separately via the Places provider's StoreInfo.
    /// </summary>
    private static string ExtractPlaces(string query, GeoProfile geo, string gatheredContext)
    {
        var sb = new StringBuilder();
        sb.AppendLine(MarketContext(geo));
        sb.Append("Visit-intent query: ").AppendLine(query);
        sb.Append("Extract the concrete physical PLACES a visitor in ").Append(geo.Country)
          .AppendLine(" could go to, drawn ONLY from the context below. Only include venues physically in this market.");
        sb.AppendLine("Be COMPREHENSIVE: extract EVERY distinct venue the context evidences (a venue named in a " +
            "round-up still counts — include it). Never invent venues.");
        sb.AppendLine("CLASSIFY before you extract: real venue/map listings vs. ARTICLES about venues. Mine articles " +
            "for which venues exist, but NEVER output an article itself as a venue.");
        sb.AppendLine("Gathered research context (search results, map/place listings, social posts, reviews):");
        sb.AppendLine("----------------------------------------");
        sb.AppendLine(gatheredContext);
        sb.AppendLine("----------------------------------------");
        sb.AppendLine("""
            Reply with exactly this JSON object:
            {
              "products": [
                {
                  "name": "venue name as written",
                  "brand": "chain/franchise if any, else null",
                  "model": null,
                  "imageUrl": "photo link if present, else null",
                  "specs": {
                    "address": "street address if present",
                    "area": "neighbourhood/district, e.g. Abdoun",
                    "hours": "opening hours if present, e.g. 'Daily 10:00–23:00'",
                    "phone": "contact number if present, else omit",
                    "rating": "e.g. 4.5 (300 reviews) if present",
                    "mapUrl": "Google-Maps / maps link if present, else omit"
                  },
                  "offers": [],
                  "pros": ["short strength drawn from reviews in the context", "..."],
                  "cons": ["short weakness drawn from reviews in the context", "..."],
                  "summary": "one-line verdict grounded in the context, else null"
                }
              ]
            }
            One entry per distinct venue; leave "offers" empty unless the context states an explicit price; put
            address, hours, contact, rating and map link in "specs". Fill pros/cons/summary ONLY from the context
            (empty / null otherwise) — never invent venues, addresses, hours or opinions. Use an empty products array
            if the context names no real venues. Respond ONLY with valid JSON.
            """);
        return sb.ToString();
    }

    /// <summary>
    /// System prompt for the category-"thinking" pass that runs BEFORE source gathering. The LLM
    /// reasons about the product category up-front — what kind of product it is, which stores sell
    /// it, which brands compete, which specs decide a purchase — so every later pipeline step knows
    /// what it is looking for instead of extracting blindly.
    /// </summary>
    public const string CategoryIntelligenceSystem =
        "You are Daleel, a market-research strategist. Before any searching happens, you analyse a " +
        "product category for a specific market and decide what matters: the precise product TYPE, the " +
        "kinds of STORE that actually sell it (electronics shops for a TV, not grocery stores), the BRANDS " +
        "that compete across budget-to-premium tiers, the SPEC fields a buyer compares on (BTU and energy " +
        "rating for an AC; RAM, storage and camera for a phone; CPU and GPU for a laptop), and a realistic " +
        "local PRICE expectation. You ground brands and prices in the named market. You ALWAYS reply with a " +
        "single JSON object only." +
        HalalGuard;

    /// <summary>
    /// Builds the category-intelligence prompt. The returned JSON deserializes into a
    /// <see cref="Daleel.Core.Intelligence.SearchIntelligence"/> (including its
    /// <see cref="Daleel.Core.Intelligence.ProductSchema"/>), which is threaded through the rest of the
    /// pipeline to focus extraction, profile relevance, and the compare table.
    /// </summary>
    public static string CategoryIntelligence(string category, GeoProfile geo)
    {
        var sb = new StringBuilder();
        sb.AppendLine(MarketContext(geo));
        sb.Append("Analyse the product category the shopper is researching: \"").Append(category).AppendLine("\".");
        sb.Append("Decide, for ").Append(geo.Country).AppendLine(" specifically:");
        sb.AppendLine("- the precise product TYPE (e.g. \"air conditioner\", \"smartphone\", \"laptop\", \"refrigerator\");");
        sb.AppendLine("- the kinds of STORE that sell it here (electronics/HVAC/appliance retailers — not unrelated shops);");
        sb.AppendLine("- the BRANDS that actually compete in this market, spanning budget, mid-range and premium;");
        sb.AppendLine("- the 4–8 SPEC fields a buyer compares this product type on, with units and which direction is better;");
        sb.AppendLine("- a realistic local price expectation, and whether product images matter for this category.");
        sb.AppendLine("""
            Reply with exactly this JSON object:
            {
              "productType": "lower-case product type, e.g. air conditioner",
              "relevantStoreTypes": ["electronics store", "HVAC retailer", "..."],
              "expectedBrands": ["Gree", "Samsung", "LG", "..."],
              "priceExpectation": "short market-aware range, e.g. 'typically 250–1,200 JOD for split units'",
              "imagesMatter": true,
              "specs": [
                {
                  "key": "lower_snake_case_key, e.g. btu",
                  "label": "Human label, e.g. Cooling capacity",
                  "unit": "unit suffix like BTU / GB / inch / dB, else null",
                  "higherIsBetter": true,
                  "importance": "key|normal"
                }
              ],
              "reasoning": "one sentence on what decides this purchase"
            }
            Choose 4–8 specs that genuinely differentiate this product type (mark the 2–3 defining ones "key").
            higherIsBetter is true/false/null (null when not orderable, e.g. operating system). No prose outside the JSON.
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
