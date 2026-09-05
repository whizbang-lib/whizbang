using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Messaging;

namespace Whizbang.Hosting.AspNet;

/// <summary>
/// Source-genned JsonSerializerContext for the operator API responses. Keeps the
/// endpoint AOT-compatible without forcing reflection-based serialization.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
  PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IReadOnlyList<DeadLetterEntry>))]
[JsonSerializable(typeof(List<DeadLetterEntry>))]
[JsonSerializable(typeof(DeadLetterEntry))]
[JsonSerializable(typeof(DeadLetterOperatorEndpoints.ScanNowResponse))]
[JsonSerializable(typeof(IReadOnlyList<HeldCohort>))]
[JsonSerializable(typeof(HeldCohort))]
[JsonSerializable(typeof(DeadLetterOperatorEndpoints.CohortReleaseResult))]
internal partial class DeadLetterOperatorJsonContext : JsonSerializerContext {
}

/// <summary>
/// Opt-in operator HTTP endpoints for the Whizbang internal DLQ. Wraps the SQL functions
/// from migrations 050 + 051 so operators can drive recovery without raw psql access.
/// </summary>
/// <remarks>
/// <para>
/// Mount via:
/// <code>
/// app.MapWhizbangDeadLetterEndpoints();              // default path "/whizbang/dlq"
/// app.MapWhizbangDeadLetterEndpoints("/admin/dlq");  // custom path
/// </code>
/// </para>
/// <para>
/// Endpoints (all unauthenticated by default — wrap in <c>.RequireAuthorization()</c> or
/// equivalent before exposing publicly):
/// </para>
/// <list type="bullet">
///   <item><description><c>GET  /whizbang/dlq/due?max=200</c> — list rows ready for recovery</description></item>
///   <item><description><c>POST /whizbang/dlq/{id}/retry</c> — schedule the row for immediate retry</description></item>
///   <item><description><c>POST /whizbang/dlq/{id}/hold</c> — mark HoldForReview (no auto-retry)</description></item>
///   <item><description><c>POST /whizbang/dlq/{id}/give-up</c> — mark PermanentlyFailed</description></item>
///   <item><description><c>POST /whizbang/dlq/scan-now?generation={gen}</c> — manual replay sweep</description></item>
/// </list>
/// </remarks>
/// <docs>operations/dead-letter-queue/operator-api</docs>
public static class DeadLetterOperatorEndpoints {

  /// <summary>
  /// Maps the operator endpoints under the configured prefix (default <c>/whizbang/dlq</c>).
  /// Returns the <see cref="RouteGroupBuilder"/> so callers can chain
  /// <c>.RequireAuthorization()</c> / <c>.RequireHost()</c> etc.
  /// </summary>
  public static RouteGroupBuilder MapWhizbangDeadLetterEndpoints(
      this IEndpointRouteBuilder endpoints,
      string prefix = "/whizbang/dlq") {
    ArgumentNullException.ThrowIfNull(endpoints);
    ArgumentException.ThrowIfNullOrEmpty(prefix);
    var group = endpoints.MapGroup(prefix);

    group.MapGet("/due", _handleDueAsync);
    group.MapPost("/{id:guid}/retry", _handleRetryAsync);
    group.MapPost("/{id:guid}/hold", _handleHoldAsync);
    group.MapPost("/{id:guid}/give-up", _handleGiveUpAsync);
    group.MapPost("/scan-now", _handleScanNowAsync);
    group.MapGet("/cohorts", _handleCohortsAsync);
    group.MapPost("/cohorts/{fingerprint}/release", _handleCohortReleaseAsync);

    return group;
  }

  private static async Task _handleCohortsAsync(HttpContext http) {
    var svc = http.RequestServices.GetRequiredService<IDeadLetterRecoveryService>();
    var cohorts = await svc.ListHeldCohortsAsync(http.RequestAborted).ConfigureAwait(false);
    http.Response.ContentType = "application/json";
    await JsonSerializer.SerializeAsync(
      http.Response.Body, cohorts,
      DeadLetterOperatorJsonContext.Default.IReadOnlyListHeldCohort,
      http.RequestAborted).ConfigureAwait(false);
  }

  private static async Task _handleCohortReleaseAsync(HttpContext http) {
    var fingerprint = http.Request.RouteValues["fingerprint"]?.ToString();
    if (string.IsNullOrWhiteSpace(fingerprint)) {
      http.Response.StatusCode = StatusCodes.Status400BadRequest;
      return;
    }
    var staggerMinutes = 30;
    if (http.Request.Query.TryGetValue("staggerMinutes", out var s) && int.TryParse(s, out var parsed)) {
      staggerMinutes = parsed;
    }
    var svc = http.RequestServices.GetRequiredService<IDeadLetterRecoveryService>();
    // Operator release goes through the SAME staggered-eligibility path as the campaigns:
    // there is no firehose endpoint, by design.
    var released = await svc.ReleaseHeldCohortAsync(
      fingerprint, TimeSpan.FromMinutes(staggerMinutes), http.RequestAborted).ConfigureAwait(false);
    http.Response.ContentType = "application/json";
    await JsonSerializer.SerializeAsync(
      http.Response.Body, new CohortReleaseResult(fingerprint, released),
      DeadLetterOperatorJsonContext.Default.CohortReleaseResult,
      http.RequestAborted).ConfigureAwait(false);
  }

  private static async Task _handleDueAsync(HttpContext http) {
    var svc = http.RequestServices.GetRequiredService<IDeadLetterRecoveryService>();
    var max = 200;
    if (http.Request.Query.TryGetValue("max", out var maxStr) && int.TryParse(maxStr, out var parsed)) {
      max = parsed;
    }
    var entries = await svc.FetchDueAsync(maxCount: max, ct: http.RequestAborted).ConfigureAwait(false);
    http.Response.ContentType = "application/json";
    await JsonSerializer.SerializeAsync(
      http.Response.Body, entries,
      DeadLetterOperatorJsonContext.Default.IReadOnlyListDeadLetterEntry,
      http.RequestAborted).ConfigureAwait(false);
  }

  private static async Task _handleRetryAsync(HttpContext http) {
    if (!_tryGetIdFromRoute(http, out var id)) {
      http.Response.StatusCode = StatusCodes.Status400BadRequest;
      return;
    }
    var svc = http.RequestServices.GetRequiredService<IDeadLetterRecoveryService>();
    await svc.ScheduleNextAttemptAsync(id, DateTimeOffset.UtcNow, http.RequestAborted).ConfigureAwait(false);
    http.Response.StatusCode = StatusCodes.Status204NoContent;
  }

  private static async Task _handleHoldAsync(HttpContext http) {
    if (!_tryGetIdFromRoute(http, out var id)) {
      http.Response.StatusCode = StatusCodes.Status400BadRequest;
      return;
    }
    var svc = http.RequestServices.GetRequiredService<IDeadLetterRecoveryService>();
    await svc.MarkHoldingAsync(id, http.RequestAborted).ConfigureAwait(false);
    http.Response.StatusCode = StatusCodes.Status204NoContent;
  }

  private static async Task _handleGiveUpAsync(HttpContext http) {
    if (!_tryGetIdFromRoute(http, out var id)) {
      http.Response.StatusCode = StatusCodes.Status400BadRequest;
      return;
    }
    var svc = http.RequestServices.GetRequiredService<IDeadLetterRecoveryService>();
    await svc.MarkPermanentlyFailedAsync(id, http.RequestAborted).ConfigureAwait(false);
    http.Response.StatusCode = StatusCodes.Status204NoContent;
  }

  private static async Task _handleScanNowAsync(HttpContext http) {
    var svc = http.RequestServices.GetRequiredService<IDeadLetterRecoveryService>();
    string? gen = null;
    if (http.Request.Query.TryGetValue("generation", out var genStr)) {
      gen = genStr.ToString();
    }
    if (string.IsNullOrEmpty(gen)) {
      var provider = http.RequestServices.GetRequiredService<IGenerationProvider>();
      gen = provider.GetGeneration();
    }
    // Operator-initiated replay is deliberate and interactive: no stagger (0), the operator
    // wants the re-offer now — the deploy-time replay is the one that paces itself (#669).
    var scheduled = await svc.ResetForGenerationAsync(gen, 0, http.RequestAborted).ConfigureAwait(false);
    http.Response.ContentType = "application/json";
    var response = new ScanNowResponse { Generation = gen, Scheduled = scheduled };
    await JsonSerializer.SerializeAsync(
      http.Response.Body, response,
      DeadLetterOperatorJsonContext.Default.ScanNowResponse,
      http.RequestAborted).ConfigureAwait(false);
  }

  private static bool _tryGetIdFromRoute(HttpContext http, out Guid id) {
    id = Guid.Empty;
    if (http.Request.RouteValues.TryGetValue("id", out var raw) && raw is string s && Guid.TryParse(s, out var parsed)) {
      id = parsed;
      return true;
    }
    return false;
  }

  /// <summary>Response DTO for the scan-now endpoint.</summary>
  /// <summary>An operator cohort release receipt.</summary>
  /// <param name="Fingerprint">The released cohort.</param>
  /// <param name="Released">Rows returned to Pending, staggered.</param>
  public sealed record CohortReleaseResult(string Fingerprint, int Released);

  public sealed class ScanNowResponse {
    /// <summary>The generation tag that was passed (or auto-resolved).</summary>
    public string Generation { get; init; } = "";
    /// <summary>Number of DLQ rows scheduled for immediate retry.</summary>
    public int Scheduled { get; init; }
  }
}
