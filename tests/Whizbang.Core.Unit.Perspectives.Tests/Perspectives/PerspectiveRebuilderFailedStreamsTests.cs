// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Events.System;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// A rebuild in which some streams failed to replay says so (#2376). A stream's replay that throws is logged and the
/// rebuild carries on with the rest, as before; the failure is now counted in <see cref="RebuildResult.StreamsFailed"/>,
/// and a rebuild with any failed stream reports <c>Success = false</c> with an error naming how many failed (and the first
/// few ids), emitting <see cref="PerspectiveRebuildFailed"/> rather than <see cref="PerspectiveRebuildCompleted"/>. The
/// streams that did rebuild are kept.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/PerspectiveRebuilder.cs</code-under-test>
public class PerspectiveRebuilderFailedStreamsTests {
  private const string PERSPECTIVE = "Test.Perspective";

  /// <summary>A runner that writes every stream's row, except the streams it is told to fail, whose replay throws.</summary>
  private sealed class FailingRunner(IReadOnlySet<Guid> failing) : IPerspectiveRunner {
    public List<Guid> Rebuilt { get; } = [];
    public Type PerspectiveType => typeof(object);

    public Task<PerspectiveCursorCompletion> RunAsync(
        Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken = default) {
      if (failing.Contains(streamId)) {
        throw new InvalidOperationException($"the replay of {streamId} failed");
      }
      lock (Rebuilt) {
        Rebuilt.Add(streamId);
      }
      return Task.FromResult(new PerspectiveCursorCompletion {
        StreamId = streamId,
        PerspectiveName = perspectiveName,
        LastEventId = TrackedGuid.New(),
        Status = PerspectiveProcessingStatus.Completed,
        RowChanged = true,
        EventsProcessed = 1,
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

  /// <summary>The event store a whole-perspective rebuild lists its streams from: one event per stream.</summary>
  private sealed class StreamsQuery(IReadOnlyList<Guid> streamIds) : IEventStoreQuery {
    public IQueryable<EventStoreRecord> Query =>
      streamIds.Select(id => new EventStoreRecord {
        Id = TrackedGuid.New(),
        StreamId = id,
        AggregateId = id,
        AggregateType = "Test",
        Version = 1,
        EventType = "TestEvent",
        EventData = JsonElement.Parse("{}"),
        Metadata = new EnvelopeMetadata { MessageId = MessageId.New(), Hops = [] },
        CreatedAt = DateTime.UtcNow,
      }).AsQueryable();

    public IQueryable<EventStoreRecord> GetStreamEvents(Guid streamId) => Query.Where(e => e.StreamId == streamId);

    public IQueryable<EventStoreRecord> GetEventsByType(string eventType) => Query.Where(e => e.EventType == eventType);
  }

  private sealed record Built(
      PerspectiveRebuilder Rebuilder, FailingRunner Runner, RebuildEventDispatcherDouble Dispatcher, FakeLogger<PerspectiveRebuilder> Logger);

  private static Built _build(IReadOnlyList<Guid>? allStreams, params Guid[] failing) {
    var runner = new FailingRunner(failing.ToHashSet());
    var dispatcher = new RebuildEventDispatcherDouble();
    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRunnerRegistry>(new Registry(runner));
    services.AddSingleton<IDispatcher>(dispatcher);
    if (allStreams is not null) {
      services.AddSingleton<IEventStoreQuery>(new StreamsQuery(allStreams));
    }
    var logger = new FakeLogger<PerspectiveRebuilder>();
    var sp = services.BuildServiceProvider();
    return new Built(new PerspectiveRebuilder(sp.GetRequiredService<IServiceScopeFactory>(), logger), runner, dispatcher, logger);
  }

  [Test]
  public async Task RebuildStreamsAsync_OneOfThreeStreamsFails_IsNotASuccess_AndKeepsTheOtherTwoAsync() {
    Guid good1 = TrackedGuid.New(), bad = TrackedGuid.New(), good2 = TrackedGuid.New();
    var built = _build(null, bad);

    var result = await built.Rebuilder.RebuildStreamsAsync(PERSPECTIVE, [good1, bad, good2]);

    await Assert.That(result.Success).IsFalse().Because("a rebuild that left a stream unreplayed is not a clean success");
    await Assert.That(result.StreamsProcessed).IsEqualTo(2);
    await Assert.That(result.StreamsFailed).IsEqualTo(1);
    await Assert.That(result.StreamsRequested).IsEqualTo(3);
    await Assert.That(result.Error).Contains("1 of 3 stream(s) failed to replay");
    await Assert.That(result.Error).Contains(bad.ToString());
    await Assert.That(built.Runner.Rebuilt).IsEquivalentTo([good1, good2])
      .Because("the streams that did rebuild are kept, as they always were");
    var failed = built.Dispatcher.Published.OfType<PerspectiveRebuildFailed>().Single();
    await Assert.That(failed.StreamsProcessedBeforeFailure).IsEqualTo(2);
    await Assert.That(failed.Error).IsEqualTo(result.Error);
    await Assert.That(built.Dispatcher.Published.OfType<PerspectiveRebuildCompleted>()).IsEmpty();
  }

  [Test]
  public async Task RebuildInPlaceAsync_OneOfThreeStreamsFails_IsNotASuccess_AndKeepsTheOtherTwoAsync() {
    Guid good1 = TrackedGuid.New(), bad = TrackedGuid.New(), good2 = TrackedGuid.New();
    var built = _build([good1, bad, good2], bad);

    var result = await built.Rebuilder.RebuildInPlaceAsync(PERSPECTIVE);

    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.StreamsProcessed).IsEqualTo(2);
    await Assert.That(result.StreamsFailed).IsEqualTo(1);
    await Assert.That(result.Error).Contains("1 of 3 stream(s) failed to replay");
    await Assert.That(built.Runner.Rebuilt).IsEquivalentTo([good1, good2]);
    await Assert.That(built.Dispatcher.Published.OfType<PerspectiveRebuildFailed>()).HasSingleItem();
    await Assert.That(built.Dispatcher.Published.OfType<PerspectiveRebuildCompleted>()).IsEmpty();
    var warning = built.Logger.Collector.GetSnapshot()
      .Single(r => r.Level == LogLevel.Warning && r.Message.Contains("stream(s) failed to replay", StringComparison.Ordinal));
    await Assert.That(warning.Message).Contains("2 rewritten").Because("the line carries the outcome counts beside the failures");
  }

  [Test]
  public async Task RebuildStreamsAsync_EveryStreamSucceeds_ReportsNoFailure_AndCompletesAsync() {
    var built = _build(null);

    var result = await built.Rebuilder.RebuildStreamsAsync(PERSPECTIVE, [TrackedGuid.New(), TrackedGuid.New()]);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.Error).IsNull();
    await Assert.That(result.StreamsFailed).IsEqualTo(0);
    await Assert.That(built.Dispatcher.Published.OfType<PerspectiveRebuildCompleted>()).HasSingleItem();
    await Assert.That(built.Dispatcher.Published.OfType<PerspectiveRebuildFailed>()).IsEmpty();
    var completed = built.Logger.Collector.GetSnapshot().Single(r => r.Message.StartsWith("Completed", StringComparison.Ordinal));
    await Assert.That(completed.Message).Contains("0 failed").Because("the completed line counts the failed streams too");
  }

  [Test]
  public async Task RebuildStreamsAsync_ManyFailedStreams_NamesTheFirstFewAndCountsTheRestAsync() {
    var failing = Enumerable.Range(0, 7).Select(_ => TrackedGuid.New().Value).ToArray();
    var good = TrackedGuid.New();
    var built = _build(null, failing);

    var result = await built.Rebuilder.RebuildStreamsAsync(PERSPECTIVE, [good, .. failing]);

    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.StreamsFailed).IsEqualTo(7);
    await Assert.That(result.Error).Contains("7 of 8 stream(s) failed to replay");
    foreach (var named in failing.Take(5)) {
      await Assert.That(result.Error).Contains(named.ToString());
    }
    foreach (var unnamed in failing.Skip(5)) {
      await Assert.That(result.Error).DoesNotContain(unnamed.ToString());
    }
    await Assert.That(result.Error).Contains("and 2 more");
  }

  [Test]
  public async Task RebuildStreamsAsync_EveryNamedStreamFails_SaysTheyFailedRatherThanThatNothingWasThereAsync() {
    var bad = TrackedGuid.New();
    var built = _build(null, bad);

    var result = await built.Rebuilder.RebuildStreamsAsync(PERSPECTIVE, [bad]);

    await Assert.That(result.Success).IsFalse();
    await Assert.That(result.StreamsProcessed).IsEqualTo(0);
    await Assert.That(result.StreamsFailed).IsEqualTo(1);
    await Assert.That(result.Error).Contains("1 failed to replay");
    await Assert.That(built.Dispatcher.Published.OfType<PerspectiveRebuildFailed>()).HasSingleItem();
  }
}
