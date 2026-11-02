// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// A stored event the drain cannot read fails its work rows (#1983), so each row is retried with backoff and
/// dead-lettered once its failures pass <see cref="PerspectiveWorkerOptions.MaxPerspectiveEventAttempts"/>; the
/// readable events fetched with it are applied.
/// </summary>
/// <remarks>
/// Before, a read that threw dropped the whole fetch with an Error line, and a row the store skipped as unreadable
/// simply had no event. Neither recorded anything against the rows: their leases lapsed, they were claimed again, and
/// the same read failed again, forever, holding every other stream fetched with them back each time it threw.
/// </remarks>
public partial class PerspectiveWorkerDeepPathDrainTests {
  private const int UNREADABLE_ROWS_EVENT_ID = 78;

  [Test]
  [Timeout(30000)]
  public async Task DrainMode_ReadThrowsForOneRow_AppliesTheRestAndFailsThatRowAsync(CancellationToken cancellationToken) {
    var streamId = Guid.CreateVersion7();
    var goodEvent = Guid.CreateVersion7();
    var badEvent = Guid.CreateVersion7();
    var goodWork = Guid.CreateVersion7();
    var badWork = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([_raw(streamId, goodEvent, goodWork), _raw(streamId, badEvent, badWork)]);
    var eventStore = new DrainEventStore { ThrowForEventIds = [badEvent] };
    // The read of the good row alone, once the failing read has been split row by row.
    eventStore.EnqueueDeserialized([_envelope(goodEvent, new DrainDeepEvent("good"))]);
    var runner = new DrainRunner();
    var logger = new FakeLogger<PerspectiveWorker>();

    var (worker, harness, _) = _createWorker(coordinator, eventStore, _registry(runner), logger: logger);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await coordinator.FirstCompletion.WaitAsync(cancellationToken);
    await harness.FailureCapture.WaitForCountAsync(1, cancellationToken);
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    await Assert.That(runner.ReceivedBatches.SelectMany(b => b)).IsEquivalentTo([goodEvent])
      .Because("one unreadable row no longer holds back the events fetched with it");
    var (category, failure) = harness.FailureCapture.Items.Single();
    await Assert.That(category).IsEqualTo(WorkCategory.PerspectiveEvent);
    await Assert.That(failure.MessageId).IsEqualTo(badWork)
      .Because("the failure is recorded against the unreadable row, which backs it off and dead-letters it at the bound");
    await Assert.That(failure.Reason).IsEqualTo(MessageFailureReason.SerializationError);
    await Assert.That(failure.Error).Contains("simulated deserialization failure", StringComparison.Ordinal);
    var records = logger.Collector.GetSnapshot();
    var attempt = records.Single(r => r.Id.Id == UNREADABLE_ROWS_EVENT_ID);
    await Assert.That(attempt.Level).IsEqualTo(LogLevel.Warning)
      .Because("the row is retried and dead-lettered at the bound, where the dead-letter record is the Error");
    await Assert.That(attempt.Exception?.Message).IsEqualTo("simulated deserialization failure");
    await Assert.That(records.Where(r => r.Level >= LogLevel.Error)).IsEmpty();
  }

  [Test]
  [Timeout(30000)]
  public async Task DrainMode_ReadThrowsForEveryRow_FailsEveryRowAsync(CancellationToken cancellationToken) {
    var streamId = Guid.CreateVersion7();
    var firstWork = Guid.CreateVersion7();
    var secondWork = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([
      _raw(streamId, Guid.CreateVersion7(), firstWork), _raw(streamId, Guid.CreateVersion7(), secondWork)]);
    var eventStore = new DrainEventStore { ThrowOnDeserialize = true };
    var runner = new DrainRunner();

    var (worker, harness, _) = _createWorker(coordinator, eventStore, _registry(runner));

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await harness.FailureCapture.WaitForCountAsync(2, cancellationToken);
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    await Assert.That(harness.FailureCapture.Items.Select(i => i.failure.MessageId)).IsEquivalentTo([firstWork, secondWork]);
    await Assert.That(eventStore.DeserializeCallCount).IsEqualTo(3)
      .Because("the failing read is tried once for the fetch and then once per row, to find which rows cannot be read");
    await Assert.That(runner.RunWithEventsCallCount).IsEqualTo(0);
  }

  [Test]
  [Timeout(30000)]
  public async Task DrainMode_StoreSkipsARowItCannotRead_FailsThatRowAsync(CancellationToken cancellationToken) {
    var streamId = Guid.CreateVersion7();
    var goodEvent = Guid.CreateVersion7();
    var badWork = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([
      _raw(streamId, goodEvent, Guid.CreateVersion7()), _raw(streamId, Guid.CreateVersion7(), badWork)]);
    var eventStore = new DrainEventStore();
    // A store that skips a row it cannot read, logging it, and returns the rest.
    eventStore.EnqueueDeserialized([_envelope(goodEvent, new DrainDeepEvent("good"))]);
    var runner = new DrainRunner();
    var logger = new FakeLogger<PerspectiveWorker>();

    var (worker, harness, _) = _createWorker(coordinator, eventStore, _registry(runner), logger: logger);

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    await coordinator.FirstCompletion.WaitAsync(cancellationToken);
    await harness.FailureCapture.WaitForCountAsync(1, cancellationToken);
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    var (_, failure) = harness.FailureCapture.Items.Single();
    await Assert.That(failure.MessageId).IsEqualTo(badWork);
    await Assert.That(failure.Reason).IsEqualTo(MessageFailureReason.SerializationError);
    var attempt = logger.Collector.GetSnapshot().Single(r => r.Id.Id == UNREADABLE_ROWS_EVENT_ID);
    await Assert.That(attempt.Level).IsEqualTo(LogLevel.Warning);
  }

  [Test]
  [Timeout(30000)]
  public async Task DrainMode_RowOfATypeThisServiceDoesNotKnow_IsNotFailedAsync(CancellationToken cancellationToken) {
    // A rolling deploy can lease an event of a type only the newer version knows to an older instance. Failing it
    // would count it toward the dead-letter bound on an instance that can never read it; it is left for one that can.
    var streamId = Guid.CreateVersion7();
    var goodEvent = Guid.CreateVersion7();
    var coordinator = new DrainWorkCoordinator();
    coordinator.EnqueueStreamEvents([
      _raw(streamId, goodEvent, Guid.CreateVersion7()),
      _raw(streamId, Guid.CreateVersion7(), Guid.CreateVersion7()) with { EventType = "Contracts.EventFromANewerVersion, Contracts" }]);
    var eventStore = new DrainEventStore();
    eventStore.EnqueueDeserialized([_envelope(goodEvent, new DrainDeepEvent("good"))]);
    var runner = new DrainRunner();

    var (worker, harness, _) = _createWorker(coordinator, eventStore, _registry(runner));

    using var cts = new CancellationTokenSource();
    await worker.StartAsync(cts.Token);
    await harness.EnqueueDrainStreamAsync(streamId, cts.Token);
    // Rows are failed before any event is applied, so a completion means the decision has been made.
    await coordinator.FirstCompletion.WaitAsync(cancellationToken);
    await cts.CancelAsync();
    try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { /* stopping is teardown; its outcome is not what this test asserts */ }

    await Assert.That(harness.FailureCapture.Items).IsEmpty();
  }
}
