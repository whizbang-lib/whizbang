// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// #2211: a window wider than the consumer's page budget is walked across audits. The stream-level walk of a window that
/// disagrees pages through the origin's streams in stream-id order and stops after
/// <see cref="StreamIntegrityOptions.MaxManifestPagesPerAudit"/> follow-ups; it used to drop its cursor there, and the
/// next audit's drill-down started from the first stream again, so the same leading streams were compared and repaired
/// every audit and the streams past the budget never were. The stopped walk now keeps its cursor, and the next
/// drill-down of the window resumes after it (once: a walk that ends leaves none, and the next starts from the first
/// stream).
/// </summary>
public partial class IntegrityManifestReceptorTests {
  private const long CURSOR_SINCE = 100;
  private const long CURSOR_THROUGH = 300;

  private static (AuditCoordinator Coordinator, CaptureTransport Transport, IntegrityManifestReceptor Receptor) _cursorFixture(
      int maxPages) {
    var coordinator = new AuditCoordinator {
      // The type roll-up disagrees, so every type-level answer drills down.
      WindowedTypeResult = new WindowedDigestResult { Digests = [_typeDigest("Contracts.TypeX", 99, 98, 5)], ComputedThrough = CURSOR_THROUGH },
    };
    var transport = new CaptureTransport();
    var tracker = new IntegrityGapTracker();
    var sp = _provider(coordinator, transport,
      new StreamIntegrityOptions { MaxManifestPagesPerAudit = maxPages, PublishReportEvents = false }, tracker: tracker);
    tracker.RecordCheckpoint(coordinator.OriginId, "origin-svc", _fixedNow, "origin.requests");
    var receptor = new IntegrityManifestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);
    return (coordinator, transport, receptor);
  }

  private static IntegrityManifest _typeAnswer(AuditCoordinator coordinator) =>
    _manifest(coordinator, [_typeDigest("Contracts.TypeX", 41, 42, 5)], ManifestLevel.Types)
      with { SinceSequence = CURSOR_SINCE, ComputedThrough = CURSOR_THROUGH, ChunkCount = 1 };

  // One page of the stream-level walk: a matching stream (only the paging is under test) and the origin's cursor.
  private static IntegrityManifest _streamPage(AuditCoordinator coordinator, Guid stream, Guid? cursor) =>
    _manifest(coordinator, [_digest(stream, 41, 42, 5)])
      with { SinceSequence = CURSOR_SINCE, ComputedThrough = CURSOR_THROUGH, ChunkCount = 1, ResumeAfterStreamId = cursor };

  private static RequestIntegrityManifest[] _streamAsks(CaptureTransport transport) =>
    [.. transport.Published.Select(p => _tryDeserializeRequest(p.Envelope)).Where(r => r is { Level: ManifestLevel.Streams })!];

  [Test]
  public async Task DrillCursor_AWalkStoppedByThePageBudget_ResumesWhereItStopped_AtTheNextAuditsDrillDownAsync() {
    var (coordinator, transport, receptor) = _cursorFixture(maxPages: 0);
    var stream = TrackedGuid.New().Value;
    var stoppedAfter = TrackedGuid.New().Value;
    coordinator.ReceivedDigests = [_digest(stream, 41, 42, 5)];

    // Audit 1: the drill-down, then the first page of a window wider than the budget.
    await receptor.HandleAsync(_typeAnswer(coordinator));
    await receptor.HandleAsync(_streamPage(coordinator, stream, stoppedAfter));
    // Audit 2: the window still disagrees.
    await receptor.HandleAsync(_typeAnswer(coordinator));

    var asks = _streamAsks(transport);
    await Assert.That(asks.Length).IsEqualTo(2).Because("one drill-down per audit, and the budget allows no follow-up");
    await Assert.That(asks[0].ResumeAfterStreamId).IsNull().Because("the first walk of a window starts at its first stream");
    await Assert.That(asks[1].ResumeAfterStreamId).IsEqualTo(stoppedAfter)
      .Because("the next audit compares the streams past where the budget stopped the last walk, not the same leading streams");
    await Assert.That(asks[1].SinceSequence).IsEqualTo(CURSOR_SINCE);
    await Assert.That(asks[1].UntilSequence).IsEqualTo(CURSOR_THROUGH);
    await Assert.That(coordinator.DrillCursors).IsEmpty().Because("a cursor is used once");
  }

  [Test]
  public async Task DrillCursor_TheResumedWalkEnds_TheNextDrillDownStartsFromTheFirstStreamAsync() {
    var (coordinator, transport, receptor) = _cursorFixture(maxPages: 0);
    var stream = TrackedGuid.New().Value;
    coordinator.ReceivedDigests = [_digest(stream, 41, 42, 5)];
    coordinator.DrillCursors[(coordinator.OriginId, CURSOR_SINCE)] = TrackedGuid.New().Value;

    await receptor.HandleAsync(_typeAnswer(coordinator));               // resumes
    await receptor.HandleAsync(_streamPage(coordinator, stream, null)); // and reaches the window's last stream
    await receptor.HandleAsync(_typeAnswer(coordinator));               // the next audit

    var asks = _streamAsks(transport);
    await Assert.That(asks[0].ResumeAfterStreamId.HasValue).IsTrue();
    await Assert.That(asks[1].ResumeAfterStreamId).IsNull()
      .Because("the walk wrapped: the leading streams are compared again, so every stream of the window is reached in turn");
  }

  [Test]
  public async Task DrillCursor_ADrillDownStartsAFreshPageBudget_ForItsWindowAsync() {
    var (coordinator, transport, receptor) = _cursorFixture(maxPages: 1);
    var stream = TrackedGuid.New().Value;
    coordinator.ReceivedDigests = [_digest(stream, 41, 42, 5)];
    var (first, second, third) = (TrackedGuid.New().Value, TrackedGuid.New().Value, TrackedGuid.New().Value);

    await receptor.HandleAsync(_typeAnswer(coordinator));
    await receptor.HandleAsync(_streamPage(coordinator, stream, first));    // followed: the budget's one page
    await receptor.HandleAsync(_streamPage(coordinator, stream, second));   // stopped, the cursor kept
    await receptor.HandleAsync(_typeAnswer(coordinator));                   // the next audit resumes after second
    await receptor.HandleAsync(_streamPage(coordinator, stream, third));    // followed again

    var asks = _streamAsks(transport);
    await Assert.That(asks.Select(a => a.ResumeAfterStreamId)).IsEquivalentTo(new Guid?[] { null, first, second, third })
      .Because("a new drill-down is a new walk; the budget the last audit spent must not stop it after its first page");
  }

  [Test]
  public async Task DrillCursor_ARecheckWalkStoppedByThePageBudget_KeepsNoCursorAsync() {
    var (coordinator, _, receptor) = _cursorFixture(maxPages: 0);
    var stream = TrackedGuid.New().Value;
    coordinator.ReceivedDigests = [_digest(stream, 41, 42, 5)];

    await receptor.HandleAsync(_streamPage(coordinator, stream, TrackedGuid.New().Value) with { Recheck = true });

    await Assert.That(coordinator.DrillCursors).IsEmpty()
      .Because("a re-check walks the window of its own rows; the audit's drill-down must not resume from it");
  }

  [Test]
  public async Task DrillCursor_AnUnwindowedDrillDown_TakesNoCursorAsync() {
    var (coordinator, transport, receptor) = _cursorFixture(maxPages: 0);
    coordinator.DrillCursors[(coordinator.OriginId, 0)] = TrackedGuid.New().Value;
    coordinator.ReceivedTypeDigests = [_typeDigest("Contracts.TypeX", 99, 98, 5)];

    await receptor.HandleAsync(_manifest(coordinator, [_typeDigest("Contracts.TypeX", 41, 42, 5)], ManifestLevel.Types));

    var ask = _streamAsks(transport).Single();
    await Assert.That(ask.Windowed).IsFalse();
    await Assert.That(ask.ResumeAfterStreamId).IsNull().Because("a full-history drill-down is not paged, so it has no walk to resume");
    await Assert.That(coordinator.DrillCursors.Count).IsEqualTo(1).Because("nothing was taken");
  }

  [Test]
  public async Task DrillCursor_ADrillDownWithoutTheOriginsAddress_LeavesTheCursorForTheNextAuditAsync() {
    var coordinator = new AuditCoordinator {
      WindowedTypeResult = new WindowedDigestResult { Digests = [_typeDigest("Contracts.TypeX", 99, 98, 5)], ComputedThrough = CURSOR_THROUGH },
    };
    var transport = new CaptureTransport();
    var sp = _provider(coordinator, transport, new StreamIntegrityOptions { PublishReportEvents = false }, tracker: new IntegrityGapTracker());
    var receptor = new IntegrityManifestReceptor(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrityManifestReceptor>.Instance);
    var kept = TrackedGuid.New().Value;
    coordinator.DrillCursors[(coordinator.OriginId, CURSOR_SINCE)] = kept;

    await receptor.HandleAsync(_typeAnswer(coordinator));

    await Assert.That(_streamAsks(transport)).IsEmpty();
    await Assert.That(coordinator.DrillCursors[(coordinator.OriginId, CURSOR_SINCE)]).IsEqualTo(kept)
      .Because("a drill-down that never left must not use up the cursor");
  }
}
