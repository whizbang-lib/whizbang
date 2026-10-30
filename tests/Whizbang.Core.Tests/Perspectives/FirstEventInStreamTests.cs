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

/// <summary>Something happened to a stream; the first one opens it.</summary>
public sealed record FirstSeenHappened : IEvent {
  [StreamId]
  public Guid StreamId { get; init; }
}

public sealed record FirstSeenModel {
  [StreamId]
  public Guid Id { get; init; }

  /// <summary>One mark per Apply: <c>F</c> when the Apply saw the stream's first event, <c>-</c> otherwise.</summary>
  public string Marks { get; init; } = string.Empty;
}

/// <summary>Records, per Apply, what the apply context said about the event.</summary>
public sealed class FirstSeenPerspective : IPerspectiveFor<FirstSeenModel, FirstSeenHappened> {
  public FirstSeenModel Apply(FirstSeenModel currentData, FirstSeenHappened @event) =>
    currentData with {
      Id = @event.StreamId,
      Marks = currentData.Marks + (ApplyContext.Current.IsFirstEventInStream ? "F" : "-"),
    };
}

/// <summary>
/// An <c>Apply</c> learns that it is applying a stream's first event from the event's own position in its stream, never
/// from the row it is handed (#1878). The runner never passes a null model, and the row it does pass changes under a
/// purge, an eviction, a rebuild or a replay, so it cannot say which event came first. The position can, and every
/// replay of the stream reproduces it.
/// </summary>
/// <docs>fundamentals/perspectives/perspectives-with-actions#no-row-contract</docs>
public class FirstEventInStreamTests {
  private const string PERSPECTIVE_NAME = nameof(FirstSeenPerspective);

  [Test]
  public async Task TheEventThatOpensAStream_IsTheFirstEventInStreamAsync() {
    var harness = new Harness();
    var streamId = TrackedGuid.New().Value;
    await harness.AppendAsync(streamId, 3);

    await harness.Runner.RunAsync(streamId, PERSPECTIVE_NAME, lastProcessedEventId: null, CancellationToken.None);

    await Assert.That(harness.Rows.Row(streamId).Marks).IsEqualTo("F--")
      .Because("only the event at the stream's first position is the first event in the stream");
  }

  [Test]
  public async Task ALaterEventOnAStreamWithNoRow_IsNotTheFirstEventInStreamAsync() {
    var harness = new Harness();
    var streamId = TrackedGuid.New().Value;
    var appended = await harness.AppendAsync(streamId, 2);

    // No row exists (never written, purged or evicted): the Apply is handed an empty model, yet the event it applies
    // is the stream's second, and the fact says so.
    await harness.Runner.RunAsync(streamId, PERSPECTIVE_NAME, lastProcessedEventId: appended[0].MessageId.Value, CancellationToken.None);

    await Assert.That(harness.Rows.Row(streamId).Marks).IsEqualTo("-")
      .Because("an empty model is not a first event: the fact comes from the event's position, not the row (#1878)");
  }

  [Test]
  public async Task ARewind_ReproducesTheFirstEventFactAsync() {
    var harness = new Harness();
    var streamId = TrackedGuid.New().Value;
    var appended = await harness.AppendAsync(streamId, 3);
    await harness.Runner.RunAsync(streamId, PERSPECTIVE_NAME, lastProcessedEventId: null, CancellationToken.None);

    await harness.Runner.RewindAndRunAsync(streamId, PERSPECTIVE_NAME, appended[0].MessageId.Value, CancellationToken.None);

    await Assert.That(harness.Rows.Row(streamId).Marks).IsEqualTo("F--")
      .Because("a replay of the stream folds the same events at the same positions, so it reaches the same facts");
  }

  [Test]
  public async Task AfterTheRun_TheFactIsNoLongerCurrentAsync() {
    var harness = new Harness();
    var streamId = TrackedGuid.New().Value;
    await harness.AppendAsync(streamId, 1);

    await harness.Runner.RunAsync(streamId, PERSPECTIVE_NAME, lastProcessedEventId: null, CancellationToken.None);

    await Assert.That(ApplyContext.Current.IsFirstEventInStream).IsFalse()
      .Because("the runner sets the fact for each Apply and restores it when the Apply returns");
  }

  [Test]
  public async Task TheInMemoryStore_MarksOnlyTheStreamsFirstEventAsync() {
    var harness = new Harness();
    var streamId = TrackedGuid.New().Value;
    var appended = await harness.AppendAsync(streamId, 3);

    var whole = await harness.ReadAsync(streamId, fromEventId: null);
    var afterFirst = await harness.ReadAsync(streamId, fromEventId: appended[0].MessageId.Value);

    await Assert.That(whole.Select(e => e.IsFirstInLocalStream)).IsEquivalentTo([true, false, false]);
    await Assert.That(afterFirst.Select(e => e.IsFirstInLocalStream)).IsEquivalentTo([false, false])
      .Because("a read that starts after the first event holds no first event, whatever it reads first");
  }

  // -------------------------------------------------------------------------------------------

  private sealed class Harness {
    public InMemoryEventStore Events { get; } = new();
    public InMemoryRows<FirstSeenModel> Rows { get; } = new();
    public IPerspectiveRunner Runner { get; }

    public Harness() {
      var services = new ServiceCollection();
      services.AddLogging();
      services.AddSingleton<FirstSeenPerspective>();
      Runner = GeneratedRunners.Create(
        "Whizbang.Core.Tests.Generated.FirstSeenPerspectiveRunner", services.BuildServiceProvider(), Events, Rows);
    }

    public async Task<List<MessageEnvelope<IEvent>>> AppendAsync(Guid streamId, int count) {
      var appended = new List<MessageEnvelope<IEvent>>();
      for (var i = 0; i < count; i++) {
        var envelope = GeneratedRunners.Envelope(new FirstSeenHappened { StreamId = streamId });
        await Events.AppendAsync(streamId, envelope);
        appended.Add(envelope);
      }
      return appended;
    }

    public async Task<List<MessageEnvelope<IEvent>>> ReadAsync(Guid streamId, Guid? fromEventId) {
      var read = new List<MessageEnvelope<IEvent>>();
      await foreach (var envelope in Events.ReadPolymorphicAsync(streamId, fromEventId, [typeof(FirstSeenHappened)])) {
        read.Add(envelope);
      }
      return read;
    }
  }
}

/// <summary>Builds the real generated runner for a test perspective, and envelopes to append.</summary>
internal static class GeneratedRunners {
  /// <summary>
  /// The generated runner, resolved by name: naming a generated type in source does not compile in a workspace that
  /// loads without running the generators (the formatting gate).
  /// </summary>
  public static IPerspectiveRunner Create<TModel>(
      string runnerTypeName,
      IServiceProvider provider,
      IEventStore events,
      IPerspectiveStore<TModel> rows,
      IPerspectiveSnapshotStore? snapshots = null,
      Microsoft.Extensions.Options.IOptions<PerspectiveSnapshotOptions>? snapshotOptions = null) where TModel : class {
    var runnerType = typeof(GeneratedRunners).Assembly.GetType(runnerTypeName, throwOnError: true)!;
    var ctor = runnerType.GetConstructors().Single();
    var args = new object?[ctor.GetParameters().Length];
    args[0] = provider;
    args[1] = Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(runnerType));
    args[2] = events;
    args[3] = rows;
    args[4] = provider.GetRequiredService<IServiceScopeFactory>();
    args[6] = snapshots;
    args[7] = snapshotOptions;
    return (IPerspectiveRunner)ctor.Invoke(args);
  }

  public static MessageEnvelope<IEvent> Envelope(IEvent payload, Guid sourceServiceId = default, long sourceCommitSequence = 0) => new() {
    MessageId = MessageId.New(),
    SourceServiceId = sourceServiceId,
    SourceCommitSequence = sourceCommitSequence,
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
}

/// <summary>Rows in memory, unversioned, with what each one holds readable by the test.</summary>
internal sealed class InMemoryRows<TModel> : IPerspectiveStore<TModel> where TModel : class {
  private readonly Dictionary<Guid, TModel> _rows = [];

  public TModel Row(Guid streamId) => _rows[streamId];

  public bool Has(Guid streamId) => _rows.ContainsKey(streamId);

  public int Writes { get; private set; }

  public void Seed(Guid streamId, TModel model) => _rows[streamId] = model;

  public Task<TModel?> GetByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) =>
    Task.FromResult(_rows.TryGetValue(streamId, out var m) ? m : null);

  public Task UpsertAsync(Guid streamId, TModel model, CancellationToken cancellationToken = default) {
    Writes++;
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
