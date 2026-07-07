using System.Net.Sockets;
using Daleel.Search.Http;

namespace Daleel.Web.Pipeline.Enrichment;

/// <summary>
/// Decides whether an enrichment-handler failure is a RETRIABLE INFRA/billing outage (the work is fine,
/// the provider is down) versus a genuinely-terminal fault. Infra failures are parked (Requeue — no
/// attempt consumed) so a billing outage never silently drops pending work; everything else Retries
/// toward its Dead cap as before. Classification is by TYPED <see cref="ProviderException.StatusCode"/>
/// and transport exception type — not fragile message string-matching — with one tight billing-message
/// fallback for a status that only survives in a wrapped message.
/// </summary>
internal static class EnrichmentErrorClassifier
{
    public static bool IsRetriableInfra(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is ProviderException pe &&
                (pe.IsTransient || pe.StatusCode is 402 or 408 or 429 or (>= 500 and <= 599)))
            {
                return true;
            }

            if (e is HttpRequestException or SocketException or IOException or TimeoutException)
            {
                return true;
            }
        }

        // Belt-and-suspenders: a billing failure whose status only survives in the message text.
        return ex.Message.Contains("HTTP 402", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("PaymentRequired", StringComparison.OrdinalIgnoreCase);
    }
}
