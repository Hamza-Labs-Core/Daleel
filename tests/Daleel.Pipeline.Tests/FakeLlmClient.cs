using Daleel.Core.Llm;

namespace Daleel.Pipeline.Tests;

/// <summary>
/// A deterministic <see cref="ILlmClient"/> for tests: returns a canned response (or a
/// per-call function of the prompt) and records what it was asked, so tests can assert on
/// prompt content without any network.
/// </summary>
public sealed class FakeLlmClient : ILlmClient
{
    private readonly Func<string, IReadOnlyList<LlmMessage>, string> _responder;

    public string Provider => "fake";
    public List<(string System, IReadOnlyList<LlmMessage> Messages)> Calls { get; } = new();

    public FakeLlmClient(string cannedResponse) => _responder = (_, _) => cannedResponse;

    public FakeLlmClient(Func<string, IReadOnlyList<LlmMessage>, string> responder) => _responder = responder;

    public Task<LlmResponse> CompleteAsync(
        string systemPrompt, IReadOnlyList<LlmMessage> messages, CancellationToken cancellationToken = default)
    {
        Calls.Add((systemPrompt, messages));
        return Task.FromResult(new LlmResponse { Content = _responder(systemPrompt, messages), Model = "fake" });
    }
}
