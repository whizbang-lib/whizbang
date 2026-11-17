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

  /// <summary><see cref="ATTR_OUTCOME"/>: nothing differed.</summary>
  public const string OUTCOME_CLEAN = "clean";

  /// <summary><see cref="ATTR_OUTCOME"/>: a difference was found and a repair requested or recorded.</summary>
  public const string OUTCOME_REPAIR_REQUESTED = "repair_requested";

  /// <summary><see cref="ATTR_OUTCOME"/>: the pair guard halted the pair.</summary>
  public const string OUTCOME_PAIR_HALTED = "pair_halted";

  /// <summary><see cref="ATTR_OUTCOME"/>: the request was declined.</summary>
  public const string OUTCOME_DECLINED = "declined";
#pragma warning restore CA1707

#pragma warning disable RCS1163, S1186, IDE0060 // RED stub: the next commit implements these
  /// <summary>Every attribute an integrity span carries.</summary>
  public static IReadOnlyList<string> Attributes { get; } = [];

  /// <summary>Starts an audit cycle span.</summary>
  public static Activity? StartAuditCycle() => null;

  /// <summary>Starts an audit span.</summary>
  public static Activity? StartAudit(string kind, string? peer, IntegrityTraceLink link) => null;

  /// <summary>Starts an answer span.</summary>
  public static Activity? StartAnswer(string kind, string? peer, IntegrityTraceLink link) => null;

  /// <summary>Starts a repair request span.</summary>
  public static Activity? StartRepairRequest(string kind, string? peer, Guid requestId) => null;

  /// <summary>Starts a repair span.</summary>
  public static Activity? StartRepair(string? peer, IntegrityTraceLink link) => null;

  /// <summary>Starts a re-check span.</summary>
  public static Activity? StartRecheck(string check) => null;

  /// <summary>The link a request sent under <paramref name="span"/> carries.</summary>
  public static IntegrityTraceLink LinkFrom(Activity? span, Guid requestId) => default;

  /// <summary>Records a count on the span.</summary>
  public static void SetCount(Activity? span, string attribute, long value) { }

  /// <summary>Records how the span ended.</summary>
  public static void SetOutcome(Activity? span, string outcome) { }
#pragma warning restore RCS1163, S1186, IDE0060
}
