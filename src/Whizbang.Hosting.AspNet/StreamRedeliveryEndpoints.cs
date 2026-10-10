// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Messaging;

namespace Whizbang.Hosting.AspNet;

/// <summary>Source-generated JSON for the redelivery operator API, so the endpoint stays AOT-compatible.</summary>
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
  PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StreamRedeliveryEndpoints.RedeliverStreamsBody))]
[JsonSerializable(typeof(StreamRedeliveryReceipt))]
[JsonSerializable(typeof(StreamRedeliveryEndpoints.RedeliveryProblem))]
internal partial class StreamRedeliveryJsonContext : JsonSerializerContext;

/// <summary>
/// Opt-in operator HTTP endpoint that asks an origin service to republish the stored events of a list of streams to
/// this service: the repair path for events lost in transport that the integrity ledger cannot drive.
/// </summary>
/// <remarks>
/// <para>
/// Mount it on the service that lost the events:
/// <code>
/// app.MapWhizbangStreamRedeliveryEndpoints();                  // default path "/whizbang/redelivery"
/// app.MapWhizbangStreamRedeliveryEndpoints("/admin/redelivery") // custom path
///    .RequireAuthorization("operators");
/// </code>
/// </para>
/// <para>
/// <c>POST {prefix}/streams</c> takes <code>{ "originService", "streamIds", "originRequestTopic"?, "replyTopic"?,
/// "tenantScope"?, "eventTypes"?, "stateOnly"? }</code> and answers <c>202 Accepted</c> with what was sent,
/// <c>400</c> for a request that names no origin or no stream, and <c>409</c> when this service cannot send it (no
/// known request topic for the origin, for instance), with the reason. The endpoint is unauthenticated unless the
/// host requires authorization on the returned group, which it should before exposing it.
/// </para>
/// </remarks>
/// <docs>resilience/stream-integrity#operator-redelivery</docs>
/// <tests>tests/Whizbang.Hosting.AspNet.Component.Tests/StreamRedeliveryEndpointsTests.cs</tests>
public static class StreamRedeliveryEndpoints {
  private const string JSON_CONTENT_TYPE = "application/json";

  /// <summary>
  /// Maps the redelivery endpoint under <paramref name="prefix"/> (default <c>/whizbang/redelivery</c>). Returns the
  /// route group so the host can chain <c>.RequireAuthorization()</c>.
  /// </summary>
  public static RouteGroupBuilder MapWhizbangStreamRedeliveryEndpoints(
      this IEndpointRouteBuilder endpoints, string prefix = "/whizbang/redelivery") {
    ArgumentNullException.ThrowIfNull(endpoints);
    ArgumentException.ThrowIfNullOrEmpty(prefix);
    var group = endpoints.MapGroup(prefix);
    group.MapPost("/streams", _handleRedeliverStreamsAsync);
    return group;
  }

  private static async Task _handleRedeliverStreamsAsync(HttpContext http) {
    RedeliverStreamsBody? body;
    try {
      body = await JsonSerializer.DeserializeAsync(
        http.Request.Body, StreamRedeliveryJsonContext.Default.RedeliverStreamsBody, http.RequestAborted).ConfigureAwait(false);
    } catch (JsonException ex) {
      await _problemAsync(http, StatusCodes.Status400BadRequest, "The body is not a redelivery request: " + ex.Message).ConfigureAwait(false);
      return;
    }

    var requester = http.RequestServices.GetRequiredService<IStreamRedeliveryRequester>();
    StreamRedeliveryReceipt receipt;
    try {
      receipt = await requester.RequestAsync(new StreamRedeliveryRequest {
        OriginService = body?.OriginService ?? "",
        StreamIds = body?.StreamIds ?? [],
        OriginRequestTopic = body?.OriginRequestTopic,
        ReplyTopic = body?.ReplyTopic,
        TenantScope = body?.TenantScope,
        EventTypes = body?.EventTypes,
        StateOnly = body?.StateOnly ?? false,
      }, http.RequestAborted).ConfigureAwait(false);
    } catch (ArgumentException ex) {
      await _problemAsync(http, StatusCodes.Status400BadRequest, ex.Message).ConfigureAwait(false);
      return;
    } catch (InvalidOperationException ex) {
      await _problemAsync(http, StatusCodes.Status409Conflict, ex.Message).ConfigureAwait(false);
      return;
    }

    http.Response.StatusCode = StatusCodes.Status202Accepted;
    http.Response.ContentType = JSON_CONTENT_TYPE;
    await JsonSerializer.SerializeAsync(
      http.Response.Body, receipt, StreamRedeliveryJsonContext.Default.StreamRedeliveryReceipt, http.RequestAborted).ConfigureAwait(false);
  }

  private static async Task _problemAsync(HttpContext http, int status, string detail) {
    http.Response.StatusCode = status;
    http.Response.ContentType = JSON_CONTENT_TYPE;
    await JsonSerializer.SerializeAsync(
      http.Response.Body, new RedeliveryProblem(detail), StreamRedeliveryJsonContext.Default.RedeliveryProblem, http.RequestAborted).ConfigureAwait(false);
  }

  /// <summary>The body of <c>POST {prefix}/streams</c>; see <see cref="StreamRedeliveryRequest"/> for each field.</summary>
  public sealed class RedeliverStreamsBody {
    /// <summary>The origin service's logical name.</summary>
    public string? OriginService { get; init; }

    /// <summary>The streams to republish.</summary>
    public List<Guid>? StreamIds { get; init; }

    /// <summary>The topic the origin takes requests on, when this service has not learned it.</summary>
    public string? OriginRequestTopic { get; init; }

    /// <summary>The topic to republish on, when not this service's repair topic or first consumer destination.</summary>
    public string? ReplyTopic { get; init; }

    /// <summary>Optional tenant filter.</summary>
    public string? TenantScope { get; init; }

    /// <summary>Optional stored event-type names.</summary>
    public List<string>? EventTypes { get; init; }

    /// <summary>True to rebuild state only, without running trigger receptors again.</summary>
    public bool? StateOnly { get; init; }
  }

  /// <summary>Why a redelivery request was refused.</summary>
  /// <param name="Detail">The reason.</param>
  public sealed record RedeliveryProblem(string Detail);
}
