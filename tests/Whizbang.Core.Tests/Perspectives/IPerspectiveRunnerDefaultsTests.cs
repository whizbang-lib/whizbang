// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

#pragma warning disable CA1707, IDE1006

/// <summary>
/// <para>
/// Locks the two default-interface-method behaviors on
/// <see cref="IPerspectiveRunner"/>. Generated runners override these; legacy
/// runners + test fakes get the defaults. If a refactor changes them,
/// every uplift path silently behaves differently.
/// </para>
/// <para>
/// Defaults under test:
///   1. RunWithEventsAsync returns Completion(Status=None, LastEventId=lastProcessedEventId)
///      — the no-op runner pattern used by test doubles that don't override drain mode.
///   2. RewindAndRunAsync(streamId, perspectiveName, triggeringEventId,
///      triggeringCommitSequence, ct) delegates to the 3-arg overload —
///      legacy runners that don't know about commit_sequence keep working.
/// </para>
/// </summary>
/// <docs>fundamentals/perspectives/perspective-runner</docs>
public class IPerspectiveRunnerDefaultsTests {

  [Test]
  public async Task RunWithEventsAsync_DefaultImpl_ReturnsNoneStatusAsync() {
    IPerspectiveRunner runner = new MinimalRunner();
    var streamId = Guid.NewGuid();
    var lastEventId = Guid.NewGuid();

    var result = await runner.RunWithEventsAsync(
      streamId,
      "MyPerspective",
      lastEventId,
      events: [],
      CancellationToken.None);

    await Assert.That(result.StreamId).IsEqualTo(streamId);
    await Assert.That(result.PerspectiveName).IsEqualTo("MyPerspective");
    await Assert.That(result.LastEventId).IsEqualTo(lastEventId);
    await Assert.That(result.Status).IsEqualTo(PerspectiveProcessingStatus.None);
  }

  [Test]
  public async Task RunWithEventsAsync_NullLastProcessedEventId_LandsOnGuidEmptyAsync() {
    IPerspectiveRunner runner = new MinimalRunner();

    var result = await runner.RunWithEventsAsync(
      Guid.NewGuid(),
      "P",
      lastProcessedEventId: null,
      events: [],
      CancellationToken.None);

    // The ?? Guid.Empty fallback in the default impl — locks against a future
    // accidental change that drops the null-coalesce and propagates null.
    await Assert.That(result.LastEventId).IsEqualTo(Guid.Empty);
  }

  [Test]
  public async Task CommitSequenceRewind_DelegatesToLegacyRewindAsync() {
    var runner = new MinimalRunner();
    var streamId = Guid.NewGuid();
    var triggerId = Guid.NewGuid();

    await ((IPerspectiveRunner)runner).RewindAndRunAsync(
      streamId,
      "MyPerspective",
      triggerId,
      triggeringCommitSequence: 12345L,  // gets dropped by the default
      CancellationToken.None);

    await Assert.That(runner.RewindCalls).IsEqualTo(1);
    await Assert.That(runner.LastTriggerEventId).IsEqualTo(triggerId);
    // The triggeringCommitSequence value is intentionally not forwarded to the
    // 3-arg overload — that's the whole point of the legacy fallback contract.
  }

  [Test]
  public async Task PrepareAsync_Default_HandsThePairToThePerPairPathAsync() {
    IPerspectiveRunner runner = new MinimalRunner();
    var streamId = Guid.NewGuid();
    var lastEventId = Guid.NewGuid();
    IReadOnlyList<MessageEnvelope<IEvent>> events = [];

    var prepared = await runner.PrepareAsync(streamId, "MyPerspective", lastEventId, events, CancellationToken.None);

    await Assert.That(prepared.RunsPerPair).IsTrue()
      .Because("a hand-written runner prepares no write, so the set-based path must hand it back whole");
    await Assert.That(prepared.PerPairReason).IsEqualTo(PerspectivePerPairReason.RunnerDoesNotPrepare);
    await Assert.That(prepared.Write).IsNull();
    await Assert.That(prepared.StreamId).IsEqualTo(streamId);
    await Assert.That(prepared.PerspectiveName).IsEqualTo("MyPerspective");
    await Assert.That(prepared.LastProcessedEventId).IsEqualTo(lastEventId);
    await Assert.That(prepared.Events).IsSameReferenceAs(events);
  }

  [Test]
  public async Task PrepareAsync_WithABatchState_Default_HandsThePairToThePerPairPathAsync() {
    IPerspectiveRunner runner = new MinimalRunner();
    var batch = new BatchedPerspectiveStateSource().BeginBatch([]);

    var prepared = await runner.PrepareAsync(Guid.NewGuid(), "P", null, [], batch, CancellationToken.None);

    await Assert.That(prepared.PerPairReason).IsEqualTo(PerspectivePerPairReason.RunnerDoesNotPrepare)
      .Because("a hand-written runner reads nothing from a batch, so its pair takes the per-pair path whole");
  }

  [Test]
  public async Task ReadEventsAsync_Default_ReturnsNullAsync() {
    // A hand-written runner does not expose its read: the batch runs its claimed pair with RunAsync, per pair.
    IPerspectiveRunner runner = new MinimalRunner();

    await Assert.That(await runner.ReadEventsAsync(Guid.NewGuid(), null)).IsNull();
  }

  [Test]
  public async Task CompleteAsync_Default_RunsAPerPairPairThroughRunWithEventsAsync() {
    var runner = new RecordingRunner();
    var streamId = Guid.NewGuid();
    var lastEventId = Guid.NewGuid();
    IReadOnlyList<MessageEnvelope<IEvent>> events = [];
    var prepared = await ((IPerspectiveRunner)runner).PrepareAsync(streamId, "P", lastEventId, events);

    var completion = await ((IPerspectiveRunner)runner).CompleteAsync(prepared, outcome: null);

    await Assert.That(runner.Runs).IsEqualTo(1);
    await Assert.That(runner.LastRun).IsEqualTo((streamId, "P", (Guid?)lastEventId, events));
    await Assert.That(completion).IsSameReferenceAs(runner.Completion);
  }

  [Test]
  public async Task CompleteAsync_Default_RefusesAPairThatCarriesAWriteAsync() {
    IPerspectiveRunner runner = new RecordingRunner();
    var prepared = new WritingPair(PendingPerspectiveWrite.Purge("\"wh_per_x\"", Guid.NewGuid()));

    await Assert.That(async () => await runner.CompleteAsync(prepared, PerspectiveWriteOutcome.Written))
      .Throws<ArgumentException>()
      .Because("a runner that prepares no write cannot complete one; writing it per-pair would write it twice");
  }

  [Test]
  public async Task CompleteAsync_Default_NullPrepared_ThrowsAsync() {
    IPerspectiveRunner runner = new RecordingRunner();

    await Assert.That(async () => await runner.CompleteAsync(null!, outcome: null)).Throws<ArgumentNullException>();
  }

  private sealed class WritingPair(PendingPerspectiveWrite write)
    : PreparedPerspectiveApply(Guid.NewGuid(), "P", null, [], write, PerspectivePerPairReason.None);

  private sealed class RecordingRunner : IPerspectiveRunner {
    public int Runs { get; private set; }
    public (Guid StreamId, string PerspectiveName, Guid? LastProcessedEventId, IReadOnlyList<MessageEnvelope<IEvent>> Events) LastRun { get; private set; }
    public PerspectiveCursorCompletion Completion { get; } = new() {
      StreamId = Guid.NewGuid(),
      PerspectiveName = "P",
      LastEventId = Guid.NewGuid(),
      Status = PerspectiveProcessingStatus.Completed,
      RowChanged = true,
    };

    public Type PerspectiveType => typeof(RecordingRunner);

    public Task<PerspectiveCursorCompletion> RunAsync(
      Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken = default)
      => throw new NotSupportedException();

    public Task<PerspectiveCursorCompletion> RunWithEventsAsync(
      Guid streamId,
      string perspectiveName,
      Guid? lastProcessedEventId,
      IReadOnlyList<MessageEnvelope<IEvent>> events,
      CancellationToken cancellationToken = default) {
      Runs++;
      LastRun = (streamId, perspectiveName, lastProcessedEventId, events);
      return Task.FromResult(Completion);
    }

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(
      Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default)
      => throw new NotSupportedException();

    public Task BootstrapSnapshotAsync(
      Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default)
      => throw new NotSupportedException();
  }

  private sealed class MinimalRunner : IPerspectiveRunner {
    public int RewindCalls { get; private set; }
    public Guid? LastTriggerEventId { get; private set; }

    public Type PerspectiveType => typeof(MinimalRunner);

    public Task<PerspectiveCursorCompletion> RunAsync(
      Guid streamId,
      string perspectiveName,
      Guid? lastProcessedEventId,
      CancellationToken cancellationToken = default)
      => Task.FromResult(new PerspectiveCursorCompletion {
        RowChanged = true,
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = lastProcessedEventId ?? Guid.Empty,
        Status = PerspectiveProcessingStatus.Completed,
      });

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(
      Guid streamId,
      string perspectiveName,
      Guid triggeringEventId,
      CancellationToken cancellationToken = default) {
      RewindCalls++;
      LastTriggerEventId = triggeringEventId;
      return Task.FromResult(new PerspectiveCursorCompletion {
        RowChanged = true,
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = triggeringEventId,
        Status = PerspectiveProcessingStatus.Completed,
      });
    }

    public Task BootstrapSnapshotAsync(
      Guid streamId,
      string perspectiveName,
      Guid lastProcessedEventId,
      CancellationToken cancellationToken = default)
      => Task.CompletedTask;
  }
}
