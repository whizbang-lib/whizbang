// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// What a rebuild does with a stream's archived data (#1931). By default it never reads the archive: a stream archived
/// through a prefix is rebuilt from its newest live snapshot and the hot events after it, or skipped when it has none,
/// and a stream archived whole is skipped. <see cref="RebuildArchivedData.IncludeWhileLive"/> replays a stream that still
/// has live events from its start, through the archive; <see cref="RebuildArchivedData.IncludeAll"/> replays a stream
/// archived whole as well. A skipped stream is reported, never failed. Each choice is locked here: a test fails if the
/// choice flips.
/// </summary>
/// <docs>fundamentals/perspectives/rebuild#archived-data</docs>
public class PerspectiveRebuilderArchivedDataTests {
  private const string PERSPECTIVE = "TestPerspective";

  [Test]
  public async Task Rebuild_ByDefault_SkipsAStreamArchivedWhole_AndReportsItSkippedNotFailedAsync() {
    var whole = Guid.NewGuid();
    var hot = Guid.NewGuid();
    var runner = new RecordingRunner();

    var result = await _rebuilder(runner, new() { [whole] = ArchivedStreamExtent.Whole })
      .RebuildStreamsAsync(PERSPECTIVE, [whole, hot]);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.StreamsSkipped).IsEqualTo(1);
    await Assert.That(result.StreamsProcessed).IsEqualTo(1);
    await Assert.That(runner.Replayed).IsEquivalentTo([hot])
      .Because("a stream archived whole has nothing a rebuild may read by default, so it is not replayed at all");
    await Assert.That(runner.FromSnapshot).IsEmpty();
  }

  [Test]
  public async Task Rebuild_ByDefault_StartsAStreamArchivedThroughAPrefix_FromItsNewestLiveSnapshotAsync() {
    var prefix = Guid.NewGuid();
    var runner = new RecordingRunner();

    var result = await _rebuilder(runner, new() { [prefix] = ArchivedStreamExtent.Prefix })
      .RebuildStreamsAsync(PERSPECTIVE, [prefix]);

    await Assert.That(result.Success).IsTrue();
    await Assert.That(result.StreamsProcessed).IsEqualTo(1);
    await Assert.That(result.StreamsSkipped).IsEqualTo(0);
    await Assert.That(runner.FromSnapshot).IsEquivalentTo([prefix])
      .Because("the default never reads the archive: the stream starts from its newest live snapshot");
    await Assert.That(runner.Replayed).IsEmpty().Because("a replay from the start would read the archived prefix");
  }

  [Test]
  public async Task Rebuild_ByDefault_SkipsAStreamArchivedThroughAPrefix_ThatHasNoLiveSnapshotAsync() {
    var prefix = Guid.NewGuid();
    var runner = new RecordingRunner { WithoutSnapshot = [prefix] };

    var result = await _rebuilder(runner, new() { [prefix] = ArchivedStreamExtent.Prefix })
      .RebuildStreamsAsync(PERSPECTIVE, [prefix]);

    await Assert.That(result.Success).IsTrue()
      .Because("a stream skipped for its archived data is reported as skipped, not as a stream nothing rebuilt");
    await Assert.That(result.StreamsSkipped).IsEqualTo(1);
    await Assert.That(result.StreamsProcessed).IsEqualTo(0);
    await Assert.That(result.Error).IsNull();
    await Assert.That(runner.Replayed).IsEmpty();
  }

  [Test]
  public async Task Rebuild_IncludeWhileLive_ReplaysAPrefixArchivedStreamFromItsStart_AndStillSkipsOneArchivedWholeAsync() {
    var prefix = Guid.NewGuid();
    var whole = Guid.NewGuid();
    var runner = new RecordingRunner();

    var result = await _rebuilder(runner, new() { [prefix] = ArchivedStreamExtent.Prefix, [whole] = ArchivedStreamExtent.Whole })
      .RebuildStreamsAsync(PERSPECTIVE, [prefix, whole], new RebuildOrigin { ArchivedData = RebuildArchivedData.IncludeWhileLive });

    await Assert.That(runner.Replayed).IsEquivalentTo([prefix])
      .Because("a stream with live events replays from its start, reading its archived prefix through the archive");
    await Assert.That(runner.FromSnapshot).IsEmpty();
    await Assert.That(result.StreamsSkipped).IsEqualTo(1).Because("a stream archived whole is not alive");
    await Assert.That(result.Success).IsTrue();
  }

  [Test]
  public async Task Rebuild_IncludeAll_ReplaysAStreamArchivedWhole_AndOneArchivedThroughAPrefix_FromTheirStartAsync() {
    var prefix = Guid.NewGuid();
    var whole = Guid.NewGuid();
    var runner = new RecordingRunner();

    var result = await _rebuilder(runner, new() { [prefix] = ArchivedStreamExtent.Prefix, [whole] = ArchivedStreamExtent.Whole })
      .RebuildStreamsAsync(PERSPECTIVE, [prefix, whole], new RebuildOrigin { ArchivedData = RebuildArchivedData.IncludeAll });

    await Assert.That(runner.Replayed).IsEquivalentTo([prefix, whole]);
    await Assert.That(result.StreamsSkipped).IsEqualTo(0);
    await Assert.That(result.StreamsProcessed).IsEqualTo(2);
  }

  [Test]
  public async Task Rebuild_WithNoArchiveExtentsRegistered_ReplaysEveryStreamFromItsStartAsync() {
    var stream = Guid.NewGuid();
    var runner = new RecordingRunner();

    var result = await _rebuilder(runner, extents: null).RebuildStreamsAsync(PERSPECTIVE, [stream]);

    await Assert.That(runner.Replayed).IsEquivalentTo([stream])
      .Because("a host with no archive has no archived data to skip");
    await Assert.That(result.StreamsSkipped).IsEqualTo(0);
  }

  [Test]
  public async Task Rebuild_HoldsItsArchivedDataChoice_AmbientForItsReaders_AndRestoresItAfterwardsAsync() {
    var stream = Guid.NewGuid();
    var runner = new RecordingRunner();
    RebuildArchivedDataAccessor.Current = RebuildArchivedData.Skip;

    _ = await _rebuilder(runner, []).RebuildStreamsAsync(
      PERSPECTIVE, [stream], new RebuildOrigin { ArchivedData = RebuildArchivedData.IncludeAll });

    await Assert.That(runner.ChoiceSeen).IsEqualTo(RebuildArchivedData.IncludeAll)
      .Because("the event store's archive seam and the collective replay read the choice while the stream replays");
    await Assert.That(RebuildArchivedDataAccessor.Current).IsEqualTo(RebuildArchivedData.Skip);
  }

  [Test]
  public async Task Rebuild_WithNoOrigin_ReadsNoArchivedDataAsync() {
    var whole = Guid.NewGuid();
    var runner = new RecordingRunner();

    var result = await _rebuilder(runner, new() { [whole] = ArchivedStreamExtent.Whole }).RebuildInPlaceAsync(PERSPECTIVE);

    await Assert.That(result.StreamsSkipped).IsEqualTo(1).Because("the default is Skip");
    await Assert.That(runner.Replayed).IsEmpty();
  }

  [Test]
  public async Task RebuildArchivedData_DefaultsToSkip_OnTheEnumAndOnARebuildRequestAsync() {
    await Assert.That(_unset<RebuildArchivedData>()).IsEqualTo(RebuildArchivedData.Skip).Because("an unset choice is Skip");
    await Assert.That(new RebuildOrigin().ArchivedData).IsEqualTo(RebuildArchivedData.Skip)
      .Because("a rebuild reads no archived data unless it opts in");
    await Assert.That(new Whizbang.Core.Commands.System.RebuildPerspectiveCommand().ArchivedData).IsEqualTo(RebuildArchivedData.Skip);
  }

  // ===== Fixtures =====

  // The value a field of the type holds before anything sets it.
  private static T _unset<T>() where T : struct => default;

  private static PerspectiveRebuilder _rebuilder(RecordingRunner runner, Dictionary<Guid, ArchivedStreamExtent>? extents) {
    var services = new ServiceCollection();
    services.AddSingleton<IPerspectiveRunnerRegistry>(new Registry(runner));
    // The streams a whole-perspective rebuild finds: those the extents name, or one hot stream.
    Guid[] found = extents is { Count: > 0 } ? [.. extents.Keys] : [Guid.NewGuid()];
    services.AddSingleton<IEventStoreQuery>(new StreamsQuery(found));
    if (extents is not null) {
      services.AddSingleton<IArchivedStreamExtents>(new Extents(extents));
    }
    var sp = services.BuildServiceProvider();
    return new PerspectiveRebuilder(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<PerspectiveRebuilder>.Instance);
  }

  private sealed class Extents(Dictionary<Guid, ArchivedStreamExtent> extents) : IArchivedStreamExtents {
    public Task<IReadOnlyDictionary<Guid, ArchivedStreamExtent>> GetExtentsAsync(
        IReadOnlyCollection<Guid> streamIds, CancellationToken cancellationToken = default) =>
      Task.FromResult<IReadOnlyDictionary<Guid, ArchivedStreamExtent>>(
        streamIds.Where(extents.ContainsKey).ToDictionary(id => id, id => extents[id]));
  }

  private sealed class RecordingRunner : IPerspectiveRunner {
    public List<Guid> Replayed { get; } = [];
    public List<Guid> FromSnapshot { get; } = [];
    public HashSet<Guid> WithoutSnapshot { get; init; } = [];
    public RebuildArchivedData? ChoiceSeen { get; private set; }
    public Type PerspectiveType => typeof(object);

    public Task<IReadOnlyList<PerspectiveCursorCompletion>> RunRebuildAsync(
        Guid physicalStreamId, string perspectiveName, CancellationToken cancellationToken = default) {
      Replayed.Add(physicalStreamId);
      ChoiceSeen = RebuildArchivedDataAccessor.Current;
      return Task.FromResult<IReadOnlyList<PerspectiveCursorCompletion>>([_completion(physicalStreamId, perspectiveName)]);
    }

    public Task<IReadOnlyList<PerspectiveCursorCompletion>?> RunRebuildFromLatestSnapshotAsync(
        Guid streamId, string perspectiveName, CancellationToken cancellationToken = default) {
      if (WithoutSnapshot.Contains(streamId)) {
        return Task.FromResult<IReadOnlyList<PerspectiveCursorCompletion>?>(null);
      }
      FromSnapshot.Add(streamId);
      return Task.FromResult<IReadOnlyList<PerspectiveCursorCompletion>?>([_completion(streamId, perspectiveName)]);
    }

    public Task<PerspectiveCursorCompletion> RunAsync(
        Guid streamId, string perspectiveName, Guid? lastProcessedEventId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();

    public Task<PerspectiveCursorCompletion> RewindAndRunAsync(
        Guid streamId, string perspectiveName, Guid triggeringEventId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();

    public Task BootstrapSnapshotAsync(
        Guid streamId, string perspectiveName, Guid lastProcessedEventId, CancellationToken cancellationToken = default) =>
      Task.CompletedTask;

    private static PerspectiveCursorCompletion _completion(Guid streamId, string perspectiveName) => new() {
      StreamId = streamId,
      PerspectiveName = perspectiveName,
      LastEventId = Guid.NewGuid(),
      Status = PerspectiveProcessingStatus.Completed,
      RowChanged = true,
      EventsProcessed = 1,
    };
  }

  private sealed class Registry(IPerspectiveRunner runner) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => runner;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => [];
    public IReadOnlyList<Type> GetEventTypes() => [];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }

  private sealed class StreamsQuery(Guid[] streamIds) : IEventStoreQuery {
    public IQueryable<EventStoreRecord> Query =>
      streamIds.Select(id => new EventStoreRecord {
        Id = Guid.NewGuid(),
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
}
