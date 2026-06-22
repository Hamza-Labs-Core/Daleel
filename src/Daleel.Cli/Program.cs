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

var root = new RootCommand("Daleel — Arabic market & social-media intelligence tool.");

// Core matching/monitoring commands.
root.AddCommand(BuildSearchCommand());
root.AddCommand(BuildMonitorCommand());
root.AddCommand(BuildTestMatchCommand());
root.AddCommand(BuildDryRunCommand());

// LLM-agent intelligence commands (ask, brand, product, stores, deals, compare, reviews, nearby).
foreach (var agentCommand in Daleel.Cli.AgentCommands.All())
{
    root.AddCommand(agentCommand);
}

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
// monitor: run a job from a config file, OR monitor a keyword in a market on an
// optional repeating interval.
// ─────────────────────────────────────────────────────────────────────────────
static Command BuildMonitorCommand()
{
    var keyword = new Argument<string?>("keyword", () => null, "Keyword to monitor (omit when using --config).");
    var config = new Option<FileInfo?>("--config", "Path to a JSON job/sources config file.");
    config.AddAlias("-c");
    var geo = new Option<string?>("--geo", () => "jordan", "Market profile for keyword monitoring.");
    geo.AddAlias("-g");
    var interval = new Option<string?>("--interval", "Repeat interval, e.g. 30m, 6h, 24h (omit to run once).");

    var cmd = new Command("monitor", "Run a monitoring job (config file or keyword + market).")
    {
        keyword, config, geo, interval
    };

    cmd.SetHandler(async (context) =>
    {
        var kw = context.ParseResult.GetValueForArgument(keyword);
        var file = context.ParseResult.GetValueForOption(config);
        var geoKey = context.ParseResult.GetValueForOption(geo);
        var intervalRaw = context.ParseResult.GetValueForOption(interval);

        MonitoringJob? job;
        if (file is not null)
        {
            if (!file.Exists)
            {
                Console.Error.WriteLine($"Config file not found: {file.FullName}");
                context.ExitCode = 2;
                return;
            }

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
        }
        else if (!string.IsNullOrWhiteSpace(kw))
        {
            job = BuildJobFromKeyword(kw, geoKey);
        }
        else
        {
            Console.Error.WriteLine("Provide a keyword argument or --config.");
            context.ExitCode = 2;
            return;
        }

        if (job is null || job.Sources.Count == 0)
        {
            Console.Error.WriteLine("No sources to monitor.");
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

        TimeSpan? every = null;
        if (!string.IsNullOrWhiteSpace(intervalRaw))
        {
            if (!TryParseInterval(intervalRaw, out var parsed))
            {
                Console.Error.WriteLine($"Could not parse interval '{intervalRaw}'. Use forms like 30m, 6h, 24h.");
                context.ExitCode = 2;
                return;
            }
            every = parsed;
        }

        using var client = new ApifyClient(token);
        var fetcher = new ApifyPostFetcher(client);
        var matcher = new ArabicMatcher();

        do
        {
            await using var writer = new JsonlResultWriter(job.OutputPath, append: true);
            var pipeline = new MonitoringPipeline(fetcher, matcher, writer, Console.WriteLine);
            try
            {
                var report = await pipeline.RunAsync(job);
                Console.WriteLine(
                    $"\nJob '{job.Name}' run complete. Matches: {report.Matches}, " +
                    $"sources: {report.SourcesProcessed}, output: {job.OutputPath}");
            }
            catch (ApifyException ex)
            {
                Console.Error.WriteLine($"Apify error: {ex.Message}");
                context.ExitCode = 1;
                return;
            }

            if (every is { } wait)
            {
                Console.WriteLine($"Next run in {intervalRaw}. Press Ctrl+C to stop.");
                await Task.Delay(wait);
            }
        }
        while (every is not null);
    });

    return cmd;
}

/// <summary>Builds a single-source keyword monitoring job for a market profile.</summary>
static MonitoringJob BuildJobFromKeyword(string keyword, string? geoKey)
{
    var profile = Daleel.Core.Geo.GeoProfiles.ResolveOrDefault(geoKey);
    var actor = profile.ApifyActors.FirstOrDefault() ?? "scrapeforge/facebook-search-posts";

    return new MonitoringJob
    {
        Name = $"monitor-{profile.Key}",
        Keywords = new[] { keyword },
        Mode = MatchMode.Contains,
        OutputPath = $"daleel-{profile.Key}-monitor.jsonl",
        Sources = new[]
        {
            new Source
            {
                Name = $"{profile.Key}-facebook",
                Kind = SourceKind.Search,
                Target = keyword,
                ActorId = actor,
                MaxItems = 50
            }
        }
    };
}

/// <summary>Parses an interval like "30m", "6h", "24h", "45s", "2d".</summary>
static bool TryParseInterval(string raw, out TimeSpan interval)
{
    interval = default;
    raw = raw.Trim().ToLowerInvariant();
    if (raw.Length < 2)
    {
        return false;
    }

    var unit = raw[^1];
    if (!double.TryParse(raw[..^1], System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var value))
    {
        return false;
    }

    interval = unit switch
    {
        's' => TimeSpan.FromSeconds(value),
        'm' => TimeSpan.FromMinutes(value),
        'h' => TimeSpan.FromHours(value),
        'd' => TimeSpan.FromDays(value),
        _ => TimeSpan.Zero
    };

    return interval > TimeSpan.Zero;
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
