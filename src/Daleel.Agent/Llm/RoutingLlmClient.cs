using Daleel.Core.Llm;

namespace Daleel.Agent.Llm;

/// <summary>
/// An <see cref="ILlmClient"/> that routes each completion to the model configured for the CURRENT
/// call-site (<see cref="LlmCallSiteScope"/>), building one backing client per distinct model on
/// demand. This is what gives every pipeline step its own model — for independent cost tuning —
/// without threading a model argument through the whole call graph: a step opens a call-site scope
/// and calls the client as usual.
/// </summary>
/// <remarks>
/// Each backing client is expected to already be logging-wrapped by its factory, so the metered call
/// still carries the real provider/model; the call-site itself is read from the same ambient scope by
/// the logging client. A completion made outside any scope falls back to <paramref name="defaultModel"/>.
/// </remarks>
public sealed class RoutingLlmClient : ILlmClient
{
    private readonly Func<string, string> _modelForCallSite;
    private readonly Func<string, ILlmClient> _clientForModel;
    private readonly string _defaultModel;
    private readonly Dictionary<string, ILlmClient> _clients = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <param name="modelForCallSite">Resolves a call-site key to its configured model id (pre-snapshotted).</param>
    /// <param name="clientForModel">Builds (and logging-wraps) a backing client pinned to a given model.</param>
    /// <param name="defaultModel">Model used for completions made outside any call-site scope.</param>
    public RoutingLlmClient(
        Func<string, string> modelForCallSite,
        Func<string, ILlmClient> clientForModel,
        string defaultModel)
    {
        _modelForCallSite = modelForCallSite ?? throw new ArgumentNullException(nameof(modelForCallSite));
        _clientForModel = clientForModel ?? throw new ArgumentNullException(nameof(clientForModel));
        _defaultModel = string.IsNullOrWhiteSpace(defaultModel) ? LlmCallSites.DefaultModel : defaultModel;
    }

    public string Provider => "routing";

    public Task<LlmResponse> CompleteAsync(
        string systemPrompt, IReadOnlyList<LlmMessage> messages, CancellationToken cancellationToken = default)
    {
        var callSite = LlmCallSiteScope.Current;
        var model = callSite is null ? _defaultModel : _modelForCallSite(callSite);
        if (string.IsNullOrWhiteSpace(model))
        {
            model = _defaultModel;
        }

        return ResolveClient(model).CompleteAsync(systemPrompt, messages, cancellationToken);
    }

    private ILlmClient ResolveClient(string model)
    {
        lock (_gate)
        {
            if (!_clients.TryGetValue(model, out var client))
            {
                client = _clientForModel(model);
                _clients[model] = client;
            }

            return client;
        }
    }
}
