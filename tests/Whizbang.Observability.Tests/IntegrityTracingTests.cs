// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Observability.Tests;

/// <summary>
/// #2365: the <c>Whizbang.Integrity</c> spans. Each test reads the activity it started, so spans other tests start on the
/// same process-wide source never reach its assertions.
/// </summary>
/// <docs>resilience/stream-integrity#integrity-traces</docs>
/// <code-under-test>src/Whizbang.Core/Observability/IntegrityTracing.cs</code-under-test>
[NotInParallel]   // the listener is process-wide, and one test asserts no span starts with none listening
public class IntegrityTracingTests {
  private static ActivityListener _listen() {
    var listener = new ActivityListener {
      ShouldListenTo = s => s.Name == IntegrityTracing.SOURCE_NAME,
      Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
    };
    ActivitySource.AddActivityListener(listener);
    return listener;
  }

  private static string _remoteTraceParent(out ActivityTraceId traceId, out ActivitySpanId spanId) {
    traceId = ActivityTraceId.CreateRandom();
    spanId = ActivitySpanId.CreateRandom();
    return $"00-{traceId}-{spanId}-01";
  }

  [Test]
  public async Task TheSource_IsWhizbangIntegrity_AndIsListedWithEveryWhizbangSourceAsync() {
    await Assert.That(WhizbangActivitySource.Integrity.Name).IsEqualTo("Whizbang.Integrity");
    await Assert.That(WhizbangActivitySource.Integrity.Name).IsEqualTo(IntegrityTracing.SOURCE_NAME);
    await Assert.That(WhizbangActivitySource.All).Contains("Whizbang.Integrity")
      .Because("the turnkey tracing registration subscribes every name on the list");
  }

  [Test]
  public async Task EveryActivitySourceWhizbangDeclares_IsOnTheListAsync() {
    // The drift lock: a source added to WhizbangActivitySource and not to All would never be subscribed.
    var declared = typeof(WhizbangActivitySource).GetFields()
      .Where(f => f.FieldType == typeof(ActivitySource))
      .Select(f => ((ActivitySource)f.GetValue(null)!).Name)
      .ToList();

    await Assert.That(declared.Count).IsGreaterThanOrEqualTo(5);
    await Assert.That(declared.Where(n => !WhizbangActivitySource.All.Contains(n)).ToList()).IsEmpty();
  }

  [Test]
  public async Task AnAnswer_JoinsTheTraceTheRequestCarried_AndNamesItsRequestAsync() {
    using var listener = _listen();
    var traceParent = _remoteTraceParent(out var traceId, out var spanId);
    var requestId = (Guid)TrackedGuid.New();

    using var span = IntegrityTracing.StartAnswer(IntegrityTracing.KIND_REDELIVERY, "consumer-svc",
      new IntegrityTraceLink(requestId, traceParent));

    await Assert.That(span).IsNotNull();
    await Assert.That(span!.OperationName).IsEqualTo(IntegrityTracing.SPAN_ANSWER);
    await Assert.That(span.Kind).IsEqualTo(ActivityKind.Consumer);
    await Assert.That(span.TraceId).IsEqualTo(traceId).Because("the origin's answer joins the requester's trace");
    await Assert.That(span.ParentSpanId).IsEqualTo(spanId);
    await Assert.That(span.GetTagItem(IntegrityTracing.ATTR_KIND)).IsEqualTo(IntegrityTracing.KIND_REDELIVERY);
    await Assert.That(span.GetTagItem(IntegrityTracing.ATTR_PEER)).IsEqualTo("consumer-svc");
    await Assert.That(span.GetTagItem(IntegrityTracing.ATTR_REQUEST_ID)).IsEqualTo(requestId.ToString());
  }

  [Test]
  public async Task ARequest_CarriesItsOwnSpan_SoTheAnswerIsItsChildAsync() {
    using var listener = _listen();
    var requestId = (Guid)TrackedGuid.New();

    using var request = IntegrityTracing.StartRepairRequest(IntegrityTracing.KIND_DRAIN, "origin-svc", requestId);
    var link = IntegrityTracing.LinkFrom(request, requestId);
    using var answer = IntegrityTracing.StartAnswer(IntegrityTracing.KIND_REDELIVERY, "consumer-svc", link);

    await Assert.That(request!.OperationName).IsEqualTo(IntegrityTracing.SPAN_REPAIR_REQUEST);
    await Assert.That(request.Kind).IsEqualTo(ActivityKind.Producer);
    await Assert.That(request.GetTagItem(IntegrityTracing.ATTR_REQUEST_ID)).IsEqualTo(requestId.ToString());
    await Assert.That(link.RequestId).IsEqualTo(requestId);
    await Assert.That(link.TraceParent).IsEqualTo(request.Id);
    await Assert.That(answer!.TraceId).IsEqualTo(request.TraceId);
    await Assert.That(answer.ParentSpanId).IsEqualTo(request.SpanId);
  }

  [Test]
  public async Task ALinkWithNoSpan_CarriesTheAmbientTrace_OrNoneAsync() {
    var requestId = (Guid)TrackedGuid.New();
    Activity.Current = null;

    await Assert.That(IntegrityTracing.LinkFrom(null, requestId).TraceParent).IsNull()
      .Because("with no listener and no ambient activity there is no trace to carry");

    using var ambientSource = new ActivitySource("Whizbang.Integrity.Tests.Ambient");
    using var ambientListener = new ActivityListener {
      ShouldListenTo = s => s.Name == ambientSource.Name,
      Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
    };
    ActivitySource.AddActivityListener(ambientListener);
    using var ambient = ambientSource.StartActivity("ambient");

    var link = IntegrityTracing.LinkFrom(null, requestId);
    await Assert.That(link.TraceParent).IsEqualTo(ambient!.Id).Because("an unsampled integrity span still links the caller's trace");
    await Assert.That(link.RequestId).IsEqualTo(requestId);
  }

  [Test]
  public async Task AMalformedTraceParent_IsIgnored_AndTheSpanStartsUnderTheAmbientOneAsync() {
    using var listener = _listen();
    Activity.Current = null;

    using var span = IntegrityTracing.StartRepair("origin-svc", new IntegrityTraceLink((Guid)TrackedGuid.New(), "not-a-traceparent"));

    await Assert.That(span).IsNotNull();
    await Assert.That(span!.OperationName).IsEqualTo(IntegrityTracing.SPAN_REPAIR);
    await Assert.That(span.Kind).IsEqualTo(ActivityKind.Consumer);
    await Assert.That(span.ParentSpanId).IsEqualTo(default(ActivitySpanId))
      .Because("a bad link from an older or foreign sender never fails the repair; the span is a root");
  }

  [Test]
  public async Task ARepairWithNoLink_HasNoRequestIdAsync() {
    using var listener = _listen();

    using var span = IntegrityTracing.StartRepair("origin-svc", default);

    await Assert.That(span!.GetTagItem(IntegrityTracing.ATTR_REQUEST_ID)).IsNull()
      .Because("a bundle from an origin that predates the field names no request");
    await Assert.That(span.GetTagItem(IntegrityTracing.ATTR_PEER)).IsEqualTo("origin-svc");
  }

  [Test]
  public async Task AnAudit_ARecheck_AndAnAuditCycle_AreInternalSpansAsync() {
    using var listener = _listen();

    using var cycle = IntegrityTracing.StartAuditCycle();
    using var audit = IntegrityTracing.StartAudit(IntegrityTracing.KIND_MANIFEST, "origin-svc", default);
    using var recheck = IntegrityTracing.StartRecheck(StreamIntegrityMetrics.CHECK_SEAL_RECHECK);

    await Assert.That(cycle!.OperationName).IsEqualTo(IntegrityTracing.SPAN_AUDIT_CYCLE);
    await Assert.That(audit!.OperationName).IsEqualTo(IntegrityTracing.SPAN_AUDIT);
    await Assert.That(audit.ParentSpanId).IsEqualTo(cycle.SpanId).Because("with no link, the ambient span is the parent");
    await Assert.That(recheck!.OperationName).IsEqualTo(IntegrityTracing.SPAN_RECHECK);
    await Assert.That(recheck.GetTagItem(IntegrityTracing.ATTR_CHECK)).IsEqualTo(StreamIntegrityMetrics.CHECK_SEAL_RECHECK);
    await Assert.That(new[] { cycle.Kind, audit.Kind, recheck.Kind }.All(k => k == ActivityKind.Internal)).IsTrue();
  }

  [Test]
  public async Task CountsAndOutcomes_AreRecordedOnTheSpan_AndANullSpanIsIgnoredAsync() {
    using var listener = _listen();
    using var span = IntegrityTracing.StartAudit(IntegrityTracing.KIND_CHECKPOINT, "origin-svc", default);

    IntegrityTracing.SetCount(span, IntegrityTracing.ATTR_STREAMS, 3);
    IntegrityTracing.SetOutcome(span, IntegrityTracing.OUTCOME_REPAIR_REQUESTED);
    IntegrityTracing.SetCount(null, IntegrityTracing.ATTR_STREAMS, 3);
    IntegrityTracing.SetOutcome(null, IntegrityTracing.OUTCOME_REPAIR_REQUESTED);

    await Assert.That(span!.GetTagItem(IntegrityTracing.ATTR_STREAMS)).IsEqualTo(3L);
    await Assert.That(span.GetTagItem(IntegrityTracing.ATTR_OUTCOME)).IsEqualTo(IntegrityTracing.OUTCOME_REPAIR_REQUESTED);
  }

  [Test]
  public async Task WithNoListener_NoSpanStarts_AndNothingFailsAsync() {
    Activity.Current = null;
    // No listener on the source: every start returns null, which the callers pass on unchanged.
    await Assert.That(IntegrityTracing.StartAuditCycle()).IsNull();
    await Assert.That(IntegrityTracing.StartRepairRequest(IntegrityTracing.KIND_DRAIN, "o", Guid.Empty)).IsNull();
  }

  /// <summary>
  /// The design choice that spans carry no stream, tenant or event id (#2365): an integrity span names its peer service,
  /// its kind, its request and counts. Every attribute any start or setter writes is one of
  /// <see cref="IntegrityTracing.Attributes"/>, and none of those names a stream or a tenant.
  /// </summary>
  /// <docs>resilience/stream-integrity#integrity-traces</docs>
  [Test]
  public async Task EverySpanAttribute_IsOnTheBoundedList_AndNoneNamesAStreamOrATenantAsync() {
    using var listener = _listen();
    var link = new IntegrityTraceLink((Guid)TrackedGuid.New(), null);
    List<Activity?> spans = [
      IntegrityTracing.StartAuditCycle(),
      IntegrityTracing.StartAudit(IntegrityTracing.KIND_MANIFEST, "p", link),
      IntegrityTracing.StartAnswer(IntegrityTracing.KIND_MANIFEST, "p", link),
      IntegrityTracing.StartRepairRequest(IntegrityTracing.KIND_DRAIN, "p", Guid.Empty),
      IntegrityTracing.StartRepair("p", link),
      IntegrityTracing.StartRecheck(StreamIntegrityMetrics.CHECK_SEAL_RECHECK),
    ];
    foreach (var span in spans) {
      foreach (var count in new[] { IntegrityTracing.ATTR_EVENT_TYPES, IntegrityTracing.ATTR_STREAMS, IntegrityTracing.ATTR_EVENTS }) {
        IntegrityTracing.SetCount(span, count, 1);
      }
      IntegrityTracing.SetOutcome(span, IntegrityTracing.OUTCOME_CLEAN);
    }

    var written = spans.SelectMany(s => s!.TagObjects.Select(t => t.Key)).Distinct().ToList();
    foreach (var span in spans) {
      span!.Dispose();
    }

    await Assert.That(written.Where(k => !IntegrityTracing.Attributes.Contains(k)).ToList()).IsEmpty();
    await Assert.That(IntegrityTracing.Attributes.Where(k =>
      k.Contains("stream_id", StringComparison.Ordinal) || k.Contains("tenant", StringComparison.Ordinal)
      || k.Contains("scope", StringComparison.Ordinal) || k.Contains("event_id", StringComparison.Ordinal)).ToList()).IsEmpty();
    await Assert.That(written).Contains(IntegrityTracing.ATTR_REQUEST_ID)
      .Because("positive control: the check reads the attributes the spans actually carry");
  }
}
