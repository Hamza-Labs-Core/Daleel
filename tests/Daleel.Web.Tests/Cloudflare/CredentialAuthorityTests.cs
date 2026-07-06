using Daleel.Web.Cloudflare;
using Daleel.Web.Data;
using Daleel.Web.Services;
using Daleel.Web.Tests.Data;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Daleel.Web.Tests.Cloudflare;

// ─────────────────────────────────────────────────────────────────────────────────────────────
// WorkerNames: environment-scoped script naming + the CF_*_WORKER_TOKEN → vault-name alias.
// ─────────────────────────────────────────────────────────────────────────────────────────────
public class WorkerNamesTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("prod", "")]
    [InlineData("qa", "-qa")]
    [InlineData(" QA ", "-qa")] // trimmed + case-insensitive
    public void SuffixFor_maps_daleel_env(string? env, string expected) =>
        WorkerNames.SuffixFor(env).Should().Be(expected);

    [Fact]
    public void Scripts_appends_environment_suffix_to_every_base()
    {
        var qa = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DALEEL_ENV"] = "qa" }).Build();

        WorkerNames.Scripts(qa).Should().OnlyContain(s => s.EndsWith("-qa"))
            .And.HaveCount(WorkerNames.Bases.Count);
        WorkerNames.Scripts(new ConfigurationBuilder().Build())
            .Should().BeEquivalentTo(WorkerNames.Bases);
    }

    [Theory]
    [InlineData("CF_SEARCH_WORKER_TOKEN", null, "worker:daleel-search-worker")]
    [InlineData("CF_SCRAPE_WORKER_TOKEN", "qa", "worker:daleel-scrape-worker-qa")]
    [InlineData("CF_CLASSIFY_WORKER_TOKEN", "prod", "worker:daleel-classify-worker")]
    [InlineData("CF_EXTRACT_WORKER_TOKEN", null, "worker:daleel-extract-worker")]
    [InlineData("CF_FILTER_WORKER_TOKEN", "qa", "worker:daleel-filter-worker-qa")]
    public void BearerAlias_maps_known_worker_tokens(string envName, string? daleelEnv, string expected) =>
        WorkerNames.BearerAlias(envName, daleelEnv).Should().Be(expected);

    [Theory]
    [InlineData("CF_SEARCH_WORKER_URL")]   // not a token name
    [InlineData("CF_BOGUS_WORKER_TOKEN")]  // not a known worker
    [InlineData("SERPAPI_KEY")]            // vendor key — resolves by its own name
    [InlineData("CF__WORKER_TOKEN")]       // empty capability
    public void BearerAlias_rejects_everything_else(string envName) =>
        WorkerNames.BearerAlias(envName, null).Should().BeNull();
}

// ─────────────────────────────────────────────────────────────────────────────────────────────
// CredentialVault against real Postgres: mint/rotate/set/snapshot semantics + encryption at rest.
// ─────────────────────────────────────────────────────────────────────────────────────────────
public class CredentialVaultTests : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly CredentialVault _vault;

    public CredentialVaultTests()
    {
        var connStr = PostgresTestServer.CreateFreshDatabase();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<DaleelDbContext>(o => o.UseNpgsql(connStr));
        _provider = services.BuildServiceProvider();

        using (var scope = _provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<DaleelDbContext>().Database.EnsureCreated();
        }

        // Ephemeral Data-Protection keys — same API surface as production, no key ring on disk.
        _vault = new CredentialVault(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            new EphemeralDataProtectionProvider(),
            NullLogger<CredentialVault>.Instance);
    }

    private DaleelDbContext NewDb() => _provider.CreateScope().ServiceProvider.GetRequiredService<DaleelDbContext>();

    [Fact]
    public async Task GetOrMint_is_idempotent_and_encrypts_at_rest()
    {
        var first = await _vault.GetOrMintAsync("worker:daleel-scrape-worker", ServiceCredentialKind.WorkerBearer);
        var second = await _vault.GetOrMintAsync("worker:daleel-scrape-worker", ServiceCredentialKind.WorkerBearer);

        second.Should().Be(first, "an existing credential must never be silently re-minted");
        first.Should().HaveLength(64).And.MatchRegex("^[0-9a-f]+$"); // 256-bit hex token

        await using var db = NewDb();
        var row = await db.ServiceCredentials.SingleAsync(c => c.Name == "worker:daleel-scrape-worker");
        row.EncryptedValue.Should().NotContain(first, "plaintext tokens must never reach the database");
        row.Kind.Should().Be(ServiceCredentialKind.WorkerBearer);
    }

    [Fact]
    public async Task Rotate_demotes_current_to_previous_and_updates_snapshot()
    {
        var original = await _vault.GetOrMintAsync("worker:daleel-search-worker", ServiceCredentialKind.WorkerBearer);
        var (next, previous) = await _vault.RotateAsync("worker:daleel-search-worker");

        previous.Should().Be(original, "the grace window pushes the OLD value as AUTH_TOKEN_PREVIOUS");
        next.Should().NotBe(original);
        _vault.TryGetCached("worker:daleel-search-worker").Should().Be(next, "outbound clients read the snapshot");
        (await _vault.GetAsync("worker:daleel-search-worker")).Should().Be(next);

        await using var db = NewDb();
        var row = await db.ServiceCredentials.SingleAsync(c => c.Name == "worker:daleel-search-worker");
        row.RotatedAt.Should().NotBeNull();
        row.EncryptedPreviousValue.Should().NotBeNull();
    }

    [Fact]
    public async Task Rotate_without_existing_credential_throws()
    {
        var act = () => _vault.RotateAsync("worker:daleel-ghost-worker");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*mint it first*");
    }

    [Fact]
    public async Task Set_overwrites_vendor_key_and_snapshot_survives_refresh()
    {
        await _vault.SetAsync("SERPAPI_KEY", "v1", ServiceCredentialKind.VendorKey, "initial");
        await _vault.SetAsync("SERPAPI_KEY", "v2", ServiceCredentialKind.VendorKey);

        _vault.TryGetCached("SERPAPI_KEY").Should().Be("v2");
        await _vault.RefreshSnapshotAsync(); // reload from DB — decrypted value must round-trip
        _vault.TryGetCached("SERPAPI_KEY").Should().Be("v2");

        (await _vault.ListAsync()).Should().ContainSingle(c => c.Name == "SERPAPI_KEY")
            .Which.RotatedAt.Should().NotBeNull("overwriting records a rotation");
    }

    [Fact]
    public async Task Revoked_credentials_resolve_as_absent_after_refresh()
    {
        await _vault.SetAsync("APIFY_TOKEN", "secret", ServiceCredentialKind.VendorKey);
        await using (var db = NewDb())
        {
            var row = await db.ServiceCredentials.SingleAsync(c => c.Name == "APIFY_TOKEN");
            row.Revoked = true;
            await db.SaveChangesAsync();
        }

        (await _vault.GetAsync("APIFY_TOKEN")).Should().BeNull();
        await _vault.RefreshSnapshotAsync();
        _vault.TryGetCached("APIFY_TOKEN").Should().BeNull("revocation must evict the sync snapshot too");
    }

    [Fact]
    public async Task MarkPushed_stamps_the_row()
    {
        await _vault.GetOrMintAsync("worker:daleel-filter-worker", ServiceCredentialKind.WorkerBearer);
        await _vault.MarkPushedAsync("worker:daleel-filter-worker");

        (await _vault.ListAsync()).Single(c => c.Name == "worker:daleel-filter-worker")
            .PushedAt.Should().NotBeNull();
    }

    public void Dispose() => _provider.Dispose();
}

// ─────────────────────────────────────────────────────────────────────────────────────────────
// CredentialRotationService over fakes: ensure/push accounting and grace-window push ordering.
// ─────────────────────────────────────────────────────────────────────────────────────────────
public class CredentialRotationServiceTests
{
    private sealed class FakeVault : ICredentialVault
    {
        private readonly Dictionary<string, string> _values = new();
        private int _mintCounter;
        public List<string> Pushed { get; } = new();

        public Task<string?> GetAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(_values.TryGetValue(name, out var v) ? v : null);

        public string? TryGetCached(string name) => _values.TryGetValue(name, out var v) ? v : null;

        public Task<string> GetOrMintAsync(string name, string kind, CancellationToken ct = default)
        {
            if (!_values.TryGetValue(name, out var v))
            {
                v = $"minted-{++_mintCounter}";
                _values[name] = v;
            }
            return Task.FromResult(v);
        }

        public Task<(string Current, string? Previous)> RotateAsync(string name, CancellationToken ct = default)
        {
            _values.TryGetValue(name, out var previous);
            var next = $"minted-{++_mintCounter}";
            _values[name] = next;
            return Task.FromResult((next, previous));
        }

        public Task SetAsync(string name, string value, string kind, string? notes = null, CancellationToken ct = default)
        {
            _values[name] = value;
            return Task.CompletedTask;
        }

        public Task MarkPushedAsync(string name, CancellationToken ct = default)
        {
            Pushed.Add(name);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ServiceCredential>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCredential>>(Array.Empty<ServiceCredential>());

        public Task RefreshSnapshotAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeSecrets : ICloudflareSecretsClient
    {
        /// <summary>Ordered (script, secretName, value) log — ordering IS the grace-window contract.</summary>
        public List<(string Script, string Name, string Value)> Puts { get; } = new();

        /// <summary>Scripts (or script+secret pairs) whose pushes fail, e.g. not deployed yet.</summary>
        public Func<string, string, bool> Fails { get; set; } = (_, _) => false;

        public bool IsConfigured => true;

        public Task<bool> PutSecretAsync(string scriptName, string secretName, string value, CancellationToken ct = default)
        {
            if (Fails(scriptName, secretName))
            {
                return Task.FromResult(false);
            }
            Puts.Add((scriptName, secretName, value));
            return Task.FromResult(true);
        }
    }

    private static CredentialRotationService Build(FakeVault vault, FakeSecrets secrets, string? daleelEnv = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DALEEL_ENV"] = daleelEnv }).Build();
        return new CredentialRotationService(
            vault, secrets,
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            config, NullLogger<CredentialRotationService>.Instance);
    }

    [Fact]
    public async Task EnsureAndPushAll_mints_and_pushes_every_environment_script()
    {
        var vault = new FakeVault();
        var secrets = new FakeSecrets();
        var svc = Build(vault, secrets, daleelEnv: "qa");

        var synced = await svc.EnsureAndPushAllAsync();

        synced.Should().Be(WorkerNames.Bases.Count);
        secrets.Puts.Should().OnlyContain(p => p.Name == "AUTH_TOKEN")
            .And.OnlyContain(p => p.Script.EndsWith("-qa"), "the QA authority must never touch prod scripts");
        vault.Pushed.Should().HaveCount(WorkerNames.Bases.Count);
    }

    [Fact]
    public async Task EnsureAndPushAll_counts_only_accepted_pushes()
    {
        var vault = new FakeVault();
        var secrets = new FakeSecrets
        {
            // One worker not deployed yet → its push is refused (Cloudflare 404).
            Fails = (script, _) => script == "daleel-search-worker"
        };
        var svc = Build(vault, secrets);

        var synced = await svc.EnsureAndPushAllAsync();

        synced.Should().Be(WorkerNames.Bases.Count - 1, "an unaccepted push must keep the retry loop hot");
        vault.Pushed.Should().NotContain(svc.BearerNameFor("daleel-search-worker"));
    }

    [Fact]
    public async Task Rotate_pushes_previous_token_before_the_new_one()
    {
        var vault = new FakeVault();
        var secrets = new FakeSecrets();
        var svc = Build(vault, secrets);
        var old = await vault.GetOrMintAsync(svc.BearerNameFor("daleel-scrape-worker"), ServiceCredentialKind.WorkerBearer);

        (await svc.RotateWorkerAsync("daleel-scrape-worker")).Should().BeTrue();

        // Grace-window ordering: at no instant may the worker reject BOTH tokens.
        secrets.Puts.Should().HaveCount(2);
        secrets.Puts[0].Should().Be(("daleel-scrape-worker", "AUTH_TOKEN_PREVIOUS", old));
        secrets.Puts[1].Name.Should().Be("AUTH_TOKEN");
        secrets.Puts[1].Value.Should().NotBe(old);
        vault.Pushed.Should().Contain(svc.BearerNameFor("daleel-scrape-worker"));
    }

    [Fact]
    public async Task Rotate_aborts_before_the_new_token_when_the_grace_push_fails()
    {
        var vault = new FakeVault();
        var secrets = new FakeSecrets { Fails = (_, name) => name == "AUTH_TOKEN_PREVIOUS" };
        var svc = Build(vault, secrets);
        await vault.GetOrMintAsync(svc.BearerNameFor("daleel-extract-worker"), ServiceCredentialKind.WorkerBearer);

        (await svc.RotateWorkerAsync("daleel-extract-worker")).Should().BeFalse();

        secrets.Puts.Should().BeEmpty(
            "pushing the new AUTH_TOKEN without the grace token live would 401 every in-flight caller");
        vault.Pushed.Should().BeEmpty();
        svc.RotationRepushPending.Should().BeFalse(
            "the snapshot never advanced (grace push failed first), so there is nothing to re-push");
    }

    // ── FIX 4(a): a failed NEW-token push must not strand the app on the 6h heartbeat ───────────

    [Fact]
    public async Task Rotate_newTokenPushFails_armsFastRepush_and_recovers_on_next_ensure()
    {
        // RotateAsync ALWAYS advances the vault snapshot before the worker accepts the new token. If
        // the AUTH_TOKEN push then fails, the app presents `next` but the worker holds only the old
        // value — every app→worker call 401s. The service must arm a FAST re-push (RetryInterval),
        // not wait for the 6h steady heartbeat; the next EnsureAndPushAllAsync must re-PUT the
        // advanced AUTH_TOKEN and heal the gap.
        var vault = new FakeVault();
        // AUTH_TOKEN_PREVIOUS succeeds; only the new-token push fails (a transient Cloudflare blip).
        var secrets = new FakeSecrets { Fails = (_, name) => name == "AUTH_TOKEN" };
        var svc = Build(vault, secrets);
        await vault.GetOrMintAsync(svc.BearerNameFor("daleel-scrape-worker"), ServiceCredentialKind.WorkerBearer);

        (await svc.RotateWorkerAsync("daleel-scrape-worker")).Should().BeFalse();
        svc.RotationRepushPending.Should().BeTrue(
            "the snapshot advanced but the worker never got the new token — a fast re-push must be armed");

        // The advanced token the app now presents (what EnsureAndPushAllAsync must re-deliver).
        var advanced = await vault.GetAsync(svc.BearerNameFor("daleel-scrape-worker"));
        secrets.Puts.Should().NotContain(p => p.Name == "AUTH_TOKEN",
            "the failed rotation delivered no AUTH_TOKEN");

        // Simulate the FAST-retry loop's corrective pass: the blip has cleared, pushes now succeed.
        secrets.Fails = (_, _) => false;
        await svc.EnsureAndPushAllAsync();

        secrets.Puts.Should().Contain(p => p.Script == "daleel-scrape-worker" && p.Name == "AUTH_TOKEN" && p.Value == advanced,
            "the re-push delivers the advanced AUTH_TOKEN the app is already presenting — closing the 401 window");
    }
}

// ─────────────────────────────────────────────────────────────────────────────────────────────
// AgentFactory.Resolve: the vault snapshot wins over env, including via the worker-bearer alias.
// ─────────────────────────────────────────────────────────────────────────────────────────────
public class AgentFactoryVaultResolutionTests
{
    private sealed class SnapshotOnlyVault : ICredentialVault
    {
        public Dictionary<string, string> Snapshot { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? TryGetCached(string name) => Snapshot.TryGetValue(name, out var v) ? v : null;

        public Task<string?> GetAsync(string name, CancellationToken ct = default) => Task.FromResult(TryGetCached(name));
        public Task<string> GetOrMintAsync(string name, string kind, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(string Current, string? Previous)> RotateAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetAsync(string name, string value, string kind, string? notes = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task MarkPushedAsync(string name, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ServiceCredential>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ServiceCredential>>(Array.Empty<ServiceCredential>());
        public Task RefreshSnapshotAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static (AgentFactory Factory, SnapshotOnlyVault Vault) Build()
    {
        var vault = new SnapshotOnlyVault();
        var services = new ServiceCollection();
        services.AddSingleton<ICredentialVault>(vault);
        return (new AgentFactory(services.BuildServiceProvider()), vault);
    }

    /// <summary>Sets env vars for the body, restoring the previous values afterwards.</summary>
    private static void WithEnv(IReadOnlyDictionary<string, string?> vars, Action body)
    {
        var saved = vars.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var (k, v) in vars) Environment.SetEnvironmentVariable(k, v);
            body();
        }
        finally
        {
            foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v);
        }
    }

    [Fact]
    public void Worker_token_names_resolve_to_the_vault_bearer_over_env()
    {
        var (factory, vault) = Build();
        vault.Snapshot["worker:daleel-search-worker"] = "vault-bearer";

        WithEnv(new Dictionary<string, string?>
        {
            ["CF_SEARCH_WORKER_TOKEN"] = "stale-env-token",
            ["DALEEL_ENV"] = null
        }, () => factory.Resolve("CF_SEARCH_WORKER_TOKEN").Should().Be(
            "vault-bearer", "a rotated bearer must win over the deploy-time env token"));
    }

    [Fact]
    public void Bearer_alias_respects_the_qa_environment_suffix()
    {
        var (factory, vault) = Build();
        vault.Snapshot["worker:daleel-scrape-worker-qa"] = "qa-bearer";
        vault.Snapshot["worker:daleel-scrape-worker"] = "prod-bearer";

        WithEnv(new Dictionary<string, string?> { ["DALEEL_ENV"] = "qa" },
            () => factory.Resolve("CF_SCRAPE_WORKER_TOKEN").Should().Be("qa-bearer"));
        WithEnv(new Dictionary<string, string?> { ["DALEEL_ENV"] = null },
            () => factory.Resolve("CF_SCRAPE_WORKER_TOKEN").Should().Be("prod-bearer"));
    }

    [Fact]
    public void Env_remains_the_bootstrap_fallback_when_the_vault_is_empty()
    {
        var (factory, _) = Build();
        WithEnv(new Dictionary<string, string?>
        {
            ["CF_FILTER_WORKER_TOKEN"] = "deploy-time-token",
            ["DALEEL_ENV"] = null
        }, () => factory.Resolve("CF_FILTER_WORKER_TOKEN").Should().Be("deploy-time-token"));
    }

    [Fact]
    public void Vendor_keys_resolve_by_their_own_name_from_the_vault()
    {
        var (factory, vault) = Build();
        vault.Snapshot["SERPAPI_KEY"] = "vault-vendor-key";

        WithEnv(new Dictionary<string, string?> { ["SERPAPI_KEY"] = "env-vendor-key" },
            () => factory.Resolve("SERPAPI_KEY").Should().Be(
                "vault-vendor-key", "admin-managed keys override the environment without a redeploy"));
    }
}
