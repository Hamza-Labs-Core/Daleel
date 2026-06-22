using System.CommandLine;
using System.Text;
using System.Text.Json;
using Daleel.Apify;
using Daleel.Core.Arabic;
using Daleel.Core.Models;
using Daleel.Pipeline;

// Daleel (دليل — "guide") — Arabic social media intelligence CLI.
// Commands: search, monitor, test-match, dry-run.

Console.OutputEncoding = Encoding.UTF8;

var root = new RootCommand("Daleel — Arabic social media monitoring & intelligence tool.");
root.AddCommand(BuildSearchCommand());
root.AddCommand(BuildMonitorCommand());
root.AddCommand(BuildTestMatchCommand());
root.AddCommand(BuildDryRunCommand());

return await root.InvokeAsync(args);

// ─────────────────────────────────────────────────────────────────────────────
// search: fetch from a single Apify actor by keyword, match, and write results.
// ─────────────────────────────────────────────────────────────────────────────
static Command BuildSearchCommand()
{
    var keyword = new Option<string>("--keyword", "Arabic keyword(s) to search for, comma-separated.")
    {
        IsRequired = true
    };
    keyword.AddAlias("-k");

    var actor = new Option<string>("--actor", () => "scrapeforge/facebook-search-posts",
        "Apify actor id to run.");
    var max = new Option<int>("--max", () => 25, "Maximum items to fetch.");
    var mode = new Option<MatchMode>("--mode", () => MatchMode.Contains, "Match mode.");
    var output = new Option<string>("--out", () => "daleel-results.jsonl", "Output JSONL path.");

    var cmd = new Command("search", "Search an Apify actor for a keyword and write matches.")
    {
        keyword, actor, max, mode, output
    };

    cmd.SetHandler(async (context) =>
    {
        var keywords = context.ParseResult.GetValueForOption(keyword)!
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var actorId = context.ParseResult.GetValueForOption(actor)!;
        var maxItems = context.ParseResult.GetValueForOption(max);
        var matchMode = context.ParseResult.GetValueForOption(mode);
        var outPath = context.ParseResult.GetValueForOption(output)!;

        var token = Environment.GetEnvironmentVariable("APIFY_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine("APIFY_TOKEN is not set. Export it before running 'search'.");
            context.ExitCode = 2;
            return;
        }

        if (keywords.Length == 0)
        {
            Console.Error.WriteLine("No keywords supplied.");
            context.ExitCode = 2;
            return;
        }

        var job = new MonitoringJob
        {
            Name = "search",
            Keywords = keywords,
            Mode = matchMode,
            OutputPath = outPath,
            Sources = new[]
            {
                new Source
                {
                    Name = "facebook-search",
                    Kind = SourceKind.Search,
                    Target = keywords[0],
                    ActorId = actorId,
                    MaxItems = maxItems
                }
            }
        };

        using var client = new ApifyClient(token);
        var fetcher = new ApifyPostFetcher(client);
        var matcher = new ArabicMatcher();
        await using var writer = new JsonlResultWriter(outPath);

        var pipeline = new MonitoringPipeline(fetcher, matcher, writer, Console.WriteLine);
        try
        {
            var report = await pipeline.RunAsync(job);
            Console.WriteLine(
                $"\nMatches written to {outPath}: {report.Matches} " +
                $"(fetched {report.PostsFetched}, deduped {report.Duplicates}).");
        }
        catch (ApifyException ex)
        {
            Console.Error.WriteLine($"Apify error: {ex.Message}");
            context.ExitCode = 1;
        }
    });

    return cmd;
}

// ─────────────────────────────────────────────────────────────────────────────
// monitor: run a full job described by a JSON config file.
// ─────────────────────────────────────────────────────────────────────────────
static Command BuildMonitorCommand()
{
    var config = new Option<FileInfo>("--config", "Path to a JSON job/sources config file.")
    {
        IsRequired = true
    };
    config.AddAlias("-c");

    var cmd = new Command("monitor", "Run a monitoring job from a config file.") { config };

    cmd.SetHandler(async (context) =>
    {
        var file = context.ParseResult.GetValueForOption(config)!;
        if (!file.Exists)
        {
            Console.Error.WriteLine($"Config file not found: {file.FullName}");
            context.ExitCode = 2;
            return;
        }

        MonitoringJob? job;
        try
        {
            var json = await File.ReadAllTextAsync(file.FullName);
            job = JsonSerializer.Deserialize<MonitoringJob>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            });
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"Invalid config JSON: {ex.Message}");
            context.ExitCode = 2;
            return;
        }

        if (job is null || job.Sources.Count == 0)
        {
            Console.Error.WriteLine("Config has no sources.");
            context.ExitCode = 2;
            return;
        }

        var token = Environment.GetEnvironmentVariable("APIFY_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine("APIFY_TOKEN is not set. Export it before running 'monitor'.");
            context.ExitCode = 2;
            return;
        }

        using var client = new ApifyClient(token);
        var fetcher = new ApifyPostFetcher(client);
        var matcher = new ArabicMatcher();
        await using var writer = new JsonlResultWriter(job.OutputPath);

        var pipeline = new MonitoringPipeline(fetcher, matcher, writer, Console.WriteLine);
        try
        {
            var report = await pipeline.RunAsync(job);
            Console.WriteLine(
                $"\nJob '{job.Name}' complete. Matches: {report.Matches}, " +
                $"sources: {report.SourcesProcessed}, output: {job.OutputPath}");
        }
        catch (ApifyException ex)
        {
            Console.Error.WriteLine($"Apify error: {ex.Message}");
            context.ExitCode = 1;
        }
    });

    return cmd;
}

// ─────────────────────────────────────────────────────────────────────────────
// test-match: offline — match a keyword against supplied text, show the result.
// ─────────────────────────────────────────────────────────────────────────────
static Command BuildTestMatchCommand()
{
    var keyword = new Option<string>("--keyword", "Keyword to match.") { IsRequired = true };
    keyword.AddAlias("-k");
    var text = new Option<string>("--text", "Text to match against.") { IsRequired = true };
    text.AddAlias("-t");
    var mode = new Option<MatchMode>("--mode", () => MatchMode.Contains, "Match mode.");

    var cmd = new Command("test-match", "Offline: test Arabic matching of a keyword against text.")
    {
        keyword, text, mode
    };

    cmd.SetHandler((kw, txt, m) =>
    {
        var matcher = new ArabicMatcher();
        var result = matcher.Match(txt, new[] { kw }, m);

        Console.WriteLine($"keyword     : {kw}");
        Console.WriteLine($"normalized  : {ArabicNormalizer.Normalize(kw)}");
        Console.WriteLine($"text        : {txt}");
        Console.WriteLine($"norm. text  : {ArabicNormalizer.Normalize(txt)}");
        Console.WriteLine($"mode        : {m}");
        Console.WriteLine($"match       : {(result.IsMatch ? "YES" : "no")}");
        Console.WriteLine($"score       : {result.Score:0.000}");
        if (result.IsMatch)
        {
            Console.WriteLine($"context     : {result.Context}");
        }
    }, keyword, text, mode);

    return cmd;
}

// ─────────────────────────────────────────────────────────────────────────────
// dry-run: offline — run the normalizer/matcher self-test suite inline.
// ─────────────────────────────────────────────────────────────────────────────
static Command BuildDryRunCommand()
{
    var keyword = new Option<string>("--keyword", () => "شَرِكَة",
        "Keyword to demonstrate normalization & matching with.");
    keyword.AddAlias("-k");

    var cmd = new Command("dry-run", "Offline: demonstrate Arabic normalization on built-in cases.")
    {
        keyword
    };

    cmd.SetHandler((kw) =>
    {
        Console.WriteLine($"Daleel dry-run — normalization demo for: {kw}\n");

        var matcher = new ArabicMatcher();

        // (description, keyword, text, shouldMatch)
        var cases = new (string Desc, string Keyword, string Text, bool Expected)[]
        {
            ("diacritics stripped", "شَرِكَة", "هذا نص يتحدث عن شركة الاتصالات", true),
            ("taa marbuta vs haa", "شركة", "نشرت الشركه بيانا اليوم", true),
            ("hamza alef folding", "أخبار", "اخبار عاجلة وصلت الان", true),
            ("alef maksura vs yaa", "مصطفى", "تحدث مصطفي في المؤتمر", true),
            ("tatweel removed", "خبر", "خـــبر عاجل", true),
            ("unrelated text", "شركة", "القطة تجلس على الطاوله", false),
        };

        var passed = 0;
        foreach (var (desc, k, t, expected) in cases)
        {
            var r = matcher.Match(t, new[] { k }, MatchMode.Contains);
            var ok = r.IsMatch == expected;
            passed += ok ? 1 : 0;
            Console.WriteLine(
                $"[{(ok ? "PASS" : "FAIL")}] {desc,-22} " +
                $"'{k}' vs '{t}' → match={r.IsMatch} (expected {expected})");
        }

        Console.WriteLine();

        var norm = ArabicNormalizer.Normalize(kw);
        Console.WriteLine($"Normalized form of '{kw}': '{norm}'");

        Console.WriteLine($"\n{passed}/{cases.Length} built-in cases passed.");
        if (passed != cases.Length)
        {
            Environment.ExitCode = 1;
        }
    }, keyword);

    return cmd;
}
