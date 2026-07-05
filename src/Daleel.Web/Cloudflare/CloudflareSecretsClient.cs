using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Daleel.Web.Cloudflare;

/// <summary>
/// Pushes secrets INTO Cloudflare worker scripts (PUT /accounts/{id}/workers/scripts/{script}/secrets)
/// — the write half of the VPS token authority: minted bearers travel from the vault to the worker's
/// secret store without a human or a GitHub secret in the loop. Auth is the account-level
/// CLOUDFLARE_API_TOKEN (Workers Scripts: Edit), the same credential the deploy workflows use.
/// </summary>
public interface ICloudflareSecretsClient
{
    bool IsConfigured { get; }

    /// <summary>
    /// Sets one secret on a worker script. Returns true on success, false when the script doesn't
    /// exist yet (deploy pending — caller retries later) or the API rejected the write.
    /// </summary>
    Task<bool> PutSecretAsync(string scriptName, string secretName, string value, CancellationToken ct = default);
}

public sealed class CloudflareSecretsClient : ICloudflareSecretsClient
{
    private readonly HttpClient _http;
    private readonly string? _accountId;
    private readonly ILogger<CloudflareSecretsClient> _logger;

    public CloudflareSecretsClient(HttpClient http, IConfiguration config, ILogger<CloudflareSecretsClient> logger)
    {
        _http = http;
        _logger = logger;
        _accountId = config["CLOUDFLARE_ACCOUNT_ID"]?.Trim();
        var token = config["CLOUDFLARE_API_TOKEN"]?.Trim();
        _http.BaseAddress = new Uri("https://api.cloudflare.com/");
        if (!string.IsNullOrWhiteSpace(token))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        _http.Timeout = TimeSpan.FromSeconds(30);
        IsConfigured = !string.IsNullOrWhiteSpace(_accountId) && !string.IsNullOrWhiteSpace(token);
    }

    public bool IsConfigured { get; }

    public async Task<bool> PutSecretAsync(
        string scriptName, string secretName, string value, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            return false;
        }

        try
        {
            var body = JsonSerializer.Serialize(new { name = secretName, text = value, type = "secret_text" });
            using var response = await _http.PutAsync(
                $"client/v4/accounts/{_accountId}/workers/scripts/{Uri.EscapeDataString(scriptName)}/secrets",
                new StringContent(body, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // Script not deployed yet — normal on first boot; the rotation service retries.
                _logger.LogInformation("Worker script {Script} not found — secret push deferred", scriptName);
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                // NEVER log the value; the name + status is all an operator needs.
                _logger.LogWarning("Secret push {Secret} → {Script} failed: {Status} {Body}",
                    secretName, scriptName, (int)response.StatusCode, Clip(text));
                return false;
            }

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Secret push {Secret} → {Script} failed", secretName, scriptName);
            return false;
        }
    }

    private static string Clip(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
