using Daleel.Core.Llm;
using Daleel.Web.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Daleel.Web.Moderation;

/// <summary>
/// Resolves the model the vision screens run on, for ONE call.
/// </summary>
/// <remarks>
/// The vision classifiers are singletons, so a model captured in their constructor is frozen for the
/// life of the process — the reason this knob used to need a redeploy. Resolving per call is what makes
/// the <c>model.vision</c> row at /admin/settings take effect on the next screen instead.
/// <para>
/// Order: the admin setting (<see cref="LlmCallSites.Vision"/>) wins, the
/// <c>DALEEL_MODERATION_VISION_MODEL</c> env var is the bootstrap fallback (it is what configured this
/// before the setting existed, so an untouched deployment keeps its behaviour), and the caller's own
/// compiled default is the last resort.
/// </para>
/// <para>
/// Best-effort by design: a config read that throws must never take down a screen — the halal screen is
/// fail-CLOSED, so failing to resolve a model would hide every image on the grid.
/// </para>
/// </remarks>
public interface IVisionModelResolver
{
    /// <summary>The model for this call, falling back to <paramref name="fallback"/> when unconfigured.</summary>
    Task<string> ResolveAsync(string fallback, CancellationToken ct = default);
}

public sealed class VisionModelResolver : IVisionModelResolver
{
    internal const string EnvVar = "DALEEL_MODERATION_VISION_MODEL";

    private readonly IServiceScopeFactory? _scopes;

    // Scopes are optional so a test (or a DI-less caller) can construct the classifiers without a
    // container; with none, resolution degrades to env → fallback, i.e. the pre-setting behaviour.
    public VisionModelResolver(IServiceScopeFactory? scopes = null) => _scopes = scopes;

    /// <summary>A resolver that always returns <paramref name="model"/> — for tests and hard pins.</summary>
    public static IVisionModelResolver Pinned(string model) => new PinnedResolver(model);

    public async Task<string> ResolveAsync(string fallback, CancellationToken ct = default)
    {
        if (_scopes is not null)
        {
            try
            {
                // A fresh scope per call: ISystemConfigService owns a transient DbContext, which must
                // never be resolved from (or captured by) this singleton. The service's 30s snapshot
                // cache keeps this off the wire on the per-image hot path.
                using var scope = _scopes.CreateScope();
                var config = scope.ServiceProvider.GetService<ISystemConfigService>();
                var configured = config is null
                    ? null
                    : await config.GetAsync(LlmCallSites.Vision.ConfigKey, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(configured))
                {
                    return configured!;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Fall through to env/fallback — a screen that still runs on the previous model beats
                // one that can't run at all.
            }
        }

        var env = Environment.GetEnvironmentVariable(EnvVar);
        return string.IsNullOrWhiteSpace(env) ? fallback : env!;
    }

    private sealed class PinnedResolver(string model) : IVisionModelResolver
    {
        public Task<string> ResolveAsync(string fallback, CancellationToken ct = default) =>
            Task.FromResult(model);
    }
}
