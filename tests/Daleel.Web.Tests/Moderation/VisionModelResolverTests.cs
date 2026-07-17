using Daleel.Core.Llm;
using Daleel.Web.Data;
using Daleel.Web.Moderation;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Daleel.Web.Tests.Moderation;

/// <summary>
/// The vision model is admin-switchable at runtime: the screens read <c>model.vision</c> per call, so a
/// change at /admin/settings lands on the next screen. It used to be env-only, captured once in a
/// singleton's constructor — the dropdown could not have worked without this.
/// </summary>
public class VisionModelResolverTests
{
    private const string Fallback = "compiled/default";

    [Fact]
    public async Task Prefers_the_admin_setting()
    {
        var resolver = Build(new FakeConfig { ["model.vision"] = "admin/picked" });

        (await resolver.ResolveAsync(Fallback)).Should().Be("admin/picked");
    }

    [Fact]
    public async Task Reads_the_setting_on_EVERY_call_so_an_admin_switch_needs_no_redeploy()
    {
        var config = new FakeConfig { ["model.vision"] = "old/model" };
        var resolver = Build(config);

        (await resolver.ResolveAsync(Fallback)).Should().Be("old/model");
        config["model.vision"] = "new/model"; // the admin saves a different model at /admin/settings

        (await resolver.ResolveAsync(Fallback)).Should().Be("new/model",
            "a model captured once in the singleton's ctor is exactly the bug this replaces");
    }

    [Fact]
    public async Task Falls_back_to_the_env_var_when_the_setting_is_blank()
    {
        // DALEEL_MODERATION_VISION_MODEL configured this before the setting existed — an untouched
        // deployment must keep the model it is already running on.
        using var _ = new EnvVar(VisionModelResolver.EnvVar, "env/model");
        var resolver = Build(new FakeConfig { ["model.vision"] = "  " });

        (await resolver.ResolveAsync(Fallback)).Should().Be("env/model");
    }

    [Fact]
    public async Task The_setting_wins_over_the_env_var()
    {
        using var _ = new EnvVar(VisionModelResolver.EnvVar, "env/model");
        var resolver = Build(new FakeConfig { ["model.vision"] = "admin/picked" });

        (await resolver.ResolveAsync(Fallback)).Should().Be("admin/picked",
            "the admin's runtime choice is the whole point; env is only the bootstrap");
    }

    [Fact]
    public async Task Falls_back_to_the_callers_default_when_nothing_is_configured()
    {
        using var _ = new EnvVar(VisionModelResolver.EnvVar, null);

        (await Build(new FakeConfig()).ResolveAsync(Fallback)).Should().Be(Fallback);
    }

    [Fact]
    public async Task A_config_read_that_throws_degrades_instead_of_killing_the_screen()
    {
        // The halal screen is fail-CLOSED: if resolving a model threw, every image on the grid would be
        // hidden. Degrading to env/default keeps the screen running.
        using var _ = new EnvVar(VisionModelResolver.EnvVar, null);

        (await Build(new ThrowingConfig()).ResolveAsync(Fallback)).Should().Be(Fallback);
    }

    [Fact]
    public async Task The_product_screen_sends_the_resolved_model_on_the_wire()
    {
        // End to end through the screen itself: the model the resolver returns is what OpenRouter is asked
        // for, and a later switch changes the next call's model.
        var config = new FakeConfig { ["model.vision"] = "first/model" };
        var handler = new CapturingHandler();
        using var screen = new OpenRouterProductImageScreen(
            "key", Build(config), NullLogger<OpenRouterProductImageScreen>.Instance,
            cache: null, http: new HttpClient(handler));

        await screen.RejectNonProductShotsAsync(new[] { "https://store.jo/product/a.jpg" });
        config["model.vision"] = "second/model";
        await screen.RejectNonProductShotsAsync(new[] { "https://store.jo/product/b.jpg" });

        handler.Models.Should().Equal(new[] { "first/model", "second/model" });
    }

    [Fact]
    public void The_vision_call_site_is_registered_so_it_seeds_and_renders_in_the_admin_editor()
    {
        // /admin/settings turns any string row keyed model.* into a picker and labels it from this
        // registry — being here is what puts the vision model on the page.
        LlmCallSites.All.Should().Contain(LlmCallSites.Vision);
        LlmCallSites.Vision.ConfigKey.Should().Be("model.vision");
        SystemConfigService.Defaults.Should().ContainSingle(d => d.Key == "model.vision")
            .Which.Type.Should().Be("string", "only a string row renders as a model picker");
    }

    [Fact]
    public void The_seeded_vision_default_is_not_a_superseded_model()
    {
        // A row seeded with a superseded default would be silently rewritten by the next SeedDefaults pass.
        var seeded = SystemConfigService.Defaults.Single(d => d.Key == "model.vision").Value;

        seeded.Should().Be(LlmCallSites.Vision.DefaultModel);
        typeof(SystemConfigService)
            .GetField("SupersededModelDefaults", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null).As<HashSet<string>>()
            .Should().NotContain(seeded);
    }

    private static VisionModelResolver Build(ISystemConfigService config)
    {
        var services = new ServiceCollection();
        services.AddSingleton(config);
        return new VisionModelResolver(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
    }

    private sealed class FakeConfig : Dictionary<string, string>, ISystemConfigService
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(TryGetValue(key, out var v) ? v : null);
        public Task<int> GetIntAsync(string key, int fallback, CancellationToken ct = default) => Task.FromResult(fallback);
        public Task<bool> GetBoolAsync(string key, bool fallback, CancellationToken ct = default) => Task.FromResult(fallback);
        public Task SetAsync(string key, string value, string type = "string", CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<SystemConfig>> AllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SystemConfig>>(Array.Empty<SystemConfig>());
        public Task SeedDefaultsAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ThrowingConfig : ISystemConfigService
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            throw new InvalidOperationException("db is down");
        public Task<int> GetIntAsync(string key, int fallback, CancellationToken ct = default) => Task.FromResult(fallback);
        public Task<bool> GetBoolAsync(string key, bool fallback, CancellationToken ct = default) => Task.FromResult(fallback);
        public Task SetAsync(string key, string value, string type = "string", CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<SystemConfig>> AllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SystemConfig>>(Array.Empty<SystemConfig>());
        public Task SeedDefaultsAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Captures the `model` each request asks OpenRouter for; replies "reject nothing".</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Models { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(ct));
            Models.Add(doc.RootElement.GetProperty("model").GetString()!);

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"choices":[{"message":{"content":"{\"reject\":[]}"}}]}""",
                    System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    /// <summary>Sets an env var for the test and restores the prior value.</summary>
    private sealed class EnvVar : IDisposable
    {
        private readonly string _name;
        private readonly string? _prior;

        public EnvVar(string name, string? value)
        {
            _name = name;
            _prior = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _prior);
    }
}
