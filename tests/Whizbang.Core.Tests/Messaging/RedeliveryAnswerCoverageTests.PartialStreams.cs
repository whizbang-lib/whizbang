// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// #2149: a bundle that carried only part of a stream's answer (the rest in another bundle, on another page, or beyond the
/// origin's cap) says so, and a bucket of such a stream is answered but never marked as one repair cannot change, however
/// wide the bundle's window: its missing events may be in the part the bundle did not carry.
/// </summary>
public partial class RedeliveryAnswerCoverageTests {
  private static RedeliveryComposite _partialBundle(Guid origin, Guid stream, RedeliveryAnswerWindow window, params Guid[] partial) {
    var bundle = _bundle(origin, stream, window);
    return new RedeliveryComposite {
      StreamId = bundle.StreamId,
      OriginServiceId = bundle.OriginServiceId,
      InnerEventIds = bundle.InnerEventIds,
      InnerTypeNames = bundle.InnerTypeNames,
      InnerPayloads = bundle.InnerPayloads,
      InnerCommitSequences = bundle.InnerCommitSequences,
      AnswersWindow = true,
      AnsweredFromCommitSequence = window.FromCommitSequence,
      AnsweredToCommitSequence = window.ToCommitSequence,
      AnsweredPartialStreamIds = [.. partial],
    };
  }

  [Test]
  public async Task Composite_SaysThePartialStreams_WithItsWindowAsync() {
    var stream = TrackedGuid.New().Value;
    IIdentityPreservingComposite partial = new RedeliveryComposite { AnswersWindow = true, AnsweredPartialStreamIds = [stream] };
    IIdentityPreservingComposite whole = new RedeliveryComposite { AnswersWindow = true };

    await Assert.That(partial.AnsweredWindow!.Value.PartialStreamIds).IsEquivalentTo([stream]);
    await Assert.That(whole.AnsweredWindow!.Value.PartialStreamIds).IsNull();
  }

  [Test]
  public async Task Reconcile_AWholeHistoryAnswerThatCarriedTheStreamOnlyInPart_LeavesTheBucketRepairableAsync() {
    var (origin, stream) = (TrackedGuid.New().Value, TrackedGuid.New().Value);
    var bundle = _partialBundle(origin, stream, new RedeliveryAnswerWindow(null, null), stream);
    var (coordinator, ledger, key) = _store(origin, stream);
    await using var services = _services(coordinator, ledger);

    var result = await RedeliveryReconciler.ReconcileAsync(bundle, _children(bundle), services, NullLogger.Instance, _now);

    await Assert.That(result.MarkedUnhealable).IsEqualTo(0)
      .Because("the stream's missing events may be in the bundle or page this one did not carry, or beyond the origin's cap");
    await Assert.That(ledger.TryBeginRepair(key, _now.AddDays(1), TimeSpan.FromSeconds(1), 3)).IsTrue();
  }

  [Test]
  public async Task Reconcile_APartialStreamOfAnotherBucket_DoesNotStopThisBucketsMarkAsync() {
    var (origin, stream) = (TrackedGuid.New().Value, TrackedGuid.New().Value);
    var bundle = _partialBundle(origin, stream, new RedeliveryAnswerWindow(null, null), TrackedGuid.New().Value);
    var (coordinator, ledger, _) = _store(origin, stream);
    await using var services = _services(coordinator, ledger);

    var result = await RedeliveryReconciler.ReconcileAsync(bundle, _children(bundle), services, NullLogger.Instance, _now);

    await Assert.That(result.MarkedUnhealable).IsEqualTo(1)
      .Because("this stream was carried whole and the answer covered its history: asking again re-ships the same events");
  }

  [Test]
  public async Task TheDrain_ReadsThePartialStreamsBack_AndLeavesTheirBucketsRepairableAsync() {
    var (origin, stream) = (TrackedGuid.New().Value, TrackedGuid.New().Value);
    var bundle = _partialBundle(origin, stream, new RedeliveryAnswerWindow(null, null), stream);
    var (coordinator, ledger, key) = _store(origin, stream, deferring: true);
    coordinator.BundleAnswer = new IntegrityBundleAnswer(2, 2, 0, 0, 0, [], []);
    await using var services = _services(coordinator, ledger, withDeliveries: true);
    var deferred = await RedeliveryReconciler.ReconcileAsync(bundle, _children(bundle), services, NullLogger.Instance, _now);
    await Assert.That(deferred.Deferred).IsTrue().Because("precondition: the reconcile waits for the maintainer");

    _ = services.GetRequiredService<IntegrityDeliveryRegistry>().TryGetHandler(RedeliveryReconciler.DELIVERY_TYPE, out var handler);
    await handler!(coordinator.Pending[0].Payload, services, CancellationToken.None);

    await Assert.That(coordinator.Adoptions.Count).IsEqualTo(1);
    await Assert.That(ledger.TryBeginRepair(key, _now.AddDays(1), TimeSpan.FromSeconds(1), 3)).IsTrue()
      .Because("the deferred reconcile judges the bundle with the partial streams it carried");
  }

  [Test]
  public async Task Reconcile_GivesTheCommitTimeRecordThePartialStreamsAsync() {
    var (origin, stream) = (TrackedGuid.New().Value, TrackedGuid.New().Value);
    var bundle = _partialBundle(origin, stream, new RedeliveryAnswerWindow(300, 400), stream);
    var (coordinator, ledger, _) = _store(origin, stream);
    await using var services = _services(coordinator, ledger);

    _ = await RedeliveryReconciler.ReconcileAsync(bundle, _children(bundle), services, NullLogger.Instance, _now);

    await Assert.That(coordinator.BundleAnswerWindows.Single()!.Value.PartialStreamIds).IsEquivalentTo([stream])
      .Because("the durable store marks at commit, so it needs to know which streams the bundle carried only in part");
  }
}
