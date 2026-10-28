// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Issue #1788: an inbox row claimed but queued behind others for longer than its lease lost the lease without ever
/// being dispatched, was re-claimed with another attempt charged, and was queued a second time while the first copy
/// still waited. Ten lapses dead-lettered (or, for control-plane traffic, dropped) a message no receptor ever saw.
/// The dispatch worker now keeps the leases of the rows it holds, releases uncharged any row it cannot dispatch in
/// time, dispatches each row once, and does not count attempt-cap drops as drain throughput.
/// </summary>
/// <docs>fundamentals/work-coordinator/inbox-dispatch#held-rows</docs>
[Category("Workers")]
public sealed class InboxDispatchWorkerHeldLeaseTests {
  private static readonly TimeSpan _halfLease = TimeSpan.FromSeconds(150);

  private sealed class LeaseCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public ConcurrentQueue<(WorkCategory Category, Guid[] Ids, int LeaseSeconds)> Renewals { get; } = new();
    public ConcurrentQueue<(Guid InstanceId, Guid[] Ids)> Releases { get; } = new();
    public Exception? RenewFailure { get; set; }
    public Exception? ReleaseFailure { get; set; }

    public Task<int> RenewLeasesAsync(WorkCategory category, IReadOnlyList<Guid> ids, int leaseSeconds = 300, CancellationToken cancellationToken = default) {
      if (RenewFailure is not null) {
        return Task.FromException<int>(RenewFailure);
      }
      Renewals.Enqueue((category, [.. ids], leaseSeconds));
      return Task.FromResult(ids.Count);
    }

    public Task<int> ReleaseUnprocessedInboxAsync(Guid instanceId, IReadOnlyList<Guid> messageIds, CancellationToken cancellationToken = default) {
      if (ReleaseFailure is not null) {
        return Task.FromException<int>(ReleaseFailure);
      }
      Releases.Enqueue((instanceId, [.. messageIds]));
      return Task.FromResult(messageIds.Count);
    }
  }

  private static (InboxDispatchUnitHarness H, InboxChannelWriter Writer, LeaseCoordinator Coordinator) _harness(
      LeaseRegistry? registry = null, int maxRenewals = 6) {
    var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(InboxDispatchUnitHarness.Start);
    var writer = new InboxChannelWriter(time);
    var coordinator = new LeaseCoordinator();
    var services = new ServiceCollection().AddSingleton<IWorkCoordinator>(coordinator);
    var h = new InboxDispatchUnitHarness(inbox: writer, services: services, leaseRegistry: registry,
      leaseHandleOptions: new LeaseHandleOptions { MaxRenewalsPerWork = maxRenewals }, time: time);
    return (h, writer, coordinator);
  }

  [Test]
  public async Task Keeper_RenewsAQueuedRowsLease_OnceHalfOfItIsSpentAsync() {
    var (h, writer, coordinator) = _harness();
    var work = InboxDispatchUnitHarness.Work();
    await writer.WriteAsync(work);

    await h.Worker.RenewHeldLeasesOnceAsync(CancellationToken.None);
    await Assert.That(coordinator.Renewals).IsEmpty();

    h.Time.Advance(_halfLease);
    await h.Worker.RenewHeldLeasesOnceAsync(CancellationToken.None);

    var (category, ids, leaseSeconds) = coordinator.Renewals.Single();
    await Assert.That(category).IsEqualTo(WorkCategory.Inbox);
    await Assert.That(ids).IsEquivalentTo([work.MessageId])
      .Because("nothing renewed inbox leases, so a row queued past 300 s lost its lease without ever being dispatched");
    await Assert.That(leaseSeconds).IsEqualTo(new LeaseRenewalWorkerOptions().LeaseSeconds);
  }

  [Test]
  public async Task Keeper_ExtendsTheDispatchDeadlineOfARowBeingDispatchedAsync() {
    var registry = new LeaseRegistry();
    var (h, writer, _) = _harness(registry);
    var work = InboxDispatchUnitHarness.Work();
    await writer.WriteAsync(work);
    _ = writer.TryBeginDispatch(work.MessageId);
    using var handle = new LeaseHandle(work.MessageId, WorkCategory.Inbox, h.Time.GetUtcNow() + TimeSpan.FromSeconds(270),
      maxRenewals: 6, timeProvider: h.Time, linkedTokens: []);
    registry.Register(handle);

    h.Time.Advance(_halfLease);
    await h.Worker.RenewHeldLeasesOnceAsync(CancellationToken.None);

    await Assert.That(handle.RenewalCount).IsEqualTo(1)
      .Because("the in-process deadline follows the store lease, or a handler outlives a lease it still holds");
  }

  [Test]
  public async Task Keeper_PastTheCap_ReleasesANeverDispatchedRowUncharged_AndLogsItAsync() {
    var (h, writer, coordinator) = _harness(maxRenewals: 1);
    var work = InboxDispatchUnitHarness.Work(messageType: "Contracts.Orders.OrderPlaced, Contracts");
    await writer.WriteAsync(work);
    h.Time.Advance(_halfLease);
    await h.Worker.RenewHeldLeasesOnceAsync(CancellationToken.None);

    h.Time.Advance(_halfLease);
    await h.Worker.RenewHeldLeasesOnceAsync(CancellationToken.None);

    var (instanceId, releasedIds) = coordinator.Releases.Single();
    await Assert.That(instanceId).IsEqualTo(h.InstanceId);
    await Assert.That(releasedIds).IsEquivalentTo([work.MessageId])
      .Because("attempts count dispatches, not queue time: a row this instance cannot reach in time goes back uncharged");
    var lapse = h.Logs.Single(l => l.Message.Contains(work.MessageId.ToString(), StringComparison.Ordinal));
    await Assert.That(lapse.Level).IsEqualTo(LogLevel.Warning);
    await Assert.That(lapse.Message).Contains("Contracts.Orders.OrderPlaced, Contracts").And.Contains("never dispatched");
  }

  [Test]
  public async Task Keeper_PastTheCap_DoesNotReleaseARowWhoseHandlerIsRunning_ButLogsItAsync() {
    var (h, writer, coordinator) = _harness(maxRenewals: 0);
    var dispatching = InboxDispatchUnitHarness.Work();
    var committing = InboxDispatchUnitHarness.Work();
    await writer.WriteAsync(dispatching);
    await writer.WriteAsync(committing);
    _ = writer.TryBeginDispatch(dispatching.MessageId);
    _ = writer.TryBeginDispatch(committing.MessageId);
    writer.MarkCommitPending(committing.MessageId);

    h.Time.Advance(_halfLease);
    await h.Worker.RenewHeldLeasesOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.Releases).IsEmpty()
      .Because("a row whose handler ran was dispatched; refunding its attempt would let a crashing handler retry for ever");
    await Assert.That(h.Logs.Any(l => l.Level == LogLevel.Warning && l.Message.Contains(dispatching.MessageId.ToString(), StringComparison.Ordinal)
      && l.Message.Contains("still running", StringComparison.Ordinal))).IsTrue();
    await Assert.That(h.Logs.Any(l => l.Level == LogLevel.Warning && l.Message.Contains(committing.MessageId.ToString(), StringComparison.Ordinal)
      && l.Message.Contains("commit has not landed", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task Keeper_ManyLapsesInOneSweep_LogTheFirstIndividually_ThenASummaryAsync() {
    var (h, writer, coordinator) = _harness(maxRenewals: 0);
    for (var i = 0; i < 8; i++) {
      await writer.WriteAsync(InboxDispatchUnitHarness.Work());
    }

    h.Time.Advance(_halfLease);
    await h.Worker.RenewHeldLeasesOnceAsync(CancellationToken.None);

    await Assert.That(h.Logs.Count(l => l.Message.Contains("never dispatched", StringComparison.Ordinal)))
      .IsEqualTo(InboxDispatchWorker.MAX_LEASE_LAPSES_LOGGED_PER_SWEEP);
    await Assert.That(h.Logs.Any(l => l.Message.Contains("3 more", StringComparison.Ordinal))).IsTrue();
    await Assert.That(coordinator.Releases.Single().Ids.Length).IsEqualTo(8);
  }

  [Test]
  public async Task Keeper_RenewalFailure_IsLoggedWithWhatItCosts_NotThrownAsync() {
    var (h, writer, coordinator) = _harness();
    coordinator.RenewFailure = new InvalidOperationException("connection reset");
    await writer.WriteAsync(InboxDispatchUnitHarness.Work());
    h.Time.Advance(_halfLease);

    await h.Worker.RenewHeldLeasesOnceAsync(CancellationToken.None);

    await Assert.That(h.Logs.Any(l => l.Level == LogLevel.Warning && l.Message.Contains("could not renew", StringComparison.Ordinal)))
      .IsTrue();
  }

  [Test]
  public async Task Keeper_ReleaseFailure_IsLoggedWithWhatItCosts_NotThrownAsync() {
    var (h, writer, coordinator) = _harness(maxRenewals: 0);
    coordinator.ReleaseFailure = new InvalidOperationException("connection reset");
    await writer.WriteAsync(InboxDispatchUnitHarness.Work());
    h.Time.Advance(_halfLease);

    await h.Worker.RenewHeldLeasesOnceAsync(CancellationToken.None);

    await Assert.That(h.Logs.Any(l => l.Level == LogLevel.Warning && l.Message.Contains("could not release", StringComparison.Ordinal)))
      .IsTrue();
  }

  [Test]
  public async Task Dispatch_ADuplicateCopy_IsNotDispatchedAgainAsync() {
    var (h, writer, _) = _harness();
    var work = InboxDispatchUnitHarness.Work(attempts: 11, messageType: TypeNameFormatter.Format(typeof(IntegrityCheckpoint)));
    await writer.WriteAsync(work);
    await writer.WriteAsync(work);

    await h.Worker.DispatchQueuedAsync(work, CancellationToken.None);
    await h.Worker.DispatchQueuedAsync(work, CancellationToken.None);

    await Assert.That(h.Commits.All.Count).IsEqualTo(1)
      .Because("a row is never processed twice in one process while its first copy is pending");
  }

  [Test]
  public async Task Dispatch_HandingTheCommitOn_KeepsTheRowHeldUntilTheCommitLandsAsync() {
    var (h, writer, _) = _harness();
    var work = InboxDispatchUnitHarness.Work(attempts: 11, messageType: TypeNameFormatter.Format(typeof(IntegrityCheckpoint)));
    await writer.WriteAsync(work);

    await h.Worker.DispatchQueuedAsync(work, CancellationToken.None);

    await Assert.That(writer.IsInFlight(work.MessageId)).IsTrue()
      .Because("the row is unprocessed in the store until the batched commit lands; offering it again would dispatch it twice");
  }

  [Test]
  public async Task Dispatch_EndingWithoutACommit_ReleasesTheRowAsync() {
    var (h, writer, _) = _harness();
    var work = InboxDispatchUnitHarness.Work(attempts: 11, messageType: "Contracts.Orders.OrderPlaced, Contracts");
    await writer.WriteAsync(work);

    // A domain row past its attempts is dead-lettered by the store, with no commit.
    await h.Worker.DispatchQueuedAsync(work, CancellationToken.None);

    await Assert.That(writer.IsInFlight(work.MessageId)).IsFalse();
  }

  [Test]
  public async Task Meter_AttemptCapDrops_AreNotCountedAsDrainAsync() {
    var (h, writer, _) = _harness();
    var exhausted = InboxDispatchUnitHarness.Work(attempts: 11, messageType: TypeNameFormatter.Format(typeof(IntegrityCheckpoint)));
    var fresh = InboxDispatchUnitHarness.Work(attempts: 1);
    await writer.WriteAsync(exhausted);
    await writer.WriteAsync(fresh);

    await h.Worker.DispatchQueuedAsync(exhausted, CancellationToken.None);
    await Assert.That(h.Meter.ReadAndReset()).IsEqualTo(0)
      .Because("a flood of rows dropped in microseconds read as high throughput and raised the budget past what dispatch could finish");

    await h.Worker.DispatchQueuedAsync(fresh, CancellationToken.None);
    await Assert.That(h.Meter.ReadAndReset()).IsEqualTo(1);
  }
}
