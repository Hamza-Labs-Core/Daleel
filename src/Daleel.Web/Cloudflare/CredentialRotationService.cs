using Daleel.Web.Data;

namespace Daleel.Web.Cloudflare;

/// <summary>
/// The app-to-app auth lifecycle for THIS environment's workers: names them from
/// <see cref="WorkerNames"/>, and drives ensure → push → (optional scheduled) rotate.
/// </summary>
public interface ICredentialRotationService
{
    /// <summary>The worker script names this environment owns.</summary>
    IReadOnlyList<string> WorkerScripts { get; }

    /// <summary>Vault name of a worker's bearer.</summary>
    string BearerNameFor(string scriptName);

    /// <summary>
    /// Ensures each worker's bearer exists in the vault (minting when missing) and pushes
    /// AUTH_TOKEN (+ AUTH_TOKEN_PREVIOUS) to the script. Returns how many scripts were synced.
    /// </summary>
    Task<int> EnsureAndPushAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Rotates one worker's bearer with a grace window: mints a new value, pushes the OLD one as
    /// AUTH_TOKEN_PREVIOUS and the new one as AUTH_TOKEN, so in-flight callers never see a 401.
    /// </summary>
    Task<bool> RotateWorkerAsync(string scriptName, CancellationToken ct = default);
}

/// <summary>Environment-scoped worker script naming (DALEEL_ENV: "prod" default, or "qa").</summary>
public static class WorkerNames
{
    public static readonly IReadOnlyList<string> Bases = new[]
    {
        "daleel-scrape-worker", "daleel-search-worker",
        "daleel-classify-worker", "daleel-extract-worker", "daleel-filter-worker"
    };

    public static string Suffix(IConfiguration config) => SuffixFor(config["DALEEL_ENV"]);

    /// <summary>"-qa" when the DALEEL_ENV discriminator is "qa", else "" (prod default).</summary>
    public static string SuffixFor(string? daleelEnv) =>
        string.Equals(daleelEnv?.Trim(), "qa", StringComparison.OrdinalIgnoreCase) ? "-qa" : "";

    public static IReadOnlyList<string> Scripts(IConfiguration config)
    {
        var suffix = Suffix(config);
        return Bases.Select(b => b + suffix).ToList();
    }

    /// <summary>
    /// Maps a legacy <c>CF_{X}_WORKER_TOKEN</c> env-var name to this environment's vault bearer name
    /// (<c>worker:daleel-{x}-worker[-qa]</c>), or null when the name isn't a known worker token. Lets
    /// every resolver that historically read those env vars pick up authority-minted bearers first.
    /// </summary>
    public static string? BearerAlias(string envName, string? daleelEnv)
    {
        const string prefix = "CF_";
        const string suffix = "_WORKER_TOKEN";
        if (!envName.StartsWith(prefix, StringComparison.Ordinal) ||
            !envName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return null;
        }

        var capability = envName[prefix.Length..^suffix.Length].ToLowerInvariant();
        var baseName = $"daleel-{capability}-worker";
        return Bases.Contains(baseName) ? $"worker:{baseName}{SuffixFor(daleelEnv)}" : null;
    }
}

/// <summary>
/// Background half of the token authority. On startup: loads the vault snapshot, then ensures and
/// pushes every worker bearer for this environment (retrying while workers aren't deployed yet).
/// Optionally rotates on a schedule (SystemConfig <c>credentials.rotation_days</c>; 0 = manual-only,
/// the default). A worker that hasn't received its bearer yet fails CLOSED (its authorize() 500s),
/// so the failure mode of a missed push is loud, never open.
/// </summary>
public sealed class CredentialRotationService : BackgroundService, ICredentialRotationService
{
    /// <summary>SystemConfig key: rotate every N days; 0 disables scheduled rotation.</summary>
    public const string RotationDaysKey = "credentials.rotation_days";

    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SteadyInterval = TimeSpan.FromHours(6);

    private readonly ICredentialVault _vault;
    private readonly ICloudflareSecretsClient _secrets;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<CredentialRotationService> _logger;

    public CredentialRotationService(
        ICredentialVault vault, ICloudflareSecretsClient secrets, IServiceScopeFactory scopeFactory,
        IConfiguration config, ILogger<CredentialRotationService> logger)
    {
        _vault = vault;
        _secrets = secrets;
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
        WorkerScripts = WorkerNames.Scripts(config);
    }

    public IReadOnlyList<string> WorkerScripts { get; }

    public string BearerNameFor(string scriptName) => $"worker:{scriptName}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The vault snapshot powers sync resolution (AgentFactory/clients) — load it first thing.
        await SafeRefreshAsync(stoppingToken).ConfigureAwait(false);

        if (!_secrets.IsConfigured)
        {
            _logger.LogInformation(
                "Credential rotation idle: CLOUDFLARE_API_TOKEN/ACCOUNT_ID not configured — workers keep " +
                "their deploy-time bearers (env fallback)");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var synced = 0;
            try
            {
                synced = await EnsureAndPushAllAsync(stoppingToken).ConfigureAwait(false);
                await MaybeScheduledRotationAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Credential sync pass failed");
            }

            // Fast retries until every script has accepted its bearer (covers first-deploy races),
            // then settle into a slow heartbeat that also re-pushes after worker re-deploys.
            var interval = synced < WorkerScripts.Count ? RetryInterval : SteadyInterval;
            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async Task<int> EnsureAndPushAllAsync(CancellationToken ct = default)
    {
        var synced = 0;
        foreach (var script in WorkerScripts)
        {
            ct.ThrowIfCancellationRequested();
            var name = BearerNameFor(script);
            var current = await _vault.GetOrMintAsync(name, ServiceCredentialKind.WorkerBearer, ct)
                .ConfigureAwait(false);

            if (await _secrets.PutSecretAsync(script, "AUTH_TOKEN", current, ct).ConfigureAwait(false))
            {
                await _vault.MarkPushedAsync(name, ct).ConfigureAwait(false);
                synced++;
            }
        }

        if (synced > 0)
        {
            _logger.LogInformation("Worker bearers synced: {Synced}/{Total}", synced, WorkerScripts.Count);
        }
        return synced;
    }

    public async Task<bool> RotateWorkerAsync(string scriptName, CancellationToken ct = default)
    {
        var name = BearerNameFor(scriptName);
        var (current, previous) = await _vault.RotateAsync(name, ct).ConfigureAwait(false);

        // Grace-window ordering: the OLD token becomes AUTH_TOKEN_PREVIOUS FIRST, so at no moment
        // does the worker reject either the old value (still held by in-flight callers and this
        // app's snapshot readers) or the new one.
        if (previous is not null &&
            !await _secrets.PutSecretAsync(scriptName, "AUTH_TOKEN_PREVIOUS", previous, ct).ConfigureAwait(false))
        {
            _logger.LogWarning("Rotation of {Script}: previous-token push failed; new token NOT pushed", scriptName);
            return false;
        }

        if (!await _secrets.PutSecretAsync(scriptName, "AUTH_TOKEN", current, ct).ConfigureAwait(false))
        {
            _logger.LogWarning("Rotation of {Script}: new-token push failed — worker still accepts the previous value", scriptName);
            return false;
        }

        await _vault.MarkPushedAsync(name, ct).ConfigureAwait(false);
        _logger.LogInformation("Rotated worker bearer for {Script}", scriptName);
        return true;
    }

    private async Task MaybeScheduledRotationAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<ISystemConfigService>();
        var days = await config.GetIntAsync(RotationDaysKey, 0, ct).ConfigureAwait(false);
        if (days <= 0)
        {
            return; // manual-only (default)
        }

        foreach (var script in WorkerScripts)
        {
            var meta = (await _vault.ListAsync(ct).ConfigureAwait(false))
                .FirstOrDefault(c => c.Name == BearerNameFor(script));
            var age = DateTimeOffset.UtcNow - (meta?.RotatedAt ?? meta?.CreatedAt ?? DateTimeOffset.UtcNow);
            if (meta is not null && age > TimeSpan.FromDays(days))
            {
                await RotateWorkerAsync(script, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task SafeRefreshAsync(CancellationToken ct)
    {
        try
        {
            await _vault.RefreshSnapshotAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Vault snapshot load failed at startup (migrations pending?) — retrying inline");
        }
    }
}
