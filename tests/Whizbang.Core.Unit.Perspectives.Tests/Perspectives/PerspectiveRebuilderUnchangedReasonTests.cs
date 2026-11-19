// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Commands.System;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// A rebuild of named streams whose fold left a row untouched says why (#2252): the fold equaled the stored row (the
/// read model was already right, so the stream is rebuilt and verified), the row's ordering guard refused the write, or
/// no event produced a model. The first is a success; the other two are told apart from each other and from a stream
/// with no events.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveRebuilder.cs</code-under-test>
public class PerspectiveRebuilderUnchangedReasonTests {
  private const string PERSPECTIVE = "Test.Perspective";

  /// <summary>A runner that reports, per stream, a written row, an untouched row with a reason, or no events.</summary>
  private sealed class ReasonRunner(
      IReadOnlyDictionary<Guid, PerspectiveRowUnchangedReason> unchanged, IReadOnlySet<Guid> empty) : IPerspectiveRunner {
    public Type PerspectiveType => typeof(object);

    public Task<PerspectiveCursorCompletion> RunAsync(
        Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken = default) {
      if (empty.Contains(streamId)) {
        return Task.FromResult(new PerspectiveCursorCompletion {
          StreamId = streamId,
          PerspectiveName = perspectiveName,
          LastEventId = Guid.Empty,
          Status = PerspectiveProcessingStatus.None,
          RowChanged = false,
        });
      }
      var hasReason = unchanged.TryGetValue(streamId, out var reason);
      return Task.FromResult(new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = TrackedGuid.New(),
        Status = PerspectiveProcessingStatus.Completed,
        RowChanged = !hasReason,
        UnchangedReason = hasReason ? reason : PerspectiveRowUnchangedReason.Unreported,
        EventsProcessed = 2,
      });
    }

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(
        Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
      RunAsync(streamId, perspectiveName, null, cancellationToken);

    public Task BootstrapSnapshotAsync(
        Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;
  }

  private sealed class Registry(IPerspectiveRunner runner) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => runner;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => [];
    public IReadOnlyList<Type> GetEventTypes() => [];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }

  private static PerspectiveRebuilder _build(
      IReadOnlyDictionary<Guid, PerspectiveRowUnchangedReason> unchanged, params Guid[] empty) {
    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRunnerRegistry>(new Registry(new ReasonRunner(unchanged, empty.ToHashSet())));
    var sp = services.BuildServiceProvider();
    return new PerspectiveRebuilder(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<PerspectiveRebuilder>.Instance);
  }

  [Test]
  public async Task RebuildStreamsAsync_AFoldThatEqualsTheStoredRow_IsRebuiltAndVerified_NotFailedAsync() {
    var stream = TrackedGuid.New();
    var rebuilder = _build(new Dictionary<Guid, PerspectiveRowUnchangedReason> {
      [stream] = PerspectiveRowUnchangedReason.AlreadyCorrect,
    });

    var result = await rebuilder.RebuildStreamsAsync(PERSPECTIVE, [stream]);

    await Assert.That(result.Success).IsTrue().Because($"an identical row is the expected outcome of a correction ({result.Error})");
    await Assert.That(result.StreamsProcessed).IsEqualTo(1);
    await Assert.That(result.StreamsAlreadyCorrect).IsEqualTo(1);
    await Assert.That(result.StreamsWriteRefused).IsEqualTo(0);
  }

  [Test]
  public async Task RebuildStreamOfRunAsync_AFoldThatEqualsTheStoredRow_SucceedsSoTheRunDoesNotRetryItAsync() {
    var stream = TrackedGuid.New();
    var rebuilder = _build(new Dictionary<Guid, PerspectiveRowUnchangedReason> {
      [stream] = PerspectiveRowUnchangedReason.AlreadyCorrect,
    });
    var handler = new RebuildPerspectiveStreamsHandler(rebuilder);
    var command = new RebuildPerspectiveStreamsCommand(TrackedGuid.New(), [stream], PERSPECTIVE, new RebuildOrigin());

    // Throwing would fail the stream's attempt and the run would retry it.
    await handler.HandleStreamAsync(command, stream, CancellationToken.None);

    var result = await rebuilder.RebuildStreamOfRunAsync(PERSPECTIVE, stream, new RebuildOrigin());
    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.StreamsAlreadyCorrect).IsEqualTo(1);
  }

  [Test]
  public async Task RebuildStreamsAsync_AWriteTheOrderingGuardRefused_FailsAndSaysSoAsync() {
    var stream = TrackedGuid.New();
    var rebuilder = _build(new Dictionary<Guid, PerspectiveRowUnchangedReason> {
      [stream] = PerspectiveRowUnchangedReason.WriteRefused,
    });

    var result = await rebuilder.RebuildStreamsAsync(PERSPECTIVE, [stream]);

    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.StreamsWriteRefused).IsEqualTo(1);
    await Assert.That(result.StreamsWithoutModel).IsEqualTo(0);
    await Assert.That(result.Error).Contains("1 folded but the row's ordering guard refused the write");
    await Assert.That(result.Error).Contains("0 folded but no event produced a model");
  }

  [Test]
  public async Task RebuildStreamsAsync_AFoldThatProducedNoModel_FailsAndIsToldApartFromARefusalAsync() {
    var stream = TrackedGuid.New();
    var rebuilder = _build(new Dictionary<Guid, PerspectiveRowUnchangedReason> {
      [stream] = PerspectiveRowUnchangedReason.NoModel,
    });

    var result = await rebuilder.RebuildStreamsAsync(PERSPECTIVE, [stream]);

    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.StreamsWithoutModel).IsEqualTo(1);
    await Assert.That(result.StreamsWriteRefused).IsEqualTo(0);
    await Assert.That(result.Error).Contains("1 folded but no event produced a model");
    await Assert.That(result.Error).Contains("0 folded but the row's ordering guard refused the write");
  }

  [Test]
  public async Task RebuildStreamsAsync_AMixedRun_SeparatesRewrittenAlreadyCorrectRefusedNoModelAndNoEventsAsync() {
    var rewritten = TrackedGuid.New();
    var identical = TrackedGuid.New();
    var refused = TrackedGuid.New();
    var noModel = TrackedGuid.New();
    var noEvents = TrackedGuid.New();
    var rebuilder = _build(new Dictionary<Guid, PerspectiveRowUnchangedReason> {
      [identical] = PerspectiveRowUnchangedReason.AlreadyCorrect,
      [refused] = PerspectiveRowUnchangedReason.WriteRefused,
      [noModel] = PerspectiveRowUnchangedReason.NoModel,
    }, noEvents);

    var result = await rebuilder.RebuildStreamsAsync(PERSPECTIVE, [rewritten, identical, refused, noModel, noEvents]);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.StreamsRequested).IsEqualTo(5);
    await Assert.That(result.StreamsProcessed).IsEqualTo(2).Because("the rewritten row and the verified one");
    await Assert.That(result.StreamsAlreadyCorrect).IsEqualTo(1);
    await Assert.That(result.StreamsWriteRefused).IsEqualTo(1);
    await Assert.That(result.StreamsWithoutModel).IsEqualTo(1);
    await Assert.That(result.StreamsWithoutEvents).IsEqualTo(1);
  }

  [Test]
  public async Task RebuildStreamsAsync_AnUnreportedReason_StillCountsAsFoldedButUnchangedAsync() {
    // A runner that predates the reason (a hand-written one) still reads as it always did.
    var stream = TrackedGuid.New();
    var rebuilder = _build(new Dictionary<Guid, PerspectiveRowUnchangedReason> {
      [stream] = PerspectiveRowUnchangedReason.Unreported,
    });

    var result = await rebuilder.RebuildStreamsAsync(PERSPECTIVE, [stream]);

    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.Error).Contains("1 folded but left the row unchanged");
    await Assert.That(result.StreamsWriteRefused).IsEqualTo(0);
    await Assert.That(result.StreamsWithoutModel).IsEqualTo(0);
  }
}
