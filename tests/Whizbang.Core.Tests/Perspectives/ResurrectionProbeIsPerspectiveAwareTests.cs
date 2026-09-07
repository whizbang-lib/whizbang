using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>Contract A on the shared stream: the perspective under test does not fold it.</summary>
public sealed record ResurrectionProbeUnhandled : IEvent {
  [StreamId]
  public Guid StreamId { get; init; }
}

/// <summary>Contract B on the shared stream: the only event the perspective folds.</summary>
public sealed record ResurrectionProbeHandled : IEvent {
  [StreamId]
  public Guid StreamId { get; init; }
  public int Delta { get; init; }
}

public sealed class ResurrectionProbeModel {
  [StreamId]
  public Guid Id { get; set; }
  public int Total { get; set; }
}

/// <summary>A row-TTL Sourced perspective, so the row-null branch probes for history.</summary>
[RowTtl(Days = 60)]
public sealed class ResurrectionProbePerspective : IPerspectiveFor<ResurrectionProbeModel, ResurrectionProbeHandled> {
  public ResurrectionProbeModel Apply(ResurrectionProbeModel currentData, ResurrectionProbeHandled @event) {
    currentData.Total += @event.Delta;
    return currentData;
  }
}

/// <summary>
/// Behavioral lock for issue #696 over the real generated runner: on the row-null branch of a
/// row-TTL perspective, the resurrection probe asks the event store about the event types THIS
/// perspective folds. A stream shared by several contracts holds history the perspective never
/// folded; the untyped probe ("any earlier event") read that as "reaped and woken" and re-folded on
/// first contact, with a full replay, a warning, and two system events written into the stream for
/// its lifetime. A genuinely reaped row still has prior events of the handled types, so real
/// resurrection is unchanged.
/// </summary>
/// <docs>fundamentals/perspectives/row-retention</docs>
public class ResurrectionProbeIsPerspectiveAwareTests {
  private const string PERSPECTIVE_NAME = nameof(ResurrectionProbePerspective);

  [Test]
  public async Task FirstContact_OnStreamWithUnhandledHistory_CreatesRow_DoesNotRewindAsync() {
    var streamId = Guid.NewGuid();
    // The stream's first event is another contract's; the perspective's first handled event is
    // version 2. Any-history is true; handled-history is false.
    var store = new RecordingEventStore { AnyHistory = true, HandledHistory = false };
    var rows = new InMemoryPerspectiveStore();
    var runner = _runner(store, rows);
    var incoming = _envelope(new ResurrectionProbeHandled { StreamId = streamId, Delta = 5 });

    await runner.RunWithEventsAsync(streamId, PERSPECTIVE_NAME, null, [incoming], CancellationToken.None);

    await Assert.That(store.TypedProbes).Count().IsEqualTo(1)
      .Because("the probe must be the perspective-aware one");
    // Compared by identity on purpose: a structural comparison of System.Type reflects into
    // members that throw for non-generic-parameter types.
    await Assert.That(store.TypedProbes[0].Types.Length).IsEqualTo(1);
    await Assert.That(store.TypedProbes[0].Types[0] == typeof(ResurrectionProbeHandled)).IsTrue()
      .Because("the probe carries exactly the event types this perspective folds");
    await Assert.That(store.PolymorphicReads).IsEqualTo(0)
      .Because("no rewind: this is a first contact, not a reaped row");
    var row = await rows.GetByStreamIdAsync(streamId);
    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Total).IsEqualTo(5)
      .Because("the row is created from the incoming batch");
  }

  [Test]
  public async Task ReapedRow_OnStreamWithHandledHistory_StillResurrectsAsync() {
    var streamId = Guid.NewGuid();
    var store = new RecordingEventStore { AnyHistory = true, HandledHistory = true };
    var b1 = _envelope(new ResurrectionProbeHandled { StreamId = streamId, Delta = 1 });
    var b2 = _envelope(new ResurrectionProbeHandled { StreamId = streamId, Delta = 2 });
    var b3 = _envelope(new ResurrectionProbeHandled { StreamId = streamId, Delta = 3 });
    store.Stream.AddRange([b1, b2, b3]);   // the reaped row's whole history, incoming event included
    var rows = new InMemoryPerspectiveStore();
    var runner = _runner(store, rows);

    await runner.RunWithEventsAsync(streamId, PERSPECTIVE_NAME, null, [b3], CancellationToken.None);

    await Assert.That(store.PolymorphicReads).IsGreaterThanOrEqualTo(1)
      .Because("prior events of the handled type mean the row was reaped: re-fold from the log");
    var row = await rows.GetByStreamIdAsync(streamId);
    await Assert.That(row).IsNotNull();
    await Assert.That(row!.Total).IsEqualTo(6)
      .Because("the re-fold is the whole history, not the incoming batch alone");
  }

  [Test]
  public async Task FirstContact_OnBrandNewStream_CreatesRowAsync() {
    var streamId = Guid.NewGuid();
    var store = new RecordingEventStore { AnyHistory = false, HandledHistory = false };
    var rows = new InMemoryPerspectiveStore();
    var runner = _runner(store, rows);

    await runner.RunWithEventsAsync(streamId, PERSPECTIVE_NAME, null,
      [_envelope(new ResurrectionProbeHandled { StreamId = streamId, Delta = 7 })], CancellationToken.None);

    await Assert.That(store.PolymorphicReads).IsEqualTo(0);
    var row = await rows.GetByStreamIdAsync(streamId);
    await Assert.That(row!.Total).IsEqualTo(7);
  }

  // -------------------------------------------------------------------------------------------

  private static IPerspectiveRunner _runner(RecordingEventStore store, InMemoryPerspectiveStore rows) {
    // The generated module initializer registers the TTL; register again so a test elsewhere that
    // reconfigures the static registry cannot switch this branch off.
    PerspectiveTtlRegistry.Register(typeof(ResurrectionProbeModel), 60 * 24 * 60 * 60);
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<ResurrectionProbePerspective>();
    var provider = services.BuildServiceProvider();
    // The real generated runner, resolved by name: naming a generated type in source does not
    // compile in a workspace that loads without running the generators (the formatting gate).
    var runnerType = typeof(ResurrectionProbeIsPerspectiveAwareTests).Assembly
      .GetType("Whizbang.Core.Tests.Generated.ResurrectionProbePerspectiveRunner", throwOnError: true)!;
    var logger = Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(runnerType))!;
    var ctor = runnerType.GetConstructors().Single();
    var args = new object?[ctor.GetParameters().Length];
    args[0] = provider;
    args[1] = logger;
    args[2] = store;
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
  /// Answers the two probes from configured flags and records which one the runner asked; serves
  /// <see cref="Stream"/> to the rewind's polymorphic read.
  /// </summary>
  private sealed class RecordingEventStore : IEventStore {
    public bool AnyHistory { get; init; }
    public bool HandledHistory { get; init; }
    public List<MessageEnvelope<IEvent>> Stream { get; } = [];
    public List<(Guid StreamId, Guid Before, Type[] Types)> TypedProbes { get; } = [];
    public int UntypedProbes { get; private set; }
    public int PolymorphicReads { get; private set; }

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull => Task.CompletedTask;

    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) =>
      AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();

    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) =>
      AsyncEnumerable.Empty<MessageEnvelope<TMessage>>();

    public async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
      PolymorphicReads++;
      await Task.Yield();
      // Page like a real store: everything after fromEventId (all of it when null), so the
      // rewind's read-until-drained loop terminates.
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

    public Task<bool> HasStreamEventsBeforeAsync(Guid streamId, Guid beforeEventId, CancellationToken cancellationToken = default) {
      UntypedProbes++;
      return Task.FromResult(AnyHistory);
    }

    public Task<bool> HasStreamEventsBeforeAsync(Guid streamId, Guid beforeEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) {
      TypedProbes.Add((streamId, beforeEventId, [.. eventTypes]));
      return Task.FromResult(HandledHistory);
    }
  }

  private sealed class InMemoryPerspectiveStore : IPerspectiveStore<ResurrectionProbeModel> {
    private readonly Dictionary<Guid, ResurrectionProbeModel> _rows = [];

    public Task<ResurrectionProbeModel?> GetByStreamIdAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      Task.FromResult(_rows.TryGetValue(streamId, out var m) ? m : null);

    public Task UpsertAsync(Guid streamId, ResurrectionProbeModel model, CancellationToken cancellationToken = default) {
      _rows[streamId] = model;
      return Task.CompletedTask;
    }

    public Task UpsertWithPhysicalFieldsAsync(Guid streamId, ResurrectionProbeModel model, IDictionary<string, object?> physicalFieldValues, PerspectiveScope? scope = null, CancellationToken cancellationToken = default) =>
      UpsertAsync(streamId, model, cancellationToken);

    public Task<ResurrectionProbeModel?> GetByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, CancellationToken cancellationToken = default) where TPartitionKey : notnull =>
      Task.FromResult<ResurrectionProbeModel?>(null);

    public Task UpsertByPartitionKeyAsync<TPartitionKey>(TPartitionKey partitionKey, ResurrectionProbeModel model, CancellationToken cancellationToken = default) where TPartitionKey : notnull =>
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
