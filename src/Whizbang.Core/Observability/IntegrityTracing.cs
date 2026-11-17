// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics;

namespace Whizbang.Core.Observability;

/// <summary>
/// The trace link an integrity request carries and its answer echoes (#2365): the request's id, and the W3C
/// <c>traceparent</c> of the span that sent it, so an origin's answer and a consumer's repair join one trace across
/// services. Both are null on a message from a release that predates them.
/// </summary>
/// <param name="RequestId">The request's id, echoed on every answer to it; null when the sender named none.</param>
/// <param name="TraceParent">The sending span's W3C <c>traceparent</c>; null when the sender had none.</param>
/// <docs>resilience/stream-integrity#integrity-traces</docs>
public readonly record struct IntegrityTraceLink(Guid? RequestId, string? TraceParent);

/// <summary>
/// The <c>Whizbang.Integrity</c> spans (#2365): an audit cycle, an audit (a comparison of an origin's manifest or
/// checkpoint), an answer (an origin serving a manifest or redelivery request), a repair request, a repair (a consumer
/// applying a redelivery) and a re-check.
/// </summary>
/// <docs>resilience/stream-integrity#integrity-traces</docs>
/// <tests>tests/Whizbang.Observability.Tests/IntegrityTracingTests.cs</tests>
public static class IntegrityTracing {
#pragma warning disable CA1707
  /// <summary>The activity source's name.</summary>
  public const string SOURCE_NAME = "Whizbang.Integrity";

  /// <summary>One audit cycle of the audit worker: the manifest asks it sends.</summary>
  public const string SPAN_AUDIT_CYCLE = "integrity.audit_cycle";

  /// <summary>A consumer compares an origin's manifest or checkpoint with its own store.</summary>
  public const string SPAN_AUDIT = "integrity.audit";

  /// <summary>An origin answers a manifest or redelivery request.</summary>
  public const string SPAN_ANSWER = "integrity.answer";

  /// <summary>A consumer asks an origin to redeliver.</summary>
  public const string SPAN_REPAIR_REQUEST = "integrity.repair_request";

  /// <summary>A consumer applies a redelivery bundle.</summary>
  public const string SPAN_REPAIR = "integrity.repair";

  /// <summary>A re-check: the sealed-history re-check or the idle ledger re-check.</summary>
  public const string SPAN_RECHECK = "integrity.recheck";

  /// <summary>What the span is about (<c>manifest</c>, <c>checkpoint</c>, <c>redelivery</c>, <c>drain</c>, ...).</summary>
  public const string ATTR_KIND = "whizbang.integrity.kind";

  /// <summary>The other service's name.</summary>
  public const string ATTR_PEER = "whizbang.integrity.peer";

  /// <summary>The request the span sends, answers or applies the answer of.</summary>
  public const string ATTR_REQUEST_ID = "whizbang.integrity.request_id";

  /// <summary>The re-check's name (<c>seal_recheck</c>, <c>ledger_recheck</c>).</summary>
  public const string ATTR_CHECK = "whizbang.integrity.check";

  /// <summary>How many event types the span covered.</summary>
  public const string ATTR_EVENT_TYPES = "whizbang.integrity.event_types";

  /// <summary>How many streams the span covered.</summary>
  public const string ATTR_STREAMS = "whizbang.integrity.streams";

  /// <summary>How many events the span covered.</summary>
  public const string ATTR_EVENTS = "whizbang.integrity.events";

  /// <summary>How many sealed epochs a re-check covered.</summary>
  public const string ATTR_EPOCHS = "whizbang.integrity.epochs";

  /// <summary>How the span ended.</summary>
  public const string ATTR_OUTCOME = "whizbang.integrity.outcome";

  /// <summary><see cref="ATTR_KIND"/>: a manifest request or its comparison.</summary>
  public const string KIND_MANIFEST = "manifest";

  /// <summary><see cref="ATTR_KIND"/>: a checkpoint's comparison.</summary>
  public const string KIND_CHECKPOINT = "checkpoint";

  /// <summary><see cref="ATTR_KIND"/>: a redelivery request's answer.</summary>
  public const string KIND_REDELIVERY = "redelivery";

  /// <summary><see cref="ATTR_KIND"/>: a repair request the paced drain sent from the ledger.</summary>
  public const string KIND_DRAIN = "drain";

  /// <summary><see cref="ATTR_KIND"/>: a repair request an audit sent as it compared.</summary>
  public const string KIND_INLINE = "inline";

  /// <summary><see cref="ATTR_KIND"/>: a repair request an operator made.</summary>
  public const string KIND_OPERATOR = "operator";

  /// <summary><see cref="ATTR_CHECK"/>: the idle ledger re-check, which asks origins to answer open ledger rows again.</summary>
  public const string CHECK_LEDGER_RECHECK = "ledger_recheck";

  /// <summary><see cref="ATTR_OUTCOME"/>: nothing differed.</summary>
  public const string OUTCOME_CLEAN = "clean";

  /// <summary><see cref="ATTR_OUTCOME"/>: a difference was found and a repair requested or recorded.</summary>
  public const string OUTCOME_REPAIR_REQUESTED = "repair_requested";

  /// <summary><see cref="ATTR_OUTCOME"/>: a re-check found drift.</summary>
  public const string OUTCOME_DRIFT = "drift";

  /// <summary><see cref="ATTR_OUTCOME"/>: the pair guard halted the pair.</summary>
  public const string OUTCOME_PAIR_HALTED = "pair_halted";

  /// <summary><see cref="ATTR_OUTCOME"/>: the request was declined.</summary>
  public const string OUTCOME_DECLINED = "declined";
#pragma warning restore CA1707

  /// <summary>
  /// Every attribute an integrity span carries (#2365): its kind, peer service, request, re-check name, counts and
  /// outcome. None names a stream, a tenant or an event: a span says what a service did with another, not whose data it
  /// touched, and the counts say how much.
  /// </summary>
  /// <tests>tests/Whizbang.Observability.Tests/IntegrityTracingTests.cs:EverySpanAttribute_IsOnTheBoundedList_AndNoneNamesAStreamOrATenantAsync</tests>
  public static IReadOnlyList<string> Attributes { get; } = [
    ATTR_KIND, ATTR_PEER, ATTR_REQUEST_ID, ATTR_CHECK, ATTR_EVENT_TYPES, ATTR_STREAMS, ATTR_EVENTS, ATTR_EPOCHS, ATTR_OUTCOME,
  ];

  private static ActivitySource _source => WhizbangActivitySource.Integrity;

  /// <summary>Starts the span of one audit cycle: the manifest asks the audit worker sends run under it.</summary>
  /// <returns>The span, or null when nothing listens.</returns>
  public static Activity? StartAuditCycle() => _source.StartActivity(SPAN_AUDIT_CYCLE, ActivityKind.Internal);

  /// <summary>Starts the span of a consumer's comparison of an origin's manifest or checkpoint.</summary>
  /// <param name="kind"><see cref="KIND_MANIFEST"/> or <see cref="KIND_CHECKPOINT"/>.</param>
  /// <param name="peer">The origin's name.</param>
  /// <param name="link">The link the manifest echoed from the ask; default under the ambient span.</param>
  /// <returns>The span, or null when nothing listens.</returns>
  public static Activity? StartAudit(string kind, string? peer, IntegrityTraceLink link) =>
    _start(SPAN_AUDIT, ActivityKind.Internal, kind, peer, link);

  /// <summary>Starts the span of an origin's answer to a manifest or redelivery request, in the request's trace.</summary>
  /// <param name="kind"><see cref="KIND_MANIFEST"/> or <see cref="KIND_REDELIVERY"/>.</param>
  /// <param name="peer">The requester's name.</param>
  /// <param name="link">The link the request carried.</param>
  /// <returns>The span, or null when nothing listens.</returns>
  public static Activity? StartAnswer(string kind, string? peer, IntegrityTraceLink link) =>
    _start(SPAN_ANSWER, ActivityKind.Consumer, kind, peer, link);

  /// <summary>Starts the span of a consumer's repair request, under the ambient span (the audit that asked, if any).</summary>
  /// <param name="kind"><see cref="KIND_DRAIN"/>, <see cref="KIND_INLINE"/> or <see cref="KIND_OPERATOR"/>.</param>
  /// <param name="peer">The origin's name.</param>
  /// <param name="requestId">The request's id.</param>
  /// <returns>The span, or null when nothing listens.</returns>
  public static Activity? StartRepairRequest(string kind, string? peer, Guid requestId) =>
    _start(SPAN_REPAIR_REQUEST, ActivityKind.Producer, kind, peer, new IntegrityTraceLink(requestId, null));

  /// <summary>Starts the span of a consumer applying a redelivery bundle, in the trace the bundle carried.</summary>
  /// <param name="peer">The origin's name.</param>
  /// <param name="link">The link the bundle echoed.</param>
  /// <returns>The span, or null when nothing listens.</returns>
  public static Activity? StartRepair(string? peer, IntegrityTraceLink link) =>
    _start(SPAN_REPAIR, ActivityKind.Consumer, KIND_REDELIVERY, peer, link);

  /// <summary>Starts the span of a re-check.</summary>
  /// <param name="check">The re-check's name.</param>
  /// <returns>The span, or null when nothing listens.</returns>
  public static Activity? StartRecheck(string check) {
    var span = _source.StartActivity(SPAN_RECHECK, ActivityKind.Internal);
    span?.SetTag(ATTR_CHECK, check);
    return span;
  }

  /// <summary>
  /// The link a request sent under <paramref name="span"/> carries: its id, and the span's <c>traceparent</c>, or the
  /// ambient one when the span was not sampled, so an unsampled integrity span still links the caller's trace.
  /// </summary>
  /// <param name="span">The request's span, or null.</param>
  /// <param name="requestId">The request's id.</param>
  /// <returns>The link.</returns>
  public static IntegrityTraceLink LinkFrom(Activity? span, Guid requestId) =>
    new(requestId, (span ?? Activity.Current)?.Id);

  /// <summary>
  /// The W3C <c>traceparent</c> of the ambient span, which an answer carries back so the receiver's work joins the
  /// trace; null when there is none.
  /// </summary>
  public static string? CurrentTraceParent => Activity.Current?.Id;

  /// <summary>Records a count on the span; nothing when there is no span.</summary>
  /// <param name="span">The span, or null.</param>
  /// <param name="attribute">One of the count attributes.</param>
  /// <param name="value">The count.</param>
  public static void SetCount(Activity? span, string attribute, long value) => span?.SetTag(attribute, value);

  /// <summary>Records how the span ended; nothing when there is no span.</summary>
  /// <param name="span">The span, or null.</param>
  /// <param name="outcome">One of the outcome values.</param>
  public static void SetOutcome(Activity? span, string outcome) => span?.SetTag(ATTR_OUTCOME, outcome);

  // A link whose traceparent parses is the parent; anything else (none, or one a foreign sender garbled) starts the span
  // under the ambient one, so a bad link never fails the work it describes.
  private static Activity? _start(string name, ActivityKind kind, string what, string? peer, IntegrityTraceLink link) {
    var parent = link.TraceParent is { } traceParent && ActivityContext.TryParse(traceParent, null, out var parsed)
      ? parsed
      : default;
    var span = _source.StartActivity(name, kind, parent);
    if (span is null) {
      return null;
    }
    span.SetTag(ATTR_KIND, what);
    span.SetTag(ATTR_PEER, peer);
    if (link.RequestId is { } requestId) {
      span.SetTag(ATTR_REQUEST_ID, requestId.ToString());
    }
    return span;
  }
}
