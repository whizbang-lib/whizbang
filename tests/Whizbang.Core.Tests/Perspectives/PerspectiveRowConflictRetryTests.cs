// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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

/// <summary>Sets both fields of a <see cref="RowConflictModel"/>.</summary>
public sealed record RowConflictCreated : IEvent {
  [StreamId]
  public Guid StreamId { get; init; }
  public string Name { get; init; } = string.Empty;
  public int Value { get; init; }
}

/// <summary>Sets <see cref="RowConflictModel.Value"/> only, carrying the loaded name forward.</summary>
public sealed record RowConflictValueChanged : IEvent {
  [StreamId]
  public Guid StreamId { get; init; }
  public int Value { get; init; }
}

public sealed record RowConflictModel {
  [StreamId]
  public Guid Id { get; init; }
  public string Name { get; init; } = string.Empty;
  public int Value { get; init; }
}

/// <summary>A whole-row fold: every apply returns the entire model, which is what makes a stale write lose data.</summary>
public sealed class RowConflictPerspective :
    IPerspectiveFor<RowConflictModel, RowConflictCreated>,
    IPerspectiveFor<RowConflictModel, RowConflictValueChanged> {
  public RowConflictModel Apply(RowConflictModel currentData, RowConflictCreated @event) =>
    new() { Id = @event.StreamId, Name = @event.Name, Value = @event.Value };

  public RowConflictModel Apply(RowConflictModel currentData, RowConflictValueChanged @event) =>
    currentData with { Value = @event.Value };
}

/// <summary>
/// The generated runner's side of issue #928. A per-stream apply reads the row, folds its events in
/// memory and writes the whole row back; a concurrent writer (a collective apply) that commits between
/// that read and that write used to be silently overwritten. The runner now carries the row version it
/// read to the write, and a store that detects the row moved refuses the write with
/// <see cref="PerspectiveRowConflictException"/>: the runner re-reads, re-applies and writes again, and
/// gives up loudly when the row keeps moving.
/// </summary>
/// <docs>fundamentals/perspectives/perspectives</docs>
public class PerspectiveRowConflictRetryTests {
  private const string PERSPECTIVE_NAME = nameof(RowConflictPerspective);

  [Test]
  public async Task ConcurrentWriteBetweenReadAndWrite_IsNotOverwritten_TheApplyRetriesOntoItAsync() {
    var streamId = Guid.CreateVersion7();
    var store = new VersionedStore();
    store.Seed(streamId, new RowConflictModel { Id = streamId, Name = "original", Value = 1 });
    // Exactly one concurrent writer lands in the window between the runner's read and its write.
    store.BeforeWrite = (row, attempt) => attempt == 1 ? row with { Name = "collective" } : null;

    var result = await _runner(store).RunWithEventsAsync(streamId, PERSPECTIVE_NAME, null,
      [_envelope(new RowConflictValueChanged { StreamId = streamId, Value = 2 })], CancellationToken.None);

    var row = store.Row(streamId);
    await Assert.That(row.Name).IsEqualTo("collective")
      .Because("the concurrent writer's change must survive: the stale whole-row write was refused");
    await Assert.That(row.Value).IsEqualTo(2)
      .Because("and the per-stream event is still applied, onto the row as the concurrent writer left it");
    await Assert.That(store.ReadsForApply).IsEqualTo(2).Because("one read, one re-read after the conflict");
    await Assert.That(store.ModelReads).IsEqualTo(2);
    await Assert.That(store.WriteAttempts).IsEqualTo(2);
    await Assert.That(result.Status).IsEqualTo(PerspectiveProcessingStatus.Completed);
    await Assert.That(result.EventsProcessed).IsEqualTo(1);
  }

  [Test]
  public async Task NoConcurrentWrite_OneReadOneWrite_AndNoMetadataReadAsync() {
    var streamId = Guid.CreateVersion7();
    var store = new VersionedStore();
    store.Seed(streamId, new RowConflictModel { Id = streamId, Name = "original", Value = 1 });
    var versionBefore = store.VersionOf(streamId);

    await _runner(store).RunWithEventsAsync(streamId, PERSPECTIVE_NAME, null,
      [_envelope(new RowConflictValueChanged { StreamId = streamId, Value = 2 })], CancellationToken.None);

    await Assert.That(store.Row(streamId)).IsEqualTo(new RowConflictModel { Id = streamId, Name = "original", Value = 2 });
    await Assert.That(store.ReadsForApply).IsEqualTo(1);
    await Assert.That(store.ModelReads).IsEqualTo(1);
    await Assert.That(store.MetadataReads).IsEqualTo(0)
      .Because("a versioned read already carries the metadata the idempotency filter needs, so the "
        + "separate metadata read the runner used to make is not made at all");
    await Assert.That(store.WriteAttempts).IsEqualTo(1);
    await Assert.That(store.LastExpected).IsEqualTo(PerspectiveRowVersion.Of(versionBefore));
  }

  [Test]
  public async Task RowAbsentAtRead_TheModelIsNotReadAndTheWriteExpectsNoRowAsync() {
    var streamId = Guid.CreateVersion7();
    var store = new VersionedStore();

    await _runner(store).RunWithEventsAsync(streamId, PERSPECTIVE_NAME, null,
      [_envelope(new RowConflictCreated { StreamId = streamId, Name = "new", Value = 5 })], CancellationToken.None);

    await Assert.That(store.ModelReads).IsEqualTo(0)
      .Because("the versioned read already proved there is no row; reading the model would be a wasted round trip");
    await Assert.That(store.LastExpected).IsEqualTo(PerspectiveRowVersion.Absent);
    await Assert.That(store.Row(streamId)).IsEqualTo(new RowConflictModel { Id = streamId, Name = "new", Value = 5 });
  }

  [Test]
  public async Task RowKeepsMoving_TheApplyGivesUpLoudly_AndNeverOverwritesAsync() {
    var streamId = Guid.CreateVersion7();
    var store = new VersionedStore();
    store.Seed(streamId, new RowConflictModel { Id = streamId, Name = "original", Value = 1 });
    store.BeforeWrite = (row, attempt) => row with { Name = "collective-" + attempt };

    var runner = _runner(store);
    var thrown = await Assert.That(async () => await runner.RunWithEventsAsync(streamId, PERSPECTIVE_NAME, null,
        [_envelope(new RowConflictValueChanged { StreamId = streamId, Value = 2 })], CancellationToken.None))
      .Throws<PerspectiveRowConflictException>();

    await Assert.That(thrown!.StreamId).IsEqualTo(streamId);
    await Assert.That(store.WriteAttempts).IsEqualTo(5)
      .Because("the retry is bounded: a row that changes under every attempt is reported, not spun on");
    var row = store.Row(streamId);
    await Assert.That(row.Name).IsEqualTo("collective-5");
    await Assert.That(row.Value).IsEqualTo(1)
      .Because("no stale write ever landed; the event stays unapplied and is redelivered by the failure path");
  }

  [Test]
  public async Task ConcurrentWriterAppliedTheSameEvent_TheRetrySkipsItAsync() {
    var streamId = Guid.CreateVersion7();
    var store = new VersionedStore();
    store.Seed(streamId, new RowConflictModel { Id = streamId, Name = "original", Value = 1 });
    var incoming = _envelope(new RowConflictValueChanged { StreamId = streamId, Value = 2 });
    // The concurrent writer is another apply of the very same event (for example a rewind on the same
    // row): it records the event as applied, so the retry must find nothing left to do.
    store.BeforeWrite = (row, attempt) => {
      if (attempt == 1) {
        store.StampApplied(streamId, incoming.MessageId.Value);
        return row with { Value = 2 };
      }
      return null;
    };

    var result = await _runner(store).RunWithEventsAsync(streamId, PERSPECTIVE_NAME, null, [incoming], CancellationToken.None);

    await Assert.That(result.Status).IsEqualTo(PerspectiveProcessingStatus.Completed);
    await Assert.That(result.EventsProcessed).IsEqualTo(0)
      .Because("on the re-read the idempotency filter sees the event already applied");
    await Assert.That(store.WriteAttempts).IsEqualTo(1);
    await Assert.That(store.Row(streamId).Value).IsEqualTo(2);
  }

  [Test]
  public async Task PreLifecycleReceptors_FireOncePerBatch_NotOncePerAttemptAsync() {
    var streamId = Guid.CreateVersion7();
    var store = new VersionedStore();
    store.Seed(streamId, new RowConflictModel { Id = streamId, Name = "original", Value = 1 });
    store.BeforeWrite = (row, attempt) => attempt == 1 ? row with { Name = "collective" } : null;
    var invoker = new CountingReceptorInvoker();

    await _runner(store, invoker).RunWithEventsAsync(streamId, PERSPECTIVE_NAME, null,
      [_envelope(new RowConflictValueChanged { StreamId = streamId, Value = 2 })], CancellationToken.None);

    await Assert.That(store.WriteAttempts).IsEqualTo(2);
    await Assert.That(invoker.Count(LifecycleStage.PrePerspectiveInline)).IsEqualTo(1)
      .Because("a retry re-reads and re-folds the row; it does not re-announce the batch");
    await Assert.That(invoker.Count(LifecycleStage.PrePerspectiveDetached)).IsEqualTo(1);
    await Assert.That(invoker.Count(LifecycleStage.PostPerspectiveDetached)).IsEqualTo(1)
      .Because("the post stage follows the one write that landed");
  }

  [Test]
  public async Task StoreWithoutVersions_KeepsTheOldReadOrder_ModelThenMetadataAsync() {
    var streamId = Guid.CreateVersion7();
    var store = new UnversionedStore();
    store.Rows[streamId] = new RowConflictModel { Id = streamId, Name = "original", Value = 1 };

    await _runner(store).RunWithEventsAsync(streamId, PERSPECTIVE_NAME, null,
      [_envelope(new RowConflictValueChanged { StreamId = streamId, Value = 2 })], CancellationToken.None);

    await Assert.That(string.Join(",", store.Calls)).IsEqualTo("model,metadata,upsert")
      .Because("a store that cannot version its rows sees exactly the calls it saw before this change");
    await Assert.That(store.Rows[streamId].Value).IsEqualTo(2);
  }

  // -------------------------------------------------------------------------------------------

  private static IPerspectiveRunner _runner(IPerspectiveStore<RowConflictModel> rows, CountingReceptorInvoker? invoker = null) {
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<RowConflictPerspective>();
    if (invoker is not null) {
      services.AddSingleton<IReceptorInvoker>(invoker);
      services.AddSingleton<IReceptorRegistry>(new EveryStageRegistry());
    }
    var provider = services.BuildServiceProvider();
    // The real generated runner, resolved by name: naming a generated type in source does not
    // compile in a workspace that loads without running the generators (the formatting gate).
    var runnerType = typeof(PerspectiveRowConflictRetryTests).Assembly
      .GetType("Whizbang.Core.Tests.Generated.RowConflictPerspectiveRunner", throwOnError: true)!;
    var logger = Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(runnerType))!;
    var ctor = runnerType.GetConstructors().Single();
    var args = new object?[ctor.GetParameters().Length];
    args[0] = provider;
    args[1] = logger;
    args[2] = new EmptyEventStore();
    args[3] = rows;
    args[4] = provider.GetRequiredService<IServiceScopeFactory>();
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

  /// <summary>
  /// A store that versions its rows the way the Postgres store does: every write moves the version,
  /// and a write whose expected version no longer matches is refused with a conflict.
  /// <see cref="BeforeWrite"/> is the deterministic seam for a concurrent writer: it runs after the
  /// runner's read and before its write, and may replace the row (a committed concurrent write).
  /// </summary>
  private sealed class VersionedStore : IPerspectiveStore<RowConflictModel> {
    private readonly Dictionary<Guid, (RowConflictModel Model, long Version, PerspectiveMetadata? Metadata)> _rows = [];
    private long _nextVersion = 100;

    public Func<RowConflictModel, int, RowConflictModel?>? BeforeWrite { get; set; }
    public int ReadsForApply { get; private set; }
    public int ModelReads { get; private set; }
    public int MetadataReads { get; private set; }
    public int WriteAttempts { get; private set; }
    public PerspectiveRowVersion LastExpected { get; private set; }

    public void Seed(Guid id, RowConflictModel model) => _rows[id] = (model, ++_nextVersion, null);
    public RowConflictModel Row(Guid id) => _rows[id].Model;
    public long VersionOf(Guid id) => _rows[id].Version;

    public void StampApplied(Guid id, Guid eventId) {
      var current = _rows[id];
      _rows[id] = current with { Metadata = new PerspectiveMetadata { EventId = eventId.ToString("D"), EventType = "concurrent" } };
    }

    public Task<PerspectiveApplyRead> ReadForApplyAsync(Guid streamId, CancellationToken cancellationToken = default) {
      ReadsForApply++;
      return Task.FromResult(_rows.TryGetValue(streamId, out var row)
        ? new PerspectiveApplyRead(PerspectiveRowVersion.Of(row.Version), row.Metadata)
        : new PerspectiveApplyRead(PerspectiveRowVersion.Absent, null));
    }

    public Task<RowConflictModel?> GetByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) {
      ModelReads++;
      return Task.FromResult(_rows.TryGetValue(streamId, out var row) ? row.Model : null);
    }

    public Task<PerspectiveMetadata?> GetMetadataByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) {
      MetadataReads++;
      return Task.FromResult(_rows.TryGetValue(streamId, out var row) ? row.Metadata : null);
    }

    public Task UpsertAsync(Guid streamId, RowConflictModel model, PerspectiveScope scope, bool forceUpdateScope,
        PerspectiveMetadata metadata, PerspectiveRowVersion expectedVersion, CancellationToken cancellationToken = default) {
      WriteAttempts++;
      LastExpected = expectedVersion;
      if (BeforeWrite is not null && _rows.TryGetValue(streamId, out var before)) {
        var concurrent = BeforeWrite(before.Model, WriteAttempts);
        if (concurrent is not null) {
          _rows[streamId] = (concurrent, ++_nextVersion, _rows[streamId].Metadata);
        }
      }
      var actual = _rows.TryGetValue(streamId, out var current)
        ? PerspectiveRowVersion.Of(current.Version)
        : PerspectiveRowVersion.Absent;
      if (actual != expectedVersion) {
        throw new PerspectiveRowConflictException(typeof(RowConflictModel), streamId, expectedVersion, actual);
      }
      _rows[streamId] = (model, ++_nextVersion, metadata);
      return Task.CompletedTask;
    }

    public Task UpsertAsync(Guid streamId, RowConflictModel model, CancellationToken cancellationToken = default)
      => throw new InvalidOperationException("the runner must write through the versioned overload");

    public Task UpsertWithPhysicalFieldsAsync(Guid streamId, RowConflictModel model, IDictionary<string, object?> physicalFieldValues,
        PerspectiveScope? scope = null, CancellationToken cancellationToken = default)
      => throw new InvalidOperationException("the model has no physical fields");

    public Task<RowConflictModel?> GetByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => Task.FromResult<RowConflictModel?>(null);

    public Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, RowConflictModel model, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => Task.CompletedTask;

    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PurgeAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PurgeByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => Task.CompletedTask;
  }

  /// <summary>A store written before row versions existed: it overrides none of the versioned members.</summary>
  private sealed class UnversionedStore : IPerspectiveStore<RowConflictModel> {
    public Dictionary<Guid, RowConflictModel> Rows { get; } = [];
    public List<string> Calls { get; } = [];

    public Task<RowConflictModel?> GetByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) {
      Calls.Add("model");
      return Task.FromResult(Rows.TryGetValue(streamId, out var row) ? row : null);
    }

    public Task<PerspectiveMetadata?> GetMetadataByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) {
      Calls.Add("metadata");
      return Task.FromResult<PerspectiveMetadata?>(null);
    }

    public Task UpsertAsync(Guid streamId, RowConflictModel model, CancellationToken cancellationToken = default) {
      Calls.Add("upsert");
      Rows[streamId] = model;
      return Task.CompletedTask;
    }

    public Task UpsertWithPhysicalFieldsAsync(Guid streamId, RowConflictModel model, IDictionary<string, object?> physicalFieldValues,
        PerspectiveScope? scope = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<RowConflictModel?> GetByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => Task.FromResult<RowConflictModel?>(null);

    public Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, RowConflictModel model, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => Task.CompletedTask;

    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PurgeAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PurgeByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default)
      where TPartitionKey : notnull => Task.CompletedTask;
  }

  /// <summary>Counts receptor invocations per lifecycle stage.</summary>
  private sealed class CountingReceptorInvoker : IReceptorInvoker {
    private readonly System.Collections.Concurrent.ConcurrentDictionary<LifecycleStage, int> _counts = new();

    public int Count(LifecycleStage stage) => _counts.GetValueOrDefault(stage);

    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage, ILifecycleContext? context = null,
        CancellationToken cancellationToken = default) {
      _counts.AddOrUpdate(stage, 1, (_, n) => n + 1);
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>Reports one receptor at every stage, so the runner invokes each stage it reaches.</summary>
  private sealed class EveryStageRegistry : IReceptorRegistry {
    private static readonly ReceptorInfo[] _one = [
      new ReceptorInfo(typeof(RowConflictValueChanged), "row-conflict-probe", (_, _, _, _, _) => ValueTask.FromResult<object?>(null))
    ];

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) => _one;
    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }

  /// <summary>An event store with no history: the batch under test is all there is.</summary>
  private sealed class EmptyEventStore : IEventStore {
    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull => Task.CompletedTask;
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) =>
      AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) =>
      AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();
    public IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      AsyncEnumerable.Empty<MessageEnvelope<IEvent>>();
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<MessageEnvelope<TMessage>>());
    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      Task.FromResult(new List<MessageEnvelope<IEvent>>());
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => Task.FromResult(0L);
    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) => [];
  }
}
