// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The coordinator-backed ledger's mark for buckets repair cannot change (#1790): one round trip through the store,
/// and when the store cannot answer, nothing marked, so repair stays on its terminal cadence rather than stopping on a
/// fact nobody recorded.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/CoordinatorIntegrityRepairLedger.cs</code-under-test>
/// <docs>resilience/stream-integrity#unhealable-buckets</docs>
[Category("Shard4")]
public class CoordinatorIntegrityRepairLedgerUnhealableTests {
  private static readonly DateTimeOffset _now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

  private sealed class MarkingCoordinator : IWorkCoordinator {
    public List<(Guid Origin, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> Keys, long? From, long? Until, TimeSpan Timeout)> PendingMarks { get; } = [];

    public Task<int> IntegrityMarkAnswerPendingAsync(
        Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys, long? windowFrom, long? windowUntil,
        DateTimeOffset now, TimeSpan timeout, CancellationToken cancellationToken = default) {
      if (Failure is not null) {
        return Task.FromException<int>(Failure);
      }
      PendingMarks.Add((originServiceId, keys, windowFrom, windowUntil, timeout));
      return Task.FromResult(keys.Count);
    }

    public (RedeliveryAnswerWindow Window, IReadOnlyList<Guid> EventIds)? WindowedMark { get; private set; }

    public IReadOnlyList<IntegrityRepairLedger.DivergenceKey>? Answer { get; set; }
    public Exception? Failure { get; set; }
    public int Calls { get; private set; }
    public Guid? Origin { get; private set; }

    public Task<IReadOnlyList<IntegrityRepairLedger.DivergenceKey>?> IntegrityMarkUnhealableBatchAsync(
        Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys, DateTimeOffset now,
        CancellationToken cancellationToken = default) {
      Calls++;
      Origin = originServiceId;
      return Failure is not null
        ? Task.FromException<IReadOnlyList<IntegrityRepairLedger.DivergenceKey>?>(Failure)
        : Task.FromResult(Answer);
    }

    public Task<IReadOnlyList<IntegrityRepairLedger.DivergenceKey>?> IntegrityMarkUnhealableBatchAsync(
        Guid originServiceId, IReadOnlyList<IntegrityRepairLedger.DivergenceKey> keys, RedeliveryAnswerWindow answeredWindow,
        IReadOnlyList<Guid> answeredEventIds, DateTimeOffset now, CancellationToken cancellationToken = default) {
      WindowedMark = (answeredWindow, answeredEventIds);
      return IntegrityMarkUnhealableBatchAsync(originServiceId, keys, now, cancellationToken);
    }

    public Task<WorkBatch> ClaimWorkAsync(ClaimWorkRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WorkCoordinatorStatistics());
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) => Task.FromResult<PerspectiveCursorInfo?>(null);
  }

  private sealed class CapturingLogger : ILogger<CoordinatorIntegrityRepairLedger> {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Entries.Add((logLevel, formatter(state, exception)));
  }

  private static (CoordinatorIntegrityRepairLedger Ledger, CapturingLogger Logger, ServiceProvider Services) _build(
      MarkingCoordinator? coordinator) {
    var services = new ServiceCollection();
    if (coordinator is not null) {
      services.AddSingleton<IWorkCoordinator>(coordinator);
    }
    var sp = services.BuildServiceProvider();
    var logger = new CapturingLogger();
    return (new CoordinatorIntegrityRepairLedger(sp.GetRequiredService<IServiceScopeFactory>(), logger), logger, sp);
  }

  private static IntegrityRepairLedger.DivergenceKey _key(Guid origin) =>
    new(origin, "tenant-a", "Contracts.TypeX", TrackedGuid.New().Value);

  [Test]
  public async Task MarkUnhealable_AsksTheStoreOnce_AndReturnsWhatItMarkedAsync() {
    var origin = TrackedGuid.New().Value;
    var marked = _key(origin);
    var coordinator = new MarkingCoordinator { Answer = [marked] };
    var (ledger, _, sp) = _build(coordinator);
    await using var owned = sp;

    var result = await ledger.MarkUnhealableBatchAsync([marked, _key(origin)], _now);

    await Assert.That(result).IsEquivalentTo([marked]);
    await Assert.That(coordinator.Calls).IsEqualTo(1);
    await Assert.That(coordinator.Origin).IsEqualTo(origin);
  }

  [Test]
  public async Task MarkUnhealable_WithAWindow_GivesTheStoreTheWindowAndTheEventsAsync() {
    var origin = TrackedGuid.New().Value;
    var marked = _key(origin);
    var carried = TrackedGuid.New().Value;
    var coordinator = new MarkingCoordinator { Answer = [marked] };
    var (ledger, _, sp) = _build(coordinator);
    await using var owned = sp;

    var result = await ledger.MarkUnhealableBatchAsync([marked], new RedeliveryAnswerWindow(3, 9), [carried], _now);

    await Assert.That(result).IsEquivalentTo([marked]);
    await Assert.That(coordinator.WindowedMark!.Value.Window).IsEqualTo(new RedeliveryAnswerWindow(3, 9))
      .Because("the store judges which rows the answer covered (#2143)");
    await Assert.That(coordinator.WindowedMark!.Value.EventIds).IsEquivalentTo([carried]);
  }

  [Test]
  public async Task MarkUnhealable_StoreCannotAnswer_MarksNothingAsync() {
    var coordinator = new MarkingCoordinator { Answer = null };
    var (ledger, _, sp) = _build(coordinator);
    await using var owned = sp;

    var result = await ledger.MarkUnhealableBatchAsync([_key(TrackedGuid.New().Value)], _now);

    await Assert.That(result).IsEmpty();
  }

  [Test]
  public async Task MarkUnhealable_StoreFails_LogsAndMarksNothingAsync() {
    var coordinator = new MarkingCoordinator { Failure = new InvalidOperationException("ledger down") };
    var (ledger, logger, sp) = _build(coordinator);
    await using var owned = sp;

    var result = await ledger.MarkUnhealableBatchAsync([_key(TrackedGuid.New().Value)], _now);

    await Assert.That(result).IsEmpty();
    await Assert.That(logger.Entries.Single(e => e.Level == LogLevel.Warning).Message).Contains("mark-unhealable");
  }

  [Test]
  public async Task MarkUnhealable_NoCoordinator_MarksNothingAsync() {
    var (ledger, _, sp) = _build(null);
    await using var owned = sp;

    var result = await ledger.MarkUnhealableBatchAsync([_key(TrackedGuid.New().Value)], _now);

    await Assert.That(result).IsEmpty();
  }

  [Test]
  public async Task MarkUnhealable_NoKeys_ConsultsNothingAsync() {
    var coordinator = new MarkingCoordinator { Answer = [] };
    var (ledger, _, sp) = _build(coordinator);
    await using var owned = sp;

    var result = await ledger.MarkUnhealableBatchAsync([], _now);

    await Assert.That(result).IsEmpty();
    await Assert.That(coordinator.Calls).IsEqualTo(0);
  }

  [Test]
  public async Task MarkUnhealable_Canceled_PropagatesAsync() {
    var coordinator = new MarkingCoordinator { Failure = new OperationCanceledException() };
    var (ledger, _, sp) = _build(coordinator);
    await using var owned = sp;

    await Assert.That(async () => await ledger.MarkUnhealableBatchAsync([_key(TrackedGuid.New().Value)], _now))
      .Throws<OperationCanceledException>();
  }

  // #2029: the answer states go to the store, one round trip per origin.

  [Test]
  public async Task MarkAnswerPending_GoesToTheStore_OncePerOrigin_WithTheWindowAndTimeoutAsync() {
    var first = TrackedGuid.New().Value;
    var second = TrackedGuid.New().Value;
    var coordinator = new MarkingCoordinator();
    var (ledger, _, sp) = _build(coordinator);
    await using var owned = sp;

    var marked = await ledger.MarkAnswerPendingAsync([_key(first), _key(second), _key(first)], 10, 20, _now, TimeSpan.FromMinutes(7));

    await Assert.That(marked).IsEqualTo(3);
    await Assert.That(coordinator.PendingMarks.Count).IsEqualTo(2);
    var forFirst = coordinator.PendingMarks.Single(m => m.Origin == first);
    await Assert.That(forFirst.Keys.Count).IsEqualTo(2);
    await Assert.That(forFirst.From).IsEqualTo(10L);
    await Assert.That(forFirst.Until).IsEqualTo(20L);
    await Assert.That(forFirst.Timeout).IsEqualTo(TimeSpan.FromMinutes(7));
  }

  [Test]
  public async Task MarkAnswerPending_WithoutAStore_MarksNothingAsync() {
    var (ledger, _, sp) = _build(null);
    await using var owned = sp;

    await Assert.That(await ledger.MarkAnswerPendingAsync([_key(TrackedGuid.New().Value)], null, null, _now, TimeSpan.FromMinutes(1)))
      .IsEqualTo(0);
  }

  [Test]
  public async Task MarkAnswerPending_AStoreFailure_IsLogged_AndMarksNothingAsync() {
    var (ledger, logger, sp) = _build(new MarkingCoordinator { Failure = new InvalidOperationException("db down") });
    await using var owned = sp;

    var marked = await ledger.MarkAnswerPendingAsync([_key(TrackedGuid.New().Value)], null, null, _now, TimeSpan.FromMinutes(1));

    await Assert.That(marked).IsEqualTo(0);
    await Assert.That(logger.Entries.Single(e => e.Level == LogLevel.Warning).Message).Contains("answer-pending");
  }

  [Test]
  public async Task MarkAnswerPending_Canceled_PropagatesAsync() {
    var (ledger, _, sp) = _build(new MarkingCoordinator { Failure = new OperationCanceledException() });
    await using var owned = sp;

    await Assert.That(async () => await ledger.MarkAnswerPendingAsync([_key(TrackedGuid.New().Value)], null, null, _now, TimeSpan.FromMinutes(1)))
      .Throws<OperationCanceledException>();
  }

  [Test]
  public async Task MarkAnswered_IsLeftToTheStoresCommitTimeRecord_AndAsksNothingAsync() {
    var coordinator = new MarkingCoordinator();
    var (ledger, _, sp) = _build(coordinator);
    await using var owned = sp;

    await ledger.MarkAnsweredAsync([_key(TrackedGuid.New().Value)], _now);

    await Assert.That(coordinator.Calls).IsEqualTo(0);
    await Assert.That(coordinator.PendingMarks).IsEmpty();
  }
}
