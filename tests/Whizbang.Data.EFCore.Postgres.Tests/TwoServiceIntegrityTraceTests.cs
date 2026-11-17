// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2365, across two services over two real stores (<see cref="TwoServiceHarness"/>): one trace runs from the consumer's
/// audit cycle, through the origin's manifest answer, the consumer's comparison and its repair request, the origin's
/// redelivery answer, to the consumer applying the repair. Each hop carries the request id and the trace link on the
/// message itself.
/// </summary>
/// <docs>resilience/stream-integrity#integrity-traces</docs>
[Category("Integration")]
[Category("Shard4")]
[NotInParallel("IntegrityManifestGates")]   // the manifest receptors' per-process gates are shared state
public class TwoServiceIntegrityTraceTests : EFCoreTestBase {
  private const string SHARED = "TwoService.Contracts.OrderPlaced, TwoService.Contracts";
  private const string PAYLOAD = """{"k":"v"}""";

  [Test]
  public async Task ALossFoundByAnAudit_IsRepairedInOneTrace_AcrossBothServicesAsync() {
    var spans = new ConcurrentBag<Activity>();
    using var listener = new ActivityListener {
      ShouldListenTo = s => s.Name == IntegrityTracing.SOURCE_NAME,
      Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
      ActivityStopped = spans.Add,
    };
    ActivitySource.AddActivityListener(listener);
    await using var h = await TwoServiceHarness.CreateAsync(ConnectionString, DbContextOptions, [], consumedEventTypes: [SHARED]);
    var ids = await h.EmitAsync(
      new TwoServiceEmit((Guid)TrackedGuid.New(), SHARED, 0, null, PAYLOAD),
      new TwoServiceEmit((Guid)TrackedGuid.New(), SHARED, 0, null, PAYLOAD));
    await h.DeliverAsync([ids[0]]);

    await h.RunConsumerAuditAsync(new StreamIntegrityOptions {
      RepairMode = IntegrityRepairMode.AutoRepairCapped,
      AuditSettleWindowMinutes = 0,
      RepairRequestBackoffSeconds = 0,
      RepairDrainEnabled = false,
      MaxAskSkipMinutes = 0,
    });

    await Assert.That(await h.ConsumerHoldsAsync(ids[1])).IsTrue().Because("the audit found the loss and the repair stored it");

    // The request and every bundle answering it carry the same request id and a trace link.
    var request = h.RedeliveryRequests().Single();
    await Assert.That(request.RequestId.HasValue).IsTrue();
    await Assert.That(request.TraceParent).IsNotNull();
    var bundles = h.RedeliveryBundles();
    await Assert.That(bundles.Count).IsGreaterThan(0);
    await Assert.That(bundles.All(b => b.RequestId == request.RequestId && b.TraceParent is not null)).IsTrue();

    // One trace holds the whole exchange, consumer and origin alike.
    var repair = spans.Single(s => s.OperationName == IntegrityTracing.SPAN_REPAIR
      && (string?)s.GetTagItem(IntegrityTracing.ATTR_REQUEST_ID) == request.RequestId.ToString());
    var trace = spans.Where(s => s.TraceId == repair.TraceId).ToList();
    string Describe(Activity s) => $"{s.OperationName}:{s.GetTagItem(IntegrityTracing.ATTR_KIND)}:{s.GetTagItem(IntegrityTracing.ATTR_PEER)}";
    var described = trace.ConvertAll(Describe);
    await Assert.That(described).Contains($"{IntegrityTracing.SPAN_AUDIT_CYCLE}::");
    await Assert.That(described).Contains($"{IntegrityTracing.SPAN_ANSWER}:{IntegrityTracing.KIND_MANIFEST}:{TwoServiceHarness.CONSUMER_NAME}");
    await Assert.That(described).Contains($"{IntegrityTracing.SPAN_AUDIT}:{IntegrityTracing.KIND_MANIFEST}:{TwoServiceHarness.ORIGIN_NAME}");
    await Assert.That(described).Contains($"{IntegrityTracing.SPAN_REPAIR_REQUEST}:{IntegrityTracing.KIND_INLINE}:{TwoServiceHarness.ORIGIN_NAME}");
    await Assert.That(described).Contains($"{IntegrityTracing.SPAN_ANSWER}:{IntegrityTracing.KIND_REDELIVERY}:{TwoServiceHarness.CONSUMER_NAME}");
    await Assert.That(described).Contains($"{IntegrityTracing.SPAN_REPAIR}:{IntegrityTracing.KIND_REDELIVERY}:{TwoServiceHarness.ORIGIN_NAME}");

    // And each hop is the child of the one before it, walked up from the repair: the audit that found the loss may be a
    // drill-down below the first comparison, so the chain is followed by parent, never picked by name.
    var bySpan = trace.ToDictionary(s => s.SpanId);
    var redeliveryAnswer = bySpan[repair.ParentSpanId];
    var repairRequest = bySpan[redeliveryAnswer.ParentSpanId];
    var audit = bySpan[repairRequest.ParentSpanId];
    await Assert.That(Describe(redeliveryAnswer))
      .IsEqualTo($"{IntegrityTracing.SPAN_ANSWER}:{IntegrityTracing.KIND_REDELIVERY}:{TwoServiceHarness.CONSUMER_NAME}");
    await Assert.That(Describe(repairRequest))
      .IsEqualTo($"{IntegrityTracing.SPAN_REPAIR_REQUEST}:{IntegrityTracing.KIND_INLINE}:{TwoServiceHarness.ORIGIN_NAME}");
    await Assert.That(Describe(audit))
      .IsEqualTo($"{IntegrityTracing.SPAN_AUDIT}:{IntegrityTracing.KIND_MANIFEST}:{TwoServiceHarness.ORIGIN_NAME}");
    await Assert.That(Describe(bySpan[audit.ParentSpanId]))
      .IsEqualTo($"{IntegrityTracing.SPAN_ANSWER}:{IntegrityTracing.KIND_MANIFEST}:{TwoServiceHarness.CONSUMER_NAME}")
      .Because("the comparison starts under the manifest answer it compares");
    var root = audit;
    while (bySpan.TryGetValue(root.ParentSpanId, out var parent)) {
      root = parent;
    }
    await Assert.That(root.OperationName).IsEqualTo(IntegrityTracing.SPAN_AUDIT_CYCLE).Because("the trace starts at the audit cycle");
  }
}
