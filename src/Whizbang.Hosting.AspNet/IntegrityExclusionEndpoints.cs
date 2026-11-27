// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Messaging;

namespace Whizbang.Hosting.AspNet;

/// <summary>Source-generated JSON for the integrity exclusion operator API, so the endpoints stay AOT-compatible.</summary>
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
  PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(IntegrityExclusionEndpoints.ExcludeBody))]
[JsonSerializable(typeof(IntegrityExclusionEndpoints.UnexcludeBody))]
[JsonSerializable(typeof(IntegrityExclusionResult))]
[JsonSerializable(typeof(IntegrityExclusionSummary))]
[JsonSerializable(typeof(IntegrityExclusionEndpoints.ExclusionProblem))]
internal partial class IntegrityExclusionJsonContext : JsonSerializerContext;

/// <summary>
/// Opt-in operator HTTP endpoints for excluding received events from integrity checks (#2588), beside the dead-letter and
/// redelivery operator endpoints. They call <see cref="IIntegrityExclusions"/>; the work is done by the maintenance step.
/// </summary>
/// <remarks>
/// <para>
/// Mount them on the service that received the events:
/// <code>
/// app.MapWhizbangIntegrityExclusionEndpoints()                     // default path "/whizbang/integrity/exclusions"
///    .RequireAuthorization("operators");
/// </code>
/// </para>
/// <list type="bullet">
///   <item><description><c>POST {prefix}</c> takes <c>{ "reason", "requestedBy", "eventIds"?, "selection"?, "dryRun"? }</c>
///   (a selection: <c>originServiceId</c>, <c>eventTypes</c>, <c>from</c>, <c>until</c>, <c>unresolved</c>: a list of
///   <c>UnknownAnswers</c>, <c>Undecided</c>, <c>Unhealed</c>, or <c>All</c>). <c>200</c> with the outcomes when applied at
///   once or a dry run, <c>202</c> with the request id when queued, <c>400</c> for an invalid request.</description></item>
///   <item><description><c>POST {prefix}/unmark</c> takes the reason, who asked, and one of <c>eventIds</c>,
///   <c>requestId</c>, or <c>originServiceId</c> (with <c>eventTypes</c>), with the same answers.</description></item>
///   <item><description><c>GET {prefix}?origin={id}</c> answers the summary.</description></item>
/// </list>
/// <para>
/// The endpoints are unauthenticated unless the host requires authorization on the returned group, as the dead-letter
/// endpoints are, which it should before exposing them.
/// </para>
/// </remarks>
/// <docs>resilience/integrity-exclusions#the-operator-endpoints</docs>
/// <tests>tests/Whizbang.Hosting.AspNet.Component.Tests/IntegrityExclusionEndpointsTests.cs</tests>
public static class IntegrityExclusionEndpoints {
  private const string JSON_CONTENT_TYPE = "application/json";

  /// <summary>
  /// Maps the exclusion endpoints under <paramref name="prefix"/> (default <c>/whizbang/integrity/exclusions</c>).
  /// Returns the route group so the host can chain <c>.RequireAuthorization()</c>.
  /// </summary>
  /// <param name="endpoints">The host's endpoints.</param>
  /// <param name="prefix">The route prefix.</param>
  /// <returns>The route group.</returns>
  public static RouteGroupBuilder MapWhizbangIntegrityExclusionEndpoints(
      this IEndpointRouteBuilder endpoints, string prefix = "/whizbang/integrity/exclusions") {
    ArgumentNullException.ThrowIfNull(endpoints);
    ArgumentException.ThrowIfNullOrEmpty(prefix);
    var group = endpoints.MapGroup(prefix);
    group.MapPost("/", _handleExcludeAsync);
    group.MapPost("/unmark", _handleUnexcludeAsync);
    group.MapGet("/", _handleSummaryAsync);
    return group;
  }

  private static async Task _handleExcludeAsync(HttpContext http) {
    ExcludeBody? body;
    try {
      body = await JsonSerializer.DeserializeAsync(http.Request.Body, IntegrityExclusionJsonContext.Default.ExcludeBody, http.RequestAborted)
        .ConfigureAwait(false);
    } catch (JsonException ex) {
      await _problemAsync(http, StatusCodes.Status400BadRequest, "The body is not an exclusion request: " + ex.Message).ConfigureAwait(false);
      return;
    }
    var exclusions = http.RequestServices.GetRequiredService<IIntegrityExclusions>();
    IntegrityExclusionResult result;
    try {
      result = await exclusions.ExcludeAsync(new IntegrityExclusionRequest {
        Reason = body?.Reason ?? "",
        RequestedBy = body?.RequestedBy ?? "",
        EventIds = body?.EventIds,
        Selection = body?.Selection is { } s ? new IntegrityExclusionSelection {
          OriginServiceId = s.OriginServiceId,
          EventTypes = s.EventTypes,
          From = s.From,
          Until = s.Until,
          Unresolved = s.Unresolved ?? IntegrityUnresolvedKinds.None,
        } : null,
        DryRun = body?.DryRun ?? false,
      }, http.RequestAborted).ConfigureAwait(false);
    } catch (ArgumentException ex) {
      await _problemAsync(http, StatusCodes.Status400BadRequest, ex.Message).ConfigureAwait(false);
      return;
    }
    await _resultAsync(http, result).ConfigureAwait(false);
  }

  private static async Task _handleUnexcludeAsync(HttpContext http) {
    UnexcludeBody? body;
    try {
      body = await JsonSerializer.DeserializeAsync(http.Request.Body, IntegrityExclusionJsonContext.Default.UnexcludeBody, http.RequestAborted)
        .ConfigureAwait(false);
    } catch (JsonException ex) {
      await _problemAsync(http, StatusCodes.Status400BadRequest, "The body is not an unexclusion request: " + ex.Message).ConfigureAwait(false);
      return;
    }
    var exclusions = http.RequestServices.GetRequiredService<IIntegrityExclusions>();
    IntegrityExclusionResult result;
    try {
      result = await exclusions.UnexcludeAsync(new IntegrityUnexclusionRequest {
        Reason = body?.Reason ?? "",
        RequestedBy = body?.RequestedBy ?? "",
        EventIds = body?.EventIds,
        RequestId = body?.RequestId,
        OriginServiceId = body?.OriginServiceId,
        EventTypes = body?.EventTypes,
      }, http.RequestAborted).ConfigureAwait(false);
    } catch (ArgumentException ex) {
      await _problemAsync(http, StatusCodes.Status400BadRequest, ex.Message).ConfigureAwait(false);
      return;
    }
    await _resultAsync(http, result).ConfigureAwait(false);
  }

  private static async Task _handleSummaryAsync(HttpContext http) {
    Guid? origin = null;
    if (http.Request.Query.TryGetValue("origin", out var raw)) {
      if (!Guid.TryParse(raw, out var parsed)) {
        await _problemAsync(http, StatusCodes.Status400BadRequest, "origin is not a service id").ConfigureAwait(false);
        return;
      }
      origin = parsed;
    }
    var summary = await http.RequestServices.GetRequiredService<IIntegrityExclusions>()
      .SummarizeAsync(origin, http.RequestAborted).ConfigureAwait(false);
    http.Response.ContentType = JSON_CONTENT_TYPE;
    await JsonSerializer.SerializeAsync(http.Response.Body, summary, IntegrityExclusionJsonContext.Default.IntegrityExclusionSummary,
      http.RequestAborted).ConfigureAwait(false);
  }

  private static async Task _resultAsync(HttpContext http, IntegrityExclusionResult result) {
    http.Response.StatusCode = result.Queued ? StatusCodes.Status202Accepted : StatusCodes.Status200OK;
    http.Response.ContentType = JSON_CONTENT_TYPE;
    await JsonSerializer.SerializeAsync(http.Response.Body, result, IntegrityExclusionJsonContext.Default.IntegrityExclusionResult,
      http.RequestAborted).ConfigureAwait(false);
  }

  private static async Task _problemAsync(HttpContext http, int status, string detail) {
    http.Response.StatusCode = status;
    http.Response.ContentType = JSON_CONTENT_TYPE;
    await JsonSerializer.SerializeAsync(http.Response.Body, new ExclusionProblem(detail), IntegrityExclusionJsonContext.Default.ExclusionProblem,
      http.RequestAborted).ConfigureAwait(false);
  }

  /// <summary>The body of <c>POST {prefix}</c>; see <see cref="IntegrityExclusionRequest"/> for each field.</summary>
  public sealed class ExcludeBody {
    /// <summary>Why: required.</summary>
    public string? Reason { get; init; }

    /// <summary>Who asked: required.</summary>
    public string? RequestedBy { get; init; }

    /// <summary>The events, by id.</summary>
    public List<Guid>? EventIds { get; init; }

    /// <summary>The events, by selection.</summary>
    public SelectionBody? Selection { get; init; }

    /// <summary>Count what would be marked, and mark nothing.</summary>
    public bool? DryRun { get; init; }
  }

  /// <summary>A selection in a request body; see <see cref="IntegrityExclusionSelection"/>.</summary>
  public sealed class SelectionBody {
    /// <summary>Only events received from this origin.</summary>
    public Guid? OriginServiceId { get; init; }

    /// <summary>Only events of these stored type names.</summary>
    public List<string>? EventTypes { get; init; }

    /// <summary>Only events stored at or after this time.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Only events stored (or recorded as unresolved) before this time.</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>Only what is currently unresolved, of these kinds.</summary>
    public IntegrityUnresolvedKinds? Unresolved { get; init; }
  }

  /// <summary>The body of <c>POST {prefix}/unmark</c>; see <see cref="IntegrityUnexclusionRequest"/> for each field.</summary>
  public sealed class UnexcludeBody {
    /// <summary>Why: required.</summary>
    public string? Reason { get; init; }

    /// <summary>Who asked: required.</summary>
    public string? RequestedBy { get; init; }

    /// <summary>The marks, by event id.</summary>
    public List<Guid>? EventIds { get; init; }

    /// <summary>Every mark a request made.</summary>
    public Guid? RequestId { get; init; }

    /// <summary>Every mark of this origin.</summary>
    public Guid? OriginServiceId { get; init; }

    /// <summary>With the origin: only marks of these types.</summary>
    public List<string>? EventTypes { get; init; }
  }

  /// <summary>Why a request was refused.</summary>
  /// <param name="Detail">The reason.</param>
  public sealed record ExclusionProblem(string Detail);
}
