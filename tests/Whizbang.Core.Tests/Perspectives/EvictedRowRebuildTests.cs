// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Perspectives;

// Each perspective folds its own event type, so no perspective reads another's group traffic (WHIZ140).

/// <summary>Folded by <see cref="EvictionCappedPerspective"/> only.</summary>
public sealed record EvictionCappedAdded : IEvent {
  [StreamId]
  public Guid StreamId { get; init; }
  public int Delta { get; init; }
}

/// <summary>A row of a perspective whose rows leave by a row cap, with no row TTL.</summary>
public sealed class EvictionCappedModel {
  [StreamId]
  public Guid Id { get; set; }
  public int Total { get; set; }
}

/// <summary>A Sourced perspective evicted by its cap alone: no TTL, so the old probe gate never opened.</summary>
[RowCap(PerScope = 10)]
public sealed class EvictionCappedPerspective : IPerspectiveFor<EvictionCappedModel, EvictionCappedAdded> {
  public EvictionCappedModel Apply(EvictionCappedModel currentData, EvictionCappedAdded @event) {
    currentData.Total += @event.Delta;
    return currentData;
  }
}

/// <summary>Folded by both members of the eviction group.</summary>
public sealed record EvictionGroupAdded : IEvent {
  [StreamId]
  public Guid StreamId { get; init; }
  public int Delta { get; init; }
}

public sealed class EvictionGroupLeaderModel {
  [StreamId]
  public Guid Id { get; set; }
  public int Total { get; set; }
}

public sealed class EvictionGroupFollowerModel {
  [StreamId]
  public Guid Id { get; set; }
  public int Total { get; set; }
}

/// <summary>The announcing member: its cap is what starts a cascade.</summary>
[RowCap(PerScope = 10)]
[StreamGroup("eviction-rebuild")]
public sealed class EvictionGroupLeaderPerspective : IPerspectiveFor<EvictionGroupLeaderModel, EvictionGroupAdded> {
  public EvictionGroupLeaderModel Apply(EvictionGroupLeaderModel currentData, EvictionGroupAdded @event) {
    currentData.Total += @event.Delta;
    return currentData;
  }
}

/// <summary>A member with no retention of its own: its rows leave only by the group cascade.</summary>
[StreamGroup("eviction-rebuild", Announce = false)]
public sealed class EvictionGroupFollowerPerspective : IPerspectiveFor<EvictionGroupFollowerModel, EvictionGroupAdded> {
  public EvictionGroupFollowerModel Apply(EvictionGroupFollowerModel currentData, EvictionGroupAdded @event) {
    currentData.Total += @event.Delta;
    return currentData;
  }
}

/// <summary>An ephemeral event: its body is reaped once consumed, so a full replay cannot rebuild from it.</summary>
[Ephemeral]
public sealed record EvictionEphemeralAdded : IEvent {
  [StreamId]
  public Guid StreamId { get; init; }
  public int Delta { get; init; }
}

public sealed class EvictionEphemeralModel {
  [StreamId]
  public Guid Id { get; set; }
  public int Total { get; set; }
}

/// <summary>An ephemeral perspective whose rows leave by a cap.</summary>
[RowCap(PerScope = 10)]
public sealed class EvictionEphemeralPerspective : IPerspectiveFor<EvictionEphemeralModel, EvictionEphemeralAdded> {
  public EvictionEphemeralModel Apply(EvictionEphemeralModel currentData, EvictionEphemeralAdded @event) {
    currentData.Total += @event.Delta;
    return currentData;
  }
}

/// <summary>Folded by <see cref="EvictionUnretainedPerspective"/> only.</summary>
public sealed record EvictionUnretainedAdded : IEvent {
  [StreamId]
  public Guid StreamId { get; init; }
  public int Delta { get; init; }
}

public sealed class EvictionUnretainedModel {
  [StreamId]
  public Guid Id { get; set; }
  public int Total { get; set; }
}

/// <summary>Declares no retention and joins no group: nothing can evict its rows.</summary>
public sealed class EvictionUnretainedPerspective : IPerspectiveFor<EvictionUnretainedModel, EvictionUnretainedAdded> {
  public EvictionUnretainedModel Apply(EvictionUnretainedModel currentData, EvictionUnretainedAdded @event) {
    currentData.Total += @event.Delta;
    return currentData;
  }
}

/// <summary>
/// Issue #1483 over the real generated runners: after row retention evicts a row, the next event on its
/// stream rebuilds the whole row from the latest snapshot plus the history after it, whatever evicted the
/// row (a TTL, a cap, or a stream-group cascade). Applying only the incoming event onto an empty model
/// would build a partial row that nothing would ever repair. When the history was reaped (an ephemeral
/// stream) and no snapshot survives, the row starts fresh from the incoming event and says so.
/// </summary>
/// <remarks>
/// A row TTL already rebuilt this way; a cap or a cascade did not, because the probe that tells a
/// reaped row from a new stream ran only for row-TTL perspectives. An ephemeral perspective never probed
/// at all, so it restarted silently even when a snapshot could have restored it.
/// </remarks>
/// <docs>fundamentals/perspectives/row-retention#what-an-eviction-removes</docs>
public class EvictedRowRebuildTests {
  [Test]
  public async Task CappedPerspective_EvictedRow_NextEventRebuildsTheWholeRowFromHistoryAsync() {
    var streamId = Guid.NewGuid();
    var store = new HistoryEventStore { HandledHistory = true };
    var b1 = _envelope(new EvictionCappedAdded { StreamId = streamId, Delta = 1 });
    var b2 = _envelope(new EvictionCappedAdded { StreamId = streamId, Delta = 2 });
    var b3 = _envelope(new EvictionCappedAdded { StreamId = streamId, Delta = 3 });
    store.Stream.AddRange([b1, b2, b3]);
    var rows = new InMemoryRows<EvictionCappedModel>();
    var runner = _runner<EvictionCappedPerspective, EvictionCappedModel>(store, rows, snapshots: null, logs: null);

    await runner.RunWithEventsAsync(streamId, _name<EvictionCappedPerspective>(), null, [b3], CancellationToken.None);

    var row = await rows.GetByStreamIdAsync(streamId);
    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Total).IsEqualTo(6)
      .Because("a capped row that was evicted is rebuilt from its history, not started over from the incoming event");
  }

  [Test]
  public async Task CappedPerspective_EvictedRowWithSnapshot_RestoresTheSnapshotAndReplaysTheTailAsync() {
    var streamId = Guid.NewGuid();
    var store = new HistoryEventStore { HandledHistory = true };
    var b1 = _envelope(new EvictionCappedAdded { StreamId = streamId, Delta = 1 });
    var b2 = _envelope(new EvictionCappedAdded { StreamId = streamId, Delta = 2 });
    var b3 = _envelope(new EvictionCappedAdded { StreamId = streamId, Delta = 3 });
    var b4 = _envelope(new EvictionCappedAdded { StreamId = streamId, Delta = 4 });
    store.Stream.AddRange([b1, b2, b3, b4]);
    // The snapshot's total is deliberately not the fold of b1..b2, so the result shows where it came from.
    var snapshots = new KeptSnapshotStore(b2.MessageId.Value, _snapshotOf(new EvictionCappedModel { Id = streamId, Total = 100 }));
    var rows = new InMemoryRows<EvictionCappedModel>();
    var runner = _runner<EvictionCappedPerspective, EvictionCappedModel>(store, rows, snapshots, logs: null);

    await runner.RunWithEventsAsync(streamId, _name<EvictionCappedPerspective>(), null, [b4], CancellationToken.None);

    var row = await rows.GetByStreamIdAsync(streamId);
    await Assert.That(row!.Total).IsEqualTo(107)
      .Because("the rebuild restores the kept snapshot (100) and replays only the events after it (3 + 4)");
  }

  [Test]
  public async Task CascadedGroupMember_EvictedRow_NextEventRebuildsTheWholeRowFromHistoryAsync() {
    var streamId = Guid.NewGuid();
    var store = new HistoryEventStore { HandledHistory = true };
    var b1 = _envelope(new EvictionGroupAdded { StreamId = streamId, Delta = 5 });
    var b2 = _envelope(new EvictionGroupAdded { StreamId = streamId, Delta = 6 });
    store.Stream.AddRange([b1, b2]);
    var rows = new InMemoryRows<EvictionGroupFollowerModel>();
    var runner = _runner<EvictionGroupFollowerPerspective, EvictionGroupFollowerModel>(store, rows, snapshots: null, logs: null);

    await runner.RunWithEventsAsync(streamId, _name<EvictionGroupFollowerPerspective>(), null, [b2], CancellationToken.None);

    var row = await rows.GetByStreamIdAsync(streamId);
    await Assert.That(row!.Total).IsEqualTo(11)
      .Because("a group member declares no retention of its own, yet the cascade evicts its rows; it rebuilds the same way");
  }

  [Test]
  public async Task EphemeralPerspective_EvictedRowWithSnapshot_RestoresTheSnapshotAndReplaysTheTailAsync() {
    var streamId = Guid.NewGuid();
    var store = new HistoryEventStore { HandledHistory = true };
    var b1 = _envelope(new EvictionEphemeralAdded { StreamId = streamId, Delta = 1 });
    var b2 = _envelope(new EvictionEphemeralAdded { StreamId = streamId, Delta = 2 });
    var b3 = _envelope(new EvictionEphemeralAdded { StreamId = streamId, Delta = 3 });
    // b1's body was reaped once the snapshot covered it; only the events after the snapshot remain readable.
    store.Stream.AddRange([b2, b3]);
    var snapshots = new KeptSnapshotStore(b1.MessageId.Value, _snapshotOf(new EvictionEphemeralModel { Id = streamId, Total = 50 }));
    var rows = new InMemoryRows<EvictionEphemeralModel>();
    var runner = _runner<EvictionEphemeralPerspective, EvictionEphemeralModel>(store, rows, snapshots, logs: null);

    await runner.RunWithEventsAsync(streamId, _name<EvictionEphemeralPerspective>(), null, [b3], CancellationToken.None);

    var row = await rows.GetByStreamIdAsync(streamId);
    await Assert.That(row!.Total).IsEqualTo(55)
      .Because("the kept snapshot is the ephemeral row's floor: restore it (50) and replay what follows (2 + 3)");
  }

  [Test]
  public async Task EphemeralPerspective_EvictedRowWithoutSnapshot_StartsFreshAndWarnsAsync() {
    var streamId = Guid.NewGuid();
    var store = new HistoryEventStore { HandledHistory = true };
    var incoming = _envelope(new EvictionEphemeralAdded { StreamId = streamId, Delta = 9 });
    store.Stream.Add(incoming);   // every earlier body is reaped; the pointers still answer the probe
    var rows = new InMemoryRows<EvictionEphemeralModel>();
    var logs = new List<(LogLevel Level, string Message)>();
    var runner = _runner<EvictionEphemeralPerspective, EvictionEphemeralModel>(
      store, rows, new KeptSnapshotStore(snapshotEventId: null, data: null), logs);

    await runner.RunWithEventsAsync(streamId, _name<EvictionEphemeralPerspective>(), null, [incoming], CancellationToken.None);

    var row = await rows.GetByStreamIdAsync(streamId);
    await Assert.That(row!.Total).IsEqualTo(9)
      .Because("with the history reaped and no snapshot, nothing can rebuild the row: it starts from the incoming event");
    await Assert.That(logs.Any(l => l.Level == LogLevel.Warning && l.Message.Contains("starting it fresh", StringComparison.Ordinal))).IsTrue()
      .Because("a row that silently lost its earlier state is the defect; the loss is accepted, so it is reported");
  }

  [Test]
  public async Task EphemeralPerspective_NoSnapshotStore_StartsFreshAndWarnsAsync() {
    var streamId = Guid.NewGuid();
    var store = new HistoryEventStore { HandledHistory = true };
    var incoming = _envelope(new EvictionEphemeralAdded { StreamId = streamId, Delta = 4 });
    store.Stream.Add(incoming);
    var rows = new InMemoryRows<EvictionEphemeralModel>();
    var logs = new List<(LogLevel Level, string Message)>();
    var runner = _runner<EvictionEphemeralPerspective, EvictionEphemeralModel>(store, rows, snapshots: null, logs);

    await runner.RunWithEventsAsync(streamId, _name<EvictionEphemeralPerspective>(), null, [incoming], CancellationToken.None);

    await Assert.That((await rows.GetByStreamIdAsync(streamId))!.Total).IsEqualTo(4);
    await Assert.That(logs.Any(l => l.Level == LogLevel.Warning && l.Message.Contains("starting it fresh", StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task UnretainedPerspective_MissingRow_NeverProbesTheLogAsync() {
    // The probe costs a query on every first contact, so it runs only where an eviction is possible.
    var streamId = Guid.NewGuid();
    var store = new HistoryEventStore { HandledHistory = true };
    var incoming = _envelope(new EvictionUnretainedAdded { StreamId = streamId, Delta = 2 });
    var rows = new InMemoryRows<EvictionUnretainedModel>();
    var runner = _runner<EvictionUnretainedPerspective, EvictionUnretainedModel>(store, rows, snapshots: null, logs: null);

    await runner.RunWithEventsAsync(streamId, _name<EvictionUnretainedPerspective>(), null, [incoming], CancellationToken.None);

    await Assert.That(store.TypedProbes).IsEqualTo(0)
      .Because("no retention and no group means no row of this perspective is ever evicted");
    await Assert.That((await rows.GetByStreamIdAsync(streamId))!.Total).IsEqualTo(2);
  }

  // -------------------------------------------------------------------------------------------

  private static string _name<TPerspective>() => TypeNameFormatter.FormatClrTypeName(typeof(TPerspective));

  private static JsonDocument _snapshotOf<TModel>(TModel model) {
    var info = (JsonTypeInfo<TModel>)JsonContextRegistry.CreateCombinedOptions().GetTypeInfo(typeof(TModel));
    var element = JsonSerializer.SerializeToElement(model, info);
    return SnapshotEnvelope.Wrap(element, SerializationVersion.CURRENT);
  }

  private static IPerspectiveRunner _runner<TPerspective, TModel>(
      HistoryEventStore store,
      InMemoryRows<TModel> rows,
      IPerspectiveSnapshotStore? snapshots,
      List<(LogLevel Level, string Message)>? logs)
      where TPerspective : class
      where TModel : class {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<TPerspective>();
    var provider = services.BuildServiceProvider();
    // The real generated runner, resolved by name: naming a generated type in source does not compile in
    // a workspace that loads without running the generators (the formatting gate).
    var runnerType = typeof(EvictedRowRebuildTests).Assembly
      .GetType($"Whizbang.Core.Tests.Generated.{typeof(TPerspective).Name}Runner", throwOnError: true)!;
    var logger = Activator.CreateInstance(
      typeof(CapturingLogger<>).MakeGenericType(runnerType), logs ?? [])!;
    var ctor = runnerType.GetConstructors().Single();
    var args = new object?[ctor.GetParameters().Length];
    args[0] = provider;
    args[1] = logger;
    args[2] = store;
    args[3] = rows;
    args[4] = provider.GetRequiredService<IServiceScopeFactory>();
    args[6] = snapshots;
    return (IPerspectiveRunner)ctor.Invoke(args);
  }

  private static MessageEnvelope<IEvent> _envelope(IEvent payload) => new() {
    MessageId = MessageId.New(),
    Payload = payload,
    Hops = [
      new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Timestamp = DateTimeOffset.UtcNow,
      }
    ],
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
  };

  /// <summary>Records every log line the runner writes, by level.</summary>
  private sealed class CapturingLogger<T>(List<(LogLevel Level, string Message)> sink) : ILogger<T> {
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      sink.Add((logLevel, formatter(state, exception)));
  }

  /// <summary>Holds at most one snapshot: the latest one an eviction keeps.</summary>
  private sealed class KeptSnapshotStore(Guid? snapshotEventId, JsonDocument? data) : IPerspectiveSnapshotStore {
    public Task CreateSnapshotAsync(Guid streamId, string perspectiveName, Guid snapshotEventId, JsonDocument snapshotData, CancellationToken ct = default) =>
      Task.CompletedTask;

    public Task<(Guid SnapshotEventId, JsonDocument SnapshotData)?> GetLatestSnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult(snapshotEventId is Guid id ? ((Guid, JsonDocument)?)(id, data!) : null);

    public Task<(Guid SnapshotEventId, JsonDocument SnapshotData)?> GetLatestSnapshotBeforeAsync(Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) =>
      GetLatestSnapshotAsync(streamId, perspectiveName, ct);

    public Task<(Guid SnapshotEventId, int? SnapshotVersion, JsonDocument SnapshotData)?> GetLatestSnapshotBeforeEventInStreamOrderAsync(
        Guid streamId, string perspectiveName, Guid beforeEventId, CancellationToken ct = default) =>
      Task.FromResult(snapshotEventId is Guid id ? ((Guid, int?, JsonDocument)?)(id, 1, data!) : null);

    public Task<bool> HasAnySnapshotAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.FromResult(snapshotEventId is not null);

    public Task PruneOldSnapshotsAsync(Guid streamId, string perspectiveName, int keepCount, CancellationToken ct = default) =>
      Task.CompletedTask;

    public Task DeleteAllSnapshotsAsync(Guid streamId, string perspectiveName, CancellationToken ct = default) =>
      Task.CompletedTask;
  }

  /// <summary>
  /// Answers the perspective-aware history probe from a flag, counts it, and serves <see cref="Stream"/>
  /// to the rebuild's polymorphic read the way a real store pages.
  /// </summary>
  private sealed class HistoryEventStore : IEventStore {
    public bool HandledHistory { get; init; }
    public List<MessageEnvelope<IEvent>> Stream { get; } = [];
    public int TypedProbes { get; private set; }

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull => Task.CompletedTask;

    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) =>
      AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();

    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) =>
      AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();

    public async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
      await Task.Yield();
      var start = fromEventId is null ? 0 : Stream.FindIndex(e => e.MessageId.Value == fromEventId.Value) + 1;
      for (var i = Math.Max(start, 0); i < Stream.Count; i++) {
        yield return Stream[i];
      }
    }

    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<MessageEnvelope<TMessage>>());

    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      Task.FromResult(Stream.ToList());

    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult((long)Stream.Count);

    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) => [];

    public Task<bool> HasStreamEventsBeforeAsync(Guid streamId, Guid beforeEventId, CancellationToken cancellationToken = default) =>
      Task.FromResult(HandledHistory);

    public Task<bool> HasStreamEventsBeforeAsync(Guid streamId, Guid beforeEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) {
      TypedProbes++;
      return Task.FromResult(HandledHistory);
    }
  }

  private sealed class InMemoryRows<TModel> : IPerspectiveStore<TModel> where TModel : class {
    private readonly Dictionary<Guid, TModel> _rows = [];

    public Task<TModel?> GetByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      Task.FromResult(_rows.TryGetValue(streamId, out var m) ? m : null);

    public Task UpsertAsync(Guid streamId, TModel model, CancellationToken cancellationToken = default) {
      _rows[streamId] = model;
      return Task.CompletedTask;
    }

    public Task UpsertWithPhysicalFieldsAsync(Guid streamId, TModel model, IDictionary<string, object?> physicalFieldValues, PerspectiveScope? scope = null, CancellationToken cancellationToken = default) =>
      UpsertAsync(streamId, model, cancellationToken);

    public Task<TModel?> GetByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default) where TPartitionKey : notnull =>
      Task.FromResult<TModel?>(null);

    public Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, TModel model, CancellationToken cancellationToken = default) where TPartitionKey : notnull =>
      Task.CompletedTask;

    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PurgeAsync(Guid streamId, CancellationToken cancellationToken = default) {
      _rows.Remove(streamId);
      return Task.CompletedTask;
    }

    public Task PurgeByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default) where TPartitionKey : notnull =>
      Task.CompletedTask;
  }
}
