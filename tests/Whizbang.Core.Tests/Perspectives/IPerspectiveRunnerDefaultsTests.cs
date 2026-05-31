using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

#pragma warning disable CA1707
#pragma warning disable IDE1006

/// <summary>
/// Locks the two default-interface-method behaviors on
/// <see cref="IPerspectiveRunner"/>. Generated runners override these; legacy
/// runners + test fakes get the defaults. If a refactor changes them,
/// every uplift path silently behaves differently.
///
/// Defaults under test:
///   1. RunWithEventsAsync returns Completion(Status=None, LastEventId=lastProcessedEventId)
///      — the no-op runner pattern used by test doubles that don't override drain mode.
///   2. RewindAndRunAsync(streamId, perspectiveName, triggeringEventId,
///      triggeringCommitSequence, ct) delegates to the 3-arg overload —
///      legacy runners that don't know about commit_sequence keep working.
/// </summary>
/// <docs>fundamentals/perspectives/perspective-runner</docs>
public class IPerspectiveRunnerDefaultsTests {

  [Test]
  public async Task RunWithEventsAsync_DefaultImpl_ReturnsNoneStatusAsync() {
    IPerspectiveRunner runner = new _MinimalRunner();
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
    IPerspectiveRunner runner = new _MinimalRunner();

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
    var runner = new _MinimalRunner();
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

  private sealed class _MinimalRunner : IPerspectiveRunner {
    public int RewindCalls { get; private set; }
    public Guid? LastTriggerEventId { get; private set; }

    public Type PerspectiveType => typeof(_MinimalRunner);

    public Task<PerspectiveCursorCompletion> RunAsync(
      Guid streamId,
      string perspectiveName,
      Guid? lastProcessedEventId,
      CancellationToken cancellationToken = default)
      => Task.FromResult(new PerspectiveCursorCompletion {
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
