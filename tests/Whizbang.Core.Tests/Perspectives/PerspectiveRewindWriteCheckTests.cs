// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>An attempt at an item started; every one is counted.</summary>
public sealed record RewindCheckStarted : IEvent {
  [StreamId]
  public Guid StreamId { get; init; }
}

/// <summary>The item completed.</summary>
public sealed record RewindCheckCompleted : IEvent {
  [StreamId]
  public Guid StreamId { get; init; }
}

public sealed record RewindCheckModel {
  [StreamId]
  public Guid Id { get; init; }
  public int Attempts { get; init; }
  public bool Completed { get; init; }
}

/// <summary>A whole-row fold, so a refused write loses whatever the writer folded.</summary>
public sealed class RewindCheckPerspective :
    IPerspectiveFor<RewindCheckModel, RewindCheckStarted>,
    IPerspectiveFor<RewindCheckModel, RewindCheckCompleted> {
  public RewindCheckModel Apply(RewindCheckModel currentData, RewindCheckStarted @event) =>
    currentData with { Id = @event.StreamId, Attempts = currentData.Attempts + 1 };

  public RewindCheckModel Apply(RewindCheckModel currentData, RewindCheckCompleted @event) =>
    currentData with { Id = @event.StreamId, Completed = true };
}

/// <summary>
/// A rewind's write is checked against the row version the rewind read, as a live apply's is (#1863).
/// </summary>
/// <remarks>
/// A rewind replays the stream in memory and writes the row once at the end. It used to write without a version,
/// so the only check on it was the store's ordering guard: when another writer folded a newer event into the row
/// while the rewind replayed, the rewind's frontier sorted below the row's stamp, the guard refused the write, and
/// the rewind reported itself complete with its correction lost and nothing logged. The rewind now reads the row's
/// version before it reads any event, writes against it, and replays again onto the moved row when it is refused.
/// A refusal on a row that did not move is logged.
/// </remarks>
/// <docs>fundamentals/perspectives/perspectives#concurrent-writers</docs>
public class PerspectiveRewindWriteCheckTests {
  private const string PERSPECTIVE_NAME = nameof(RewindCheckPerspective);

  [Test]
  public async Task ARewind_WritesAgainstTheRowVersionItReadAsync() {
    var harness = new Harness();
    var streamId = TrackedGuid.New().Value;
    var started = await harness.AppendAsync(streamId, new RewindCheckStarted { StreamId = streamId });
    harness.Rows.Seed(streamId, new RewindCheckModel { Id = streamId }, started.MessageId.Value);
    var versionBefore = harness.Rows.VersionOf(streamId);

    var result = await harness.Runner.RewindAndRunAsync(streamId, PERSPECTIVE_NAME, started.MessageId.Value, CancellationToken.None);

    await Assert.That(harness.Rows.LastExpected).IsEqualTo(PerspectiveRowVersion.Of(versionBefore))
      .Because("a rewind's write must land only on the row it read, exactly as a live apply's does");
    await Assert.That(result.RowChanged).IsTrue();
    await Assert.That(harness.Rows.Row(streamId).Attempts).IsEqualTo(1);
  }

  [Test]
  public async Task ARowThatMovesDuringTheRewind_IsReplayedAgain_AndHoldsTheCorrectionAndTheNewerEventAsync() {
    var harness = new Harness();
    var streamId = TrackedGuid.New().Value;
    var firstStart = await harness.AppendAsync(streamId, new RewindCheckStarted { StreamId = streamId });
    var completed = await harness.AppendAsync(streamId, new RewindCheckCompleted { StreamId = streamId });
    // The row as a lost update left it: Completed folded, the first Started never was. The rewind is its correction.
    harness.Rows.Seed(streamId, new RewindCheckModel { Id = streamId, Completed = true }, completed.MessageId.Value);
    // While the rewind replays, a second attempt starts and another runner folds it into the row and commits.
    harness.Rows.BeforeFirstWrite = async () => {
      var secondStart = await harness.AppendAsync(streamId, new RewindCheckStarted { StreamId = streamId });
      await harness.NewRunner().RunWithEventsAsync(streamId, PERSPECTIVE_NAME, completed.MessageId.Value, [secondStart], CancellationToken.None);
    };

    var result = await harness.Runner.RewindAndRunAsync(streamId, PERSPECTIVE_NAME, firstStart.MessageId.Value, CancellationToken.None);

    var row = harness.Rows.Row(streamId);
    await Assert.That(row.Attempts).IsEqualTo(2)
      .Because("the row must hold the rewind's correction (the first attempt) and the newer event (the second)");
    await Assert.That(row.Completed).IsTrue();
    await Assert.That(result.RowChanged).IsTrue()
      .Because("the rewind's write landed, on its second replay");
    await Assert.That(harness.Rows.ReadsForApply).IsEqualTo(3)
      .Because("the rewind read the version twice, once per replay, and the other runner once");
  }

  [Test]
  public async Task ARowThatMovesUnderEveryRewindReplay_GivesUpLoudly_AndNeverWritesStaleAsync() {
    var harness = new Harness();
    var streamId = TrackedGuid.New().Value;
    var started = await harness.AppendAsync(streamId, new RewindCheckStarted { StreamId = streamId });
    harness.Rows.Seed(streamId, new RewindCheckModel { Id = streamId, Attempts = 7 }, started.MessageId.Value);
    // Another writer touches the row (moves its version, not its stamp) before every write the rewind attempts.
    harness.Rows.BeforeEveryWrite = () => harness.Rows.Touch(streamId);

    var thrown = await Assert.That(async () => await harness.Runner.RewindAndRunAsync(
        streamId, PERSPECTIVE_NAME, started.MessageId.Value, CancellationToken.None))
      .Throws<PerspectiveRowConflictException>();

    await Assert.That(thrown!.StreamId).IsEqualTo(streamId);
    await Assert.That(harness.Rows.WriteAttempts).IsEqualTo(5)
      .Because("the replay is retried a bounded number of times, then the rewind fails through the worker's failure path");
    await Assert.That(harness.Rows.Row(streamId).Attempts).IsEqualTo(7)
      .Because("no write computed from a stale read ever landed");
  }

  [Test]
  public async Task ARewindFromASnapshot_ReplaysAgainFromTheSameSnapshot_WhenTheRowMovesAsync() {
    var streamId = TrackedGuid.New().Value;
    var snapshotEvent = TrackedGuid.New().Value;
    var snapshots = new OneSnapshotStore(snapshotEvent, _snapshotOf(new RewindCheckModel { Id = streamId, Attempts = 1 }));
    var harness = new Harness(snapshots);
    var completed = await harness.AppendAsync(streamId, new RewindCheckCompleted { StreamId = streamId });
    harness.Rows.Seed(streamId, new RewindCheckModel { Id = streamId, Attempts = 1 }, snapshotEvent);
    harness.Rows.BeforeFirstWrite = async () => {
      var secondStart = await harness.AppendAsync(streamId, new RewindCheckStarted { StreamId = streamId });
      await harness.NewRunner().RunWithEventsAsync(streamId, PERSPECTIVE_NAME, snapshotEvent, [secondStart], CancellationToken.None);
    };

    await harness.Runner.RewindAndRunAsync(streamId, PERSPECTIVE_NAME, completed.MessageId.Value, CancellationToken.None);

    var row = harness.Rows.Row(streamId);
    await Assert.That(row.Attempts).IsEqualTo(2)
      .Because("the second replay restores the snapshot again (one attempt) and folds the newer Started onto it");
    await Assert.That(row.Completed).IsTrue();
    await Assert.That(snapshots.Reads).IsEqualTo(1)
      .Because("the restore point is found once per rewind; a replay that is refused restarts from the same one");
  }

  [Test]
  public async Task ARewindWriteTheOrderingGuardRefusesOnAnUnmovedRow_IsLoggedAsAWarningAsync() {
    var logs = new List<(LogLevel Level, string Message)>();
    var harness = new Harness(logs: logs);
    var streamId = TrackedGuid.New().Value;
    var started = await harness.AppendAsync(streamId, new RewindCheckStarted { StreamId = streamId });
    // The row is stamped past every event the store holds, so the rewind's frontier sorts below it.
    harness.Rows.Seed(streamId, new RewindCheckModel { Id = streamId, Attempts = 9 }, TrackedGuid.New().Value);

    var result = await harness.Runner.RewindAndRunAsync(streamId, PERSPECTIVE_NAME, started.MessageId.Value, CancellationToken.None);

    await Assert.That(result.RowChanged).IsFalse();
    await Assert.That(harness.Rows.Row(streamId).Attempts).IsEqualTo(9);
    var refusal = logs.Where(l => l.Level == LogLevel.Warning && l.Message.Contains("[diag.rewind.write.refused]")).ToList();
    await Assert.That(refusal).Count().IsEqualTo(1)
      .Because("a rewind whose correction was not written is reported, never counted as applied in silence");
    await Assert.That(refusal[0].Message).Contains(streamId.ToString());
    await Assert.That(refusal[0].Message).Contains(PERSPECTIVE_NAME);
  }

  // ===== A rewind from a snapshot an archive covers reads through the archive (#1738) =====

  /// <summary>
  /// A snapshot whose stream was archived past it records how far the archive reaches. A rewind from it reads the
  /// archived events above the snapshot's version, then the hot events after it, so the archived detail between the
  /// snapshot and the cut is folded, not skipped (#1738).
  /// </summary>
  /// <docs>fundamentals/events/archive-store#rewind-past-an-archived-snapshot</docs>
  [Test]
  public async Task ARewindFromASnapshotTheArchiveCovers_ReadsThroughTheArchive_AboveTheSnapshotsVersionAsync() {
    var streamId = TrackedGuid.New().Value;
    var snapshotEvent = TrackedGuid.New().Value;
    var snapshots = new FloorSnapshotStore(snapshotEvent, 2, archivedThroughVersion: 4, _snapshotOf(new RewindCheckModel { Id = streamId, Attempts = 1 }));
    ArchiveReadingStore? store = null;
    var harness = new Harness(snapshots, wrap: inner => store = new ArchiveReadingStore(inner,
      [new RewindCheckStarted { StreamId = streamId }, new RewindCheckStarted { StreamId = streamId }], firstVersion: 3));
    var completed = await harness.AppendAsync(streamId, new RewindCheckCompleted { StreamId = streamId });
    harness.Rows.Seed(streamId, new RewindCheckModel { Id = streamId, Attempts = 1 }, snapshotEvent);

    await harness.Runner.RewindAndRunAsync(streamId, PERSPECTIVE_NAME, completed.MessageId.Value, CancellationToken.None);

    await Assert.That(store!.ThroughArchiveReads).IsEquivalentTo([((Guid?)snapshotEvent, 2L)])
      .Because("the rewind asks for the archived events above the snapshot's version, then the hot ones after its event");
    var row = harness.Rows.Row(streamId);
    await Assert.That(row.Attempts).IsEqualTo(3).Because("the snapshot's one attempt and the two archived Started events");
    await Assert.That(row.Completed).IsTrue();
  }

  /// <summary>The control: a snapshot nothing archived reads only the hot events after it.</summary>
  [Test]
  public async Task ARewindFromASnapshotNoArchiveCovers_ReadsOnlyTheHotEventsAfterItAsync() {
    var streamId = TrackedGuid.New().Value;
    var snapshotEvent = TrackedGuid.New().Value;
    var snapshots = new FloorSnapshotStore(snapshotEvent, 2, archivedThroughVersion: null, _snapshotOf(new RewindCheckModel { Id = streamId, Attempts = 1 }));
    ArchiveReadingStore? store = null;
    var harness = new Harness(snapshots, wrap: inner => store = new ArchiveReadingStore(inner,
      [new RewindCheckStarted { StreamId = streamId }], firstVersion: 3));
    var completed = await harness.AppendAsync(streamId, new RewindCheckCompleted { StreamId = streamId });
    harness.Rows.Seed(streamId, new RewindCheckModel { Id = streamId, Attempts = 1 }, snapshotEvent);

    await harness.Runner.RewindAndRunAsync(streamId, PERSPECTIVE_NAME, completed.MessageId.Value, CancellationToken.None);

    await Assert.That(store!.ThroughArchiveReads).IsEmpty().Because("a rewind to an unmarked snapshot never looks at the archive");
    await Assert.That(harness.Rows.Row(streamId).Attempts).IsEqualTo(1);
  }

  /// <summary>
  /// A marked snapshot that records no version cannot say where the archive's events after it begin, so it is not used:
  /// the rewind replays the stream from its start, which reads through the archive by its gap (#1738).
  /// </summary>
  [Test]
  public async Task AMarkedSnapshotWithNoVersion_IsNotUsed_TheRewindReplaysFromTheStartAsync() {
    var streamId = TrackedGuid.New().Value;
    var snapshotEvent = TrackedGuid.New().Value;
    var snapshots = new FloorSnapshotStore(snapshotEvent, null, archivedThroughVersion: 4, _snapshotOf(new RewindCheckModel { Id = streamId, Attempts = 7 }));
    ArchiveReadingStore? store = null;
    var harness = new Harness(snapshots, wrap: inner => store = new ArchiveReadingStore(inner, [], firstVersion: 3));
    var started = await harness.AppendAsync(streamId, new RewindCheckStarted { StreamId = streamId });
    harness.Rows.Seed(streamId, new RewindCheckModel { Id = streamId, Attempts = 7 }, snapshotEvent);

    await harness.Runner.RewindAndRunAsync(streamId, PERSPECTIVE_NAME, started.MessageId.Value, CancellationToken.None);

    await Assert.That(store!.ThroughArchiveReads).IsEmpty();
    await Assert.That(store.FromStartReads).IsGreaterThanOrEqualTo(1).Because("the replay starts from the stream's start");
    await Assert.That(harness.Rows.Row(streamId).Attempts).IsEqualTo(1)
      .Because("the snapshot's model is not the starting point: the stream's one Started is folded from an empty model");
  }

  /// <summary>An event store that serves a rewind's read through the archive from a list, and counts the reads.</summary>
  private sealed class ArchiveReadingStore(InMemoryEventStore inner, IEvent[] archived, int firstVersion)
      : ForwardingEventStoreDecorator(inner) {
    public List<(Guid?, long)> ThroughArchiveReads { get; } = [];
    public int FromStartReads { get; private set; }

    public override IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(
        Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) {
      if (fromEventId is null) {
        FromStartReads++;
      }
      return base.ReadPolymorphicAsync(streamId, fromEventId, eventTypes, cancellationToken);
    }

    public override async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicThroughArchiveAsync(
        Guid streamId, Guid? fromEventId, long afterVersion, IReadOnlyList<Type> eventTypes,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
      ThroughArchiveReads.Add((fromEventId, afterVersion));
      var version = firstVersion;
      foreach (var payload in archived) {
        yield return new MessageEnvelope<IEvent> {
          MessageId = MessageId.New(),
          Payload = payload,
          Hops = [new MessageHop { Type = HopType.Current, ServiceInstance = ServiceInstanceInfo.Unknown, Timestamp = DateTimeOffset.UnixEpoch }],
          DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
          LocalStreamVersion = version++,
        };
      }
      await foreach (var envelope in Inner.ReadPolymorphicAsync(streamId, fromEventId, eventTypes, cancellationToken)) {
        yield return envelope;
      }
    }
  }

  /// <summary>Holds one snapshot floor, with or without an archive mark.</summary>
  private sealed class FloorSnapshotStore(Guid snapshotEventId, int? version, long? archivedThroughVersion, JsonDocument data)
      : IPerspectiveSnapshotStore {
    public Task CreateSnapshotAsync(Guid streamId, string perspectiveName, Guid snapshotEventId, JsonDocument snapshotData, CancellationToken ct = default) =>
      Task.CompletedTask;

    public Task<(Guid SnapshotEventId, JsonDocument SnapshotData)?> GetLatestSnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult<(Guid, JsonDocument)?>((snapshotEventId, data));

    public Task<(Guid SnapshotEventId, JsonDocument SnapshotData)?> GetLatestSnapshotBeforeAsync(Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) =>
      GetLatestSnapshotAsync(streamId, perspectiveName, ct);

    public Task<PerspectiveSnapshotFloor?> GetSnapshotFloorAsync(Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) =>
      Task.FromResult<PerspectiveSnapshotFloor?>(new PerspectiveSnapshotFloor(snapshotEventId, version, data, archivedThroughVersion));

    public Task<bool> HasAnySnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) => Task.FromResult(true);

    public Task PruneOldSnapshotsAsync(Guid streamId, string perspectiveName, int keepCount, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteAllSnapshotsAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) => Task.CompletedTask;
  }

  // -------------------------------------------------------------------------------------------

  private sealed class Harness {
    private readonly ServiceProvider _provider;
    private readonly IPerspectiveSnapshotStore? _snapshots;
    private readonly List<(LogLevel Level, string Message)> _logs;

    public InMemoryEventStore Events { get; } = new();
    public VersionedOrderedStore Rows { get; } = new();
    public IPerspectiveRunner Runner { get; }

    private readonly IEventStore _store;

    public Harness(IPerspectiveSnapshotStore? snapshots = null, List<(LogLevel Level, string Message)>? logs = null,
        Func<InMemoryEventStore, IEventStore>? wrap = null) {
      var services = new ServiceCollection();
      services.AddLogging();
      services.AddSingleton<RewindCheckPerspective>();
      _provider = services.BuildServiceProvider();
      _snapshots = snapshots;
      _logs = logs ?? [];
      _store = wrap is null ? Events : wrap(Events);
      Runner = NewRunner();
    }

    /// <summary>Another runner on the same rows and events: the other writer.</summary>
    public IPerspectiveRunner NewRunner() {
      // The real generated runner, resolved by name: naming a generated type in source does not
      // compile in a workspace that loads without running the generators (the formatting gate).
      var runnerType = typeof(PerspectiveRewindWriteCheckTests).Assembly
        .GetType("Whizbang.Core.Tests.Generated.RewindCheckPerspectiveRunner", throwOnError: true)!;
      var ctor = runnerType.GetConstructors().Single();
      var args = new object?[ctor.GetParameters().Length];
      args[0] = _provider;
      args[1] = Activator.CreateInstance(typeof(CapturingLogger<>).MakeGenericType(runnerType), _logs)!;
      args[2] = _store;
      args[3] = Rows;
      args[4] = _provider.GetRequiredService<IServiceScopeFactory>();
      args[6] = _snapshots;
      args[7] = _snapshots is null ? null : Options.Create(new PerspectiveSnapshotOptions { Enabled = false });
      return (IPerspectiveRunner)ctor.Invoke(args);
    }

    public async Task<MessageEnvelope<IEvent>> AppendAsync(Guid streamId, IEvent payload) {
      var envelope = new MessageEnvelope<IEvent> {
        MessageId = MessageId.New(),
        Payload = payload,
        Hops = [
          new MessageHop {
            Type = HopType.Current,
            ServiceInstance = ServiceInstanceInfo.Unknown,
            Timestamp = DateTimeOffset.UnixEpoch,
          }
        ],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      };
      await Events.AppendAsync(streamId, envelope);
      return envelope;
    }
  }

  private static JsonDocument _snapshotOf(RewindCheckModel model) {
    var info = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<RewindCheckModel>)
      Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(RewindCheckModel));
    using var doc = JsonSerializer.SerializeToDocument(model, info);
    return SnapshotEnvelope.Wrap(doc.RootElement, Whizbang.Core.Serialization.SerializationVersion.CURRENT);
  }

  /// <summary>Records every log line the runner writes, by level.</summary>
  private sealed class CapturingLogger<T>(List<(LogLevel Level, string Message)> sink) : ILogger<T> {
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      sink.Add((logLevel, formatter(state, exception)));
  }

  /// <summary>
  /// A store with both checks the PostgreSQL store makes on a versioned target. A checked write lands only on the
  /// version it read and otherwise throws <see cref="PerspectiveRowConflictException"/>; every write, checked or not,
  /// is refused quietly (reported false) when its event id sorts below the row's stamp. The write hooks are the
  /// deterministic seam another writer commits into, after the rewind's read and before its write.
  /// </summary>
  private sealed class VersionedOrderedStore : IPerspectiveStore<RewindCheckModel> {
    private readonly Dictionary<Guid, (RewindCheckModel Model, long Version, string Stamp)> _rows = [];
    private long _nextVersion = 100;
    private bool _firstWriteSeen;

    public Func<Task>? BeforeFirstWrite { get; set; }
    public Action? BeforeEveryWrite { get; set; }
    public int ReadsForApply { get; private set; }
    public int WriteAttempts { get; private set; }
    public PerspectiveRowVersion LastExpected { get; private set; }

    public void Seed(Guid id, RewindCheckModel model, Guid stampEventId) =>
      _rows[id] = (model, ++_nextVersion, stampEventId.ToString("D"));

    public void Touch(Guid id) => _rows[id] = _rows[id] with { Version = ++_nextVersion };
    public RewindCheckModel Row(Guid id) => _rows[id].Model;
    public long VersionOf(Guid id) => _rows[id].Version;

    public Task<PerspectiveApplyRead> ReadForApplyAsync(Guid streamId, CancellationToken cancellationToken = default) {
      ReadsForApply++;
      return Task.FromResult(_rows.TryGetValue(streamId, out var row)
        ? new PerspectiveApplyRead(PerspectiveRowVersion.Of(row.Version), new PerspectiveMetadata { EventId = row.Stamp, EventType = "stored" })
        : new PerspectiveApplyRead(PerspectiveRowVersion.Absent, null));
    }

    public Task<RewindCheckModel?> GetByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      Task.FromResult(_rows.TryGetValue(streamId, out var row) ? row.Model : null);

    public Task<PerspectiveMetadata?> GetMetadataByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      Task.FromResult(_rows.TryGetValue(streamId, out var row) ? new PerspectiveMetadata { EventId = row.Stamp, EventType = "stored" } : null);

    public async Task<bool> TryUpsertAsync(Guid streamId, RewindCheckModel model, PerspectiveScope scope, bool forceUpdateScope,
        PerspectiveMetadata metadata, PerspectiveRowVersion expectedVersion, CancellationToken cancellationToken = default) {
      WriteAttempts++;
      if (!_firstWriteSeen && BeforeFirstWrite is not null) {
        _firstWriteSeen = true;
        await BeforeFirstWrite();
      }
      BeforeEveryWrite?.Invoke();
      LastExpected = expectedVersion;
      var exists = _rows.TryGetValue(streamId, out var current);
      if (expectedVersion.IsChecked) {
        var actual = exists ? PerspectiveRowVersion.Of(current.Version) : PerspectiveRowVersion.Absent;
        if (actual != expectedVersion) {
          throw new PerspectiveRowConflictException(typeof(RewindCheckModel), streamId, expectedVersion, actual);
        }
      }
      if (exists && string.CompareOrdinal(metadata.EventId, current.Stamp) < 0) {
        return false;  // the ordering guard: an equal event id is admitted (#1608), an earlier one is not
      }
      _rows[streamId] = (model, ++_nextVersion, metadata.EventId);
      return true;
    }

    public Task UpsertAsync(Guid streamId, RewindCheckModel model, PerspectiveScope scope, bool forceUpdateScope,
        PerspectiveMetadata metadata, PerspectiveRowVersion expectedVersion, CancellationToken cancellationToken = default) =>
      TryUpsertAsync(streamId, model, scope, forceUpdateScope, metadata, expectedVersion, cancellationToken);

    public Task UpsertAsync(Guid streamId, RewindCheckModel model, CancellationToken cancellationToken = default)
      => throw new InvalidOperationException("the runner must write through the versioned overload");

    public Task UpsertWithPhysicalFieldsAsync(Guid streamId, RewindCheckModel model, IDictionary<string, object?> physicalFieldValues,
        PerspectiveScope? scope = null, CancellationToken cancellationToken = default)
      => throw new InvalidOperationException("the model has no physical fields");

    public Task<RewindCheckModel?> GetByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => Task.FromResult<RewindCheckModel?>(null);

    public Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, RewindCheckModel model, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => Task.CompletedTask;

    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PurgeAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PurgeByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => Task.CompletedTask;
  }

  /// <summary>Holds one snapshot, placed before every event the test appends, and counts how often it is looked up.</summary>
  private sealed class OneSnapshotStore(Guid snapshotEventId, JsonDocument data) : IPerspectiveSnapshotStore {
    public int Reads { get; private set; }

    public Task CreateSnapshotAsync(Guid streamId, string perspectiveName, Guid snapshotEventId, JsonDocument snapshotData, CancellationToken ct = default) =>
      Task.CompletedTask;

    public Task<(Guid SnapshotEventId, JsonDocument SnapshotData)?> GetLatestSnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult<(Guid, JsonDocument)?>((snapshotEventId, data));

    public Task<(Guid SnapshotEventId, JsonDocument SnapshotData)?> GetLatestSnapshotBeforeAsync(Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) =>
      GetLatestSnapshotAsync(streamId, perspectiveName, ct);

    public Task<(Guid SnapshotEventId, int? SnapshotVersion, JsonDocument SnapshotData)?> GetLatestSnapshotBeforeEventInStreamOrderAsync(
        Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) {
      Reads++;
      return Task.FromResult<(Guid, int?, JsonDocument)?>((snapshotEventId, 1, data));
    }

    public Task<bool> HasAnySnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult(true);

    public Task PruneOldSnapshotsAsync(Guid streamId, string perspectiveName, int keepCount, CancellationToken ct = default) =>
      Task.CompletedTask;

    public Task DeleteAllSnapshotsAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.CompletedTask;
  }
}
