using Daleel.Web.Services;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests;

/// <summary>
/// Key resolution is SERVER-ENVIRONMENT ONLY — there are no per-user/browser keys. These tests
/// save and restore any env var they touch so parallel-running suites never see a scrubbed key.
/// </summary>
public class AgentFactoryTests
{
    private readonly AgentFactory _factory = new();

    /// <summary>Runs <paramref name="body"/> with the given env vars set, restoring originals after.</summary>
    private static void WithEnv(IReadOnlyDictionary<string, string?> vars, Action body)
    {
        var saved = vars.Keys.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var (name, value) in vars)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
            body();
        }
        finally
        {
            foreach (var (name, value) in saved)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    [Fact]
    public void Resolve_ReadsFromTheServerEnvironment() =>
        WithEnv(new Dictionary<string, string?> { ["OPENROUTER_API_KEY"] = "from-env" }, () =>
            _factory.Resolve("OPENROUTER_API_KEY").Should().Be("from-env"));

    [Fact]
    public void Resolve_ReturnsNullWhenBlankOrMissing()
    {
        WithEnv(new Dictionary<string, string?> { ["SERPAPI_KEY"] = "   " }, () =>
            _factory.Resolve("SERPAPI_KEY").Should().BeNull("whitespace is not a key"));

        WithEnv(new Dictionary<string, string?> { ["SERPAPI_KEY"] = null }, () =>
            _factory.Resolve("SERPAPI_KEY").Should().BeNull());
    }

    [Fact]
    public void HasLlm_TrueWhenAnyLlmEnvKeySet() =>
        WithEnv(new Dictionary<string, string?>
        {
            ["OPENROUTER_API_KEY"] = "sk-test",
            ["OPENAI_API_KEY"] = null,
            ["ANTHROPIC_API_KEY"] = null
        }, () => _factory.HasLlm().Should().BeTrue());

    [Fact]
    public void Describe_FlagsProvidersFromTheEnvironment() =>
        WithEnv(new Dictionary<string, string?>
        {
            ["OPENROUTER_API_KEY"] = "sk-test",
            ["SERPAPI_KEY"] = "serp",
            ["GOOGLE_PLACES_API_KEY"] = "places",
            ["APIFY_TOKEN"] = "apify",
            ["CONTEXT_DEV_API_KEY"] = null,
            ["CLOUDFLARE_ACCOUNT_ID"] = null,
            ["CLOUDFLARE_API_TOKEN"] = null
        }, () =>
        {
            var status = _factory.Describe();

            status.Llm.Should().BeTrue();
            status.WebSearch.Should().BeTrue();
            status.Places.Should().BeTrue();
            status.Social.Should().BeTrue();
            status.Scraper.Should().BeFalse("no Context.dev / Cloudflare keys are set");
        });

    [Fact]
    public void Build_WiresAnAgentWhenLlmKeyPresent() =>
        WithEnv(new Dictionary<string, string?> { ["OPENROUTER_API_KEY"] = "sk-test" }, () =>
        {
            var agent = _factory.Build(new AgentRequest
            {
                Geo = "jordan",
                Model = "anthropic/claude-sonnet-4"
            });

            agent.Should().NotBeNull();
        });

    [Fact]
    public void Build_ThrowsWhenNoLlmKeyResolvable() =>
        WithEnv(new Dictionary<string, string?>
        {
            ["OPENROUTER_API_KEY"] = null,
            ["OPENAI_API_KEY"] = null,
            ["ANTHROPIC_API_KEY"] = null
        }, () =>
            _factory.Invoking(f => f.Build(new AgentRequest()))
                .Should().Throw<InvalidOperationException>());
}
