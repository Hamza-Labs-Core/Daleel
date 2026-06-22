using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Daleel.Agent;
using Daleel.Core.Geo;

namespace Daleel.Cli;

/// <summary>
/// The agent-backed CLI surface: free-form <c>ask</c>, plus brand / product / stores /
/// deals / compare / reviews / nearby commands. Each builds an <see cref="AgentService"/>
/// from the environment and prints a JSON report (optionally Markdown).
/// </summary>
internal static class AgentCommands
{
    private static readonly JsonSerializerOptions JsonOut = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static IEnumerable<Command> All() => new[]
    {
        Ask(), Brand(), Product(), Stores(), Deals(), Compare(), Reviews(), Nearby()
    };

    // ── ask ──────────────────────────────────────────────────────────────────
    private static Command Ask()
    {
        var question = new Argument<string>("question", "Free-form question (Arabic or English).");
        var geo = GeoOption();
        var cmd = new Command("ask", "Ask a free-form market-intelligence question.") { question, geo };

        cmd.SetHandler(async (context) =>
        {
            var q = context.ParseResult.GetValueForArgument(question);
            var g = context.ParseResult.GetValueForOption(geo);
            if (!Guard(context)) return;

            var agent = Composition.BuildAgent(g ?? "usa", Console.Error.WriteLine);
            var answer = await agent.AskAsync(q, g);
            PrintSummaryThenJson(answer.Summary, answer);
        });

        return cmd;
    }

    // ── brand ────────────────────────────────────────────────────────────────
    private static Command Brand()
    {
        var name = new Argument<string>("brand", "Brand name (Arabic or English).");
        var geo = GeoOption();
        var cmd = new Command("brand", "Full brand-intelligence report for a market.") { name, geo };

        cmd.SetHandler(async (context) =>
        {
            var b = context.ParseResult.GetValueForArgument(name);
            var g = context.ParseResult.GetValueForOption(geo);
            if (!Guard(context)) return;

            var agent = Composition.BuildAgent(g ?? "usa", Console.Error.WriteLine);
            var report = await agent.ResearchBrandAsync(b, g);
            PrintSummaryThenJson(report.Summary, report);
        });

        return cmd;
    }

    // ── product ──────────────────────────────────────────────────────────────
    private static Command Product()
    {
        var category = new Argument<string>("category", "Product category (e.g. \"مكيف\", \"AC\").");
        var geo = GeoOption();
        var cmd = new Command("product", "Product-category research report.") { category, geo };

        cmd.SetHandler(async (context) =>
        {
            var c = context.ParseResult.GetValueForArgument(category);
            var g = context.ParseResult.GetValueForOption(geo);
            if (!Guard(context)) return;

            var agent = Composition.BuildAgent(g ?? "usa", Console.Error.WriteLine);
            var report = await agent.ResearchProductAsync(c, g);
            PrintSummaryThenJson(report.Summary, report);
        });

        return cmd;
    }

    // ── stores ───────────────────────────────────────────────────────────────
    private static Command Stores()
    {
        var subject = new Argument<string>("subject", "Product/brand to find stores for.");
        var geo = GeoOption();
        var near = new Option<string?>("--near", "City or place to search near (e.g. \"عمان\").");
        var cmd = new Command("stores", "Find stores (Google Places) selling something.") { subject, geo, near };

        cmd.SetHandler(async (context) =>
        {
            var s = context.ParseResult.GetValueForArgument(subject);
            var g = context.ParseResult.GetValueForOption(geo);
            if (!Guard(context)) return;

            var profile = GeoProfiles.ResolveOrDefault(g ?? "usa");
            var agent = Composition.BuildAgent(profile.Key, Console.Error.WriteLine);
            var stores = await agent.FindStoresAsync(s, profile.Key, profile.Center);
            PrintJson(stores);
        });

        return cmd;
    }

    // ── deals ────────────────────────────────────────────────────────────────
    private static Command Deals()
    {
        var subject = new Argument<string>("subject", "Product/brand to find deals for.");
        var geo = GeoOption();
        var cmd = new Command("deals", "Find current deals/promotions.") { subject, geo };

        cmd.SetHandler(async (context) =>
        {
            var s = context.ParseResult.GetValueForArgument(subject);
            var g = context.ParseResult.GetValueForOption(geo);
            if (!Guard(context)) return;

            // Deal hunting is a product-research run framed around promotions; the agent's
            // analyst narrative surfaces the deals from gathered shopping/web context.
            var agent = Composition.BuildAgent(g ?? "usa", Console.Error.WriteLine);
            var answer = await agent.AskAsync($"current deals, discounts and promotions for {s}", g);
            PrintSummaryThenJson(answer.Summary, answer);
        });

        return cmd;
    }

    // ── compare ──────────────────────────────────────────────────────────────
    private static Command Compare()
    {
        var products = new Argument<string[]>("products", "Two or more products to compare.")
        {
            Arity = ArgumentArity.OneOrMore
        };
        var geo = GeoOption();
        var cmd = new Command("compare", "Head-to-head product comparison with local prices.") { products, geo };

        cmd.SetHandler(async (context) =>
        {
            var p = context.ParseResult.GetValueForArgument(products);
            var g = context.ParseResult.GetValueForOption(geo);
            if (!Guard(context)) return;

            var agent = Composition.BuildAgent(g ?? "usa", Console.Error.WriteLine);
            var report = await agent.CompareAsync(p, g);
            PrintSummaryThenJson(report.Summary, report);
        });

        return cmd;
    }

    // ── reviews ──────────────────────────────────────────────────────────────
    private static Command Reviews()
    {
        var subject = new Argument<string>("subject", "Product to aggregate reviews/opinions for.");
        var geo = GeoOption();
        var cmd = new Command("reviews", "Aggregate customer reviews/opinions from multiple sources.") { subject, geo };

        cmd.SetHandler(async (context) =>
        {
            var s = context.ParseResult.GetValueForArgument(subject);
            var g = context.ParseResult.GetValueForOption(geo);
            if (!Guard(context)) return;

            var agent = Composition.BuildAgent(g ?? "usa", Console.Error.WriteLine);
            var report = await agent.ResearchProductAsync(s, g);
            PrintSummaryThenJson(report.Summary, new { report.Opinions, report.Sources });
        });

        return cmd;
    }

    // ── nearby ───────────────────────────────────────────────────────────────
    private static Command Nearby()
    {
        var what = new Argument<string>("what", "What to find (e.g. \"electronics\").");
        var lat = new Option<double>("--lat", "Latitude.") { IsRequired = true };
        var lng = new Option<double>("--lng", "Longitude.") { IsRequired = true };
        var radius = new Option<double>("--radius", () => 5000, "Radius in metres.");
        var geo = GeoOption();
        var cmd = new Command("nearby", "Find stores near a lat/lng point.") { what, lat, lng, radius, geo };

        cmd.SetHandler(async (context) =>
        {
            var w = context.ParseResult.GetValueForArgument(what);
            var la = context.ParseResult.GetValueForOption(lat);
            var ln = context.ParseResult.GetValueForOption(lng);
            var g = context.ParseResult.GetValueForOption(geo);
            if (!Guard(context)) return;

            var agent = Composition.BuildAgent(g ?? "usa", Console.Error.WriteLine);
            var stores = await agent.FindStoresAsync(w, g, new GeoPoint(la, ln));
            PrintJson(stores);
        });

        return cmd;
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static Option<string?> GeoOption()
    {
        var geo = new Option<string?>("--geo", () => "usa",
            "Target market: jordan | saudi | uae | egypt | usa.");
        geo.AddAlias("-g");
        return geo;
    }

    private static bool Guard(System.CommandLine.Invocation.InvocationContext context)
    {
        if (Composition.HasLlm)
        {
            return true;
        }

        Console.Error.WriteLine("This command needs an LLM. Set ANTHROPIC_API_KEY or OPENAI_API_KEY.");
        context.ExitCode = 2;
        return false;
    }

    private static void PrintJson(object value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, JsonOut));

    private static void PrintSummaryThenJson(string summary, object full)
    {
        if (!string.IsNullOrWhiteSpace(summary))
        {
            Console.WriteLine(summary);
            Console.WriteLine();
            Console.WriteLine("--- full report (JSON) ---");
        }

        PrintJson(full);
    }
}
