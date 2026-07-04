using System.Text.Json.Serialization;
using Daleel.Search.Providers;

namespace Daleel.Web.Cloudflare;

// The JSON contracts of the Elsa ↔ worker edge (docs/architecture/cloudflare-workers-pipeline.md §13–14).
// Workers emit camelCase; everything here deserializes case-insensitively via CloudflareJson.

/// <summary>Handle returned by an async submit: where the job runs and where its result will land.</summary>
public sealed record WorkerHandle
{
    [JsonPropertyName("jobId")] public required string JobId { get; init; }

    /// <summary>R2 object key (in the Data bucket) the result is written to when done.</summary>
    [JsonPropertyName("resultKey")] public required string ResultKey { get; init; }
}

/// <summary>The scrape-worker's async-accept envelope (202 response).</summary>
public sealed record WorkerSubmitResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("mode")] public string? Mode { get; init; }
    [JsonPropertyName("jobId")] public string? JobId { get; init; }
    [JsonPropertyName("resultKey")] public string? ResultKey { get; init; }
    [JsonPropertyName("error")] public WorkerError? Error { get; init; }
}

/// <summary>GET /jobs/{id} — the worker's view of an async job.</summary>
public sealed record WorkerJobStatus
{
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("jobId")] public string? JobId { get; init; }
    [JsonPropertyName("resultKey")] public string? ResultKey { get; init; }
    [JsonPropertyName("error")] public WorkerError? Error { get; init; }

    public bool IsDone => string.Equals(Status, "done", StringComparison.OrdinalIgnoreCase);
    public bool IsError => string.Equals(Status, "error", StringComparison.OrdinalIgnoreCase);
}

public sealed record WorkerError
{
    [JsonPropertyName("code")] public string? Code { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("retryable")] public bool Retryable { get; init; }
}

/// <summary>
/// The thin pointer a worker enqueues onto the poll queue when an async job finishes (done or terminal
/// error). Never a payload — the R2 doc at <see cref="ResultKey"/> is the source of truth (doc §4.1).
/// </summary>
public sealed record PollMessage
{
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("worker")] public string? Worker { get; init; }

    /// <summary>Job kind, e.g. "catalog" | "brand" — selects the drain handler.</summary>
    [JsonPropertyName("kind")] public string? Kind { get; init; }

    [JsonPropertyName("jobId")] public string? JobId { get; init; }
    [JsonPropertyName("resultKey")] public string? ResultKey { get; init; }

    /// <summary>Originating SearchJob id (correlation for events); null for ad-hoc jobs.</summary>
    [JsonPropertyName("searchJobId")] public string? SearchJobId { get; init; }

    /// <summary>Store display name the crawl belongs to (ScrapedPrice.StoreName).</summary>
    [JsonPropertyName("store")] public string? Store { get; init; }

    [JsonPropertyName("domain")] public string? Domain { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("enqueuedAt")] public long EnqueuedAt { get; init; }
    [JsonPropertyName("deadlineAt")] public long DeadlineAt { get; init; }

    public bool IsDone => string.Equals(Status, "done", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The catalogue-crawl result document the scrape-worker writes to R2. <see cref="Products"/> reuses the
/// existing <see cref="CatalogProduct"/> record so the drain path feeds the same persistence code the
/// inline path uses today (doc §14.5: worker payloads are existing Daleel domain records).
/// </summary>
public sealed record CatalogResultDoc
{
    [JsonPropertyName("kind")] public string? Kind { get; init; }
    [JsonPropertyName("domain")] public string? Domain { get; init; }
    [JsonPropertyName("store")] public string? Store { get; init; }
    [JsonPropertyName("searchJobId")] public string? SearchJobId { get; init; }
    [JsonPropertyName("capturedAt")] public DateTimeOffset CapturedAt { get; init; }
    [JsonPropertyName("productCount")] public int ProductCount { get; init; }
    [JsonPropertyName("products")] public List<CatalogProduct> Products { get; init; } = new();
}

/// <summary>One leased message from the Cloudflare Queues pull API.</summary>
public sealed record PulledMessage
{
    [JsonPropertyName("lease_id")] public required string LeaseId { get; init; }
    [JsonPropertyName("body")] public string? Body { get; init; }
    [JsonPropertyName("attempts")] public int Attempts { get; init; }
}
