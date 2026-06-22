# Daleel (دليل)

**Daleel** — Arabic for *"guide"* — is a social media intelligence tool focused on
**Arabic content monitoring**. It fetches posts from Facebook (via [Apify](https://apify.com)
actors), matches them against Arabic keywords with diacritic- and variant-aware
normalization, deduplicates, and writes the hits to a JSONL stream.

The hard part of monitoring Arabic content is matching. The same word can be written
many ways — with or without diacritics, with different alef/hamza/taa-marbuta forms,
with decorative tatweel. Daleel's core normalizer collapses all of these to a single
canonical form so a keyword like `شَرِكَة` reliably matches `شركة`, `الشركه`, and friends.

---

## Architecture

A .NET 8 solution following clean architecture — dependencies point inward toward the
domain core:

```
Daleel.Cli  ──►  Daleel.Pipeline  ──►  Daleel.Core  ◄──  Daleel.Apify
                                         (domain)
```

| Project            | Responsibility                                                        |
|--------------------|-----------------------------------------------------------------------|
| `Daleel.Core`      | Domain models, Arabic normalization/matching, pipeline interfaces. No external deps. |
| `Daleel.Apify`     | Apify REST client, actor input builders, dataset → `SocialPost` mapping. |
| `Daleel.Pipeline`  | Orchestration: fetch → normalize → match → dedup → write (JSONL).       |
| `Daleel.Cli`       | `System.CommandLine` console entry point.                              |

See [docs/architecture.md](docs/architecture.md) for the full design and data flow.

---

## The core feature: Arabic normalization

`ArabicNormalizer.Normalize` applies, in order:

1. Unicode NFC normalization
2. Diacritic / tashkeel removal (`U+064B`–`U+065F`, `U+0670`, …)
3. Alef-variant folding (`أ إ آ ٱ → ا`)
4. Alef-maksura → yaa (`ى → ي`)
5. Taa-marbuta → haa (`ة → ه`)
6. Tatweel (kashida `ـ`) removal
7. Hamza-carrier folding (`ؤ → و`, `ئ → ي`, standalone `ء` dropped)
8. Whitespace collapsing + Arabic-Indic digit folding

`ArabicMatcher` then compares normalized forms in one of three modes:

- **Exact** — normalized keyword equals the whole normalized text.
- **Contains** — normalized text contains the normalized keyword (default).
- **Fuzzy** — token-level Levenshtein distance under a configurable threshold,
  so single-character typos still match.

Multi-keyword input is supported; **any** keyword hit flags the post.

---

## Build, test, run

Requires the **.NET 8 SDK**.

```bash
dotnet build          # build the solution
dotnet test           # run all 65 tests (xUnit + FluentAssertions)
```

### CLI

```bash
# Offline: test matching of a keyword against text (no token needed)
dotnet run --project src/Daleel.Cli -- test-match \
  --keyword "شَرِكَة" --text "هذا نص يتحدث عن شركة الاتصالات"

# Offline: run the built-in normalization demo suite
dotnet run --project src/Daleel.Cli -- dry-run --keyword "شَرِكَة"

# Live: search a Facebook actor for a keyword (needs APIFY_TOKEN)
export APIFY_TOKEN=apify_xxx
dotnet run --project src/Daleel.Cli -- search \
  --keyword "شركة الاتصالات" \
  --actor scrapeforge/facebook-search-posts \
  --max 25

# Live: run a full job from a config file
dotnet run --project src/Daleel.Cli -- monitor --config sources.json
```

### Apify token

The `search` and `monitor` commands require an Apify API token, read from the
`APIFY_TOKEN` environment variable. The offline `test-match` and `dry-run` commands
need no token and exercise the Arabic engine directly.

---

## Output format

Matched posts are written as [JSON Lines](https://jsonlines.org/) — one JSON object
per line, append-friendly and stream-readable:

```json
{"Post":{"Id":"123","Text":"شركة الاتصالات تعلن...","Author":"...","Url":"..."},"Match":{"IsMatch":true,"Score":1.0,"MatchedKeyword":"شركة","Mode":"Contains","Context":"…شركه الاتصالات تعلن…"}}
```

Arabic is written un-escaped so the file is human-readable.

---

## Project layout

```
Daleel/
├── Daleel.sln
├── sources.json              # example monitor config
├── src/
│   ├── Daleel.Core/          # domain + Arabic engine
│   ├── Daleel.Apify/         # Apify integration
│   ├── Daleel.Pipeline/      # orchestration
│   └── Daleel.Cli/           # console entry point
├── tests/
│   ├── Daleel.Core.Tests/
│   └── Daleel.Pipeline.Tests/
└── docs/
    └── architecture.md
```
