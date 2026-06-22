using Daleel.Web.Services;
using FluentAssertions;
using Xunit;

namespace Daleel.Web.Tests;

public class AgentFactoryTests
{
    private readonly AgentFactory _factory = new();

    [Fact]
    public void Resolve_PrefersUserKeysOverEnvironment()
    {
        const string name = "OPENROUTER_API_KEY";
        var original = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, "from-env");
            var keys = new Dictionary<string, string> { [name] = "from-user" };

            _factory.Resolve(name, keys).Should().Be("from-user");
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, original);
        }
    }

    [Fact]
    public void Resolve_ReturnsNullWhenBlankOrMissing()
    {
        var keys = new Dictionary<string, string> { ["SERPAPI_KEY"] = "   " };
        _factory.Resolve("SERPAPI_KEY", keys).Should().NotBe("   ");
    }

    [Fact]
    public void HasLlm_TrueWhenAnyLlmKeyProvided()
    {
        var keys = new Dictionary<string, string> { ["OPENROUTER_API_KEY"] = "sk-test" };
        _factory.HasLlm(keys).Should().BeTrue();
    }

    [Fact]
    public void Describe_FlagsProvidersFromKeys()
    {
        var keys = new Dictionary<string, string>
        {
            ["OPENROUTER_API_KEY"] = "sk-test",
            ["SERPAPI_KEY"] = "serp",
            ["GOOGLE_PLACES_API_KEY"] = "places",
            ["APIFY_TOKEN"] = "apify",
        };

        var status = _factory.Describe(keys);

        status.Llm.Should().BeTrue();
        status.WebSearch.Should().BeTrue();
        status.Places.Should().BeTrue();
        status.Social.Should().BeTrue();
        status.Scraper.Should().BeFalse(); // no Context.dev / Cloudflare keys supplied
    }

    [Fact]
    public void Build_WiresAnAgentWhenLlmKeyPresent()
    {
        var request = new AgentRequest
        {
            Geo = "jordan",
            Model = "anthropic/claude-sonnet-4",
            Keys = new Dictionary<string, string> { ["OPENROUTER_API_KEY"] = "sk-test" }
        };

        var agent = _factory.Build(request);

        agent.Should().NotBeNull();
    }

    [Fact]
    public void Build_ThrowsWhenNoLlmKeyResolvable()
    {
        // Empty keys + scrub LLM env vars so neither source resolves a key.
        var names = new[] { "OPENROUTER_API_KEY", "OPENAI_API_KEY", "ANTHROPIC_API_KEY" };
        var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var n in names)
            {
                Environment.SetEnvironmentVariable(n, null);
            }

            var request = new AgentRequest { Keys = new Dictionary<string, string>() };

            _factory.Invoking(f => f.Build(request))
                .Should().Throw<InvalidOperationException>();
        }
        finally
        {
            foreach (var (n, v) in saved)
            {
                Environment.SetEnvironmentVariable(n, v);
            }
        }
    }
}
