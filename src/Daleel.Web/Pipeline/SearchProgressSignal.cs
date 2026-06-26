namespace Daleel.Web.Pipeline;

/// <summary>
/// The user-visible stages of a search, in execution order. Each maps to one node on the UI's
/// horizontal stepper. Several of the nine Elsa activities collapse into one display step (moderate +
/// cache + aggregate all read as "comparing/finalizing"), and the enrichment activity drives two
/// (<see cref="BuildingProfiles"/> for its brand loop, then <see cref="FindingStores"/> for its store
/// loop) — so the order here is the order the pipeline actually reaches them, keeping the stepper
/// monotonic (it never jumps backwards).
/// </summary>
public enum SearchStep
{
    Analyzing = 0,        // 🔍 Analyzing the query + planning
    CheckingVault = 1,    // 📦 Checking the answers cache
    SearchingWeb = 2,     // 🌐 Fanning out across providers (with source counts)
    ExtractingProducts = 3, // 🏷️ LLM analyst + product extraction
    BuildingProfiles = 4, // ⭐ Joining saved brand profiles
    FindingStores = 5,    // 🏪 Verifying stores on Google Maps
    ComparingPrices = 6,  // 📊 Aggregate + moderate + cache (ranking)
    Done = 7              // ✅ Finished
}

/// <summary>
/// A single progress update encoded into the existing <c>string</c> progress channel. The pipeline
/// emits these so the UI can (a) advance the stepper to the right <see cref="Step"/> and (b) localize
/// the live activity line from <see cref="Key"/> + <see cref="Args"/> in the viewer's own culture —
/// the background worker has no per-device UI culture, so the actual text is resolved client-side.
/// </summary>
/// <remarks>
/// Encoding keeps the transport untouched: progress still travels as a plain string over both the
/// in-process notifier and SignalR. A signal is <c>SOH stepInt US key US arg0 US arg1 …</c> using the
/// SOH (U+0001) sentinel and US (U+001F) separators — bytes that never occur in human progress text.
/// Any string that does not start with the sentinel (the agent's own diagnostic logs, the initial
/// "generating strategy" line) is treated by the UI as a plain, step-less feed line, so the channel
/// stays backward-compatible.
/// </remarks>
public readonly record struct SearchProgressSignal(SearchStep Step, string Key, IReadOnlyList<string> Args)
{
    private const char Sentinel = '\u0001';  // SOH — marks an encoded signal
    private const char Separator = '\u001f';  // US — field delimiter

    /// <summary>Encodes a step + localization key + args into a single transport string.</summary>
    public static string Encode(SearchStep step, string key, params object?[] args)
    {
        var parts = new List<string>(2 + args.Length)
        {
            ((int)step).ToString(),
            Clean(key)
        };
        foreach (var arg in args)
        {
            parts.Add(Clean(arg?.ToString() ?? string.Empty));
        }
        return Sentinel + string.Join(Separator, parts);
    }

    /// <summary>
    /// Decodes a transport string back into a signal. Returns <c>false</c> for any string that is not
    /// an encoded signal (a plain feed line), so callers can fall through to step-less rendering.
    /// </summary>
    public static bool TryDecode(string? message, out SearchProgressSignal signal)
    {
        signal = default;
        if (string.IsNullOrEmpty(message) || message[0] != Sentinel)
        {
            return false;
        }

        var fields = message[1..].Split(Separator);
        if (fields.Length < 2 || !int.TryParse(fields[0], out var stepInt))
        {
            return false;
        }

        var step = Enum.IsDefined(typeof(SearchStep), stepInt) ? (SearchStep)stepInt : SearchStep.Analyzing;
        signal = new SearchProgressSignal(step, fields[1], fields[2..]);
        return true;
    }

    // Strip the control characters we use as delimiters so user-supplied args (store/brand names)
    // can never corrupt the framing.
    private static string Clean(string value) =>
        value.IndexOf(Sentinel) < 0 && value.IndexOf(Separator) < 0
            ? value
            : value.Replace(Sentinel, ' ').Replace(Separator, ' ');
}
