// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

#pragma warning disable CA1859 // the fan-out Apply returns the spec interface, as a generated invoker expects

using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Postgres.Collective;

namespace Whizbang.Data.EFCore.Postgres.Tests.Collective;

/// <summary>
/// #2621: the replay applier's fan-out half, with no database: which models fold fan-outs, the streams a row's keys hold,
/// the merge of a key's stream into the events a replay has still to fold, and the in-memory apply that folds a foreign
/// event only while the row's key holds its stream and only into a row the live sink reached.
/// </summary>
/// <tests>src/Whizbang.Data.Postgres/Collective/CollectiveReplayApplier.cs</tests>
/// <tests>src/Whizbang.Data.Postgres/Collective/CollectiveInMemoryExecutor.cs</tests>
/// <docs>fundamentals/perspectives/multi-stream#fan-out-on-rebuild-and-rewind</docs>
[Category("Unit")]
[Category("CollectiveEvents")]
[Category("Shard1")]
public class FanOutReplayApplierTests {
  private const string TENANT = "tenant-a";

  internal sealed class Order {
    public Guid CustomerId { get; set; }
    public Guid BillingId { get; set; }
    public OrderCustomerId? TypedCustomer { get; set; }
    public string Name { get; set; } = string.Empty;
  }

  internal sealed class Unrelated;

  internal sealed record Renamed(string Name) : IEvent;

  internal sealed record Billed(string Name) : IEvent;

  internal sealed record Ignored : IEvent;

  /// <summary>A strongly typed id over a Guid, as a key column can hold.</summary>
  internal readonly record struct OrderCustomerId(Guid Value) : IWhizbangId {
    public Guid ToGuid() => Value;
    public bool IsTimeOrdered => false;
    public bool SubMillisecondPrecision => false;
    public DateTimeOffset Timestamp => DateTimeOffset.UnixEpoch;
    public bool Equals(IWhizbangId? other) => other is not null && other.ToGuid() == Value;
    public int CompareTo(IWhizbangId? other) => other is null ? 1 : Value.CompareTo(other.ToGuid());
  }

  internal sealed class Projection {
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Minor Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "Invoked through the instance invoker the generator emits.")]
    public ICollectiveSpec<Order> Apply(Renamed e) => new Spec(s => s.SetProperty(o => o.Name, e.Name));

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Minor Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "Invoked through the instance invoker the generator emits.")]
    public ICollectiveSpec<Order> Apply(Billed e) => new Spec(s => s.SetProperty(o => o.Name, "billed " + e.Name));
  }

  private sealed record Spec(Expression<Action<ICollectiveSetters<Order>>> Setters) : ICollectiveSpec<Order>;

  private static PerspectiveFanOutEntry _entry<TEvent>(Expression<Func<Order, object?>> key, bool createsItsExecutor = true)
      where TEvent : IEvent =>
    new(typeof(Projection), typeof(Order), typeof(TEvent), "Key", "key",
      static (p, e) => e is Renamed r ? ((Projection)p).Apply(r) : ((Projection)p).Apply((Billed)e), key) {
      CreateInMemoryExecutor = createsItsExecutor ? static factory => factory.Create<Order>() : null,
    };

  private static readonly PerspectiveFanOutEntry _byCustomer = _entry<Renamed>(o => o.CustomerId);

  /// <summary>An executor written before replays folded fan-outs: it implements only what it did then.</summary>
  private sealed class OldExecutor : ICollectiveInMemoryExecutor {
    public Type ModelType => typeof(Order);
    public object ApplyToRow(object spec, object currentModel, Guid streamId) => currentModel;
  }

  /// <summary>Reads the given streams; nothing else of the store is used.</summary>
  private sealed class StreamsEventStore(Dictionary<Guid, List<MessageEnvelope<IEvent>>> streams) : IEventStore {
    public List<(Guid Stream, IReadOnlyList<Type> Types)> Reads { get; } = [];

    public IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(
        Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) {
      Reads.Add((streamId, eventTypes));
      return (streams.GetValueOrDefault(streamId) ?? []).ToAsyncEnumerable();
    }

    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken cancellationToken = default) where TMessage : notnull =>
      throw new NotSupportedException();
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(
        Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(
        Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) =>
      throw new NotSupportedException();
  }

  private sealed class NoQuery : IEventStoreQuery {
    public IQueryable<EventStoreRecord> Query => Enumerable.Empty<EventStoreRecord>().AsQueryable();
    public IQueryable<EventStoreRecord> GetStreamEvents(Guid streamId) => Query;
    public IQueryable<EventStoreRecord> GetEventsByType(string eventType) => Query;
  }

  private static CollectiveReplayApplier _applier(
      IReadOnlyList<PerspectiveFanOutEntry> fanOuts,
      Dictionary<Guid, List<MessageEnvelope<IEvent>>>? streams = null,
      IReadOnlyList<ICollectiveInMemoryExecutor>? executors = null,
      Func<IReadOnlyList<Guid>, CancellationToken, Task<IReadOnlyDictionary<Guid, CollectiveApplyReach>>>? reach = null,
      Func<IReadOnlyList<string>, FanOutMissCheck, CancellationToken, Task<int>>? missed = null) =>
    new([], new ServiceCollection().AddSingleton(new Projection()).BuildServiceProvider(),
      new StreamsEventStore(streams ?? []), new NoQuery(), executors ?? [],
      new CollectiveReplayApplier.ReplaySources(ArchivedStreams: null, reach, fanOuts) { Missed = missed });

  private static MessageEnvelope<IEvent> _event(IEvent payload, long? commitSequence, string? tenant = TENANT) => new() {
    MessageId = MessageId.From(Guid.CreateVersion7()),
    Payload = payload,
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    Hops = tenant is null ? [] : [new MessageHop {
      Type = HopType.Current,
      Timestamp = DateTimeOffset.UnixEpoch,
      ServiceInstance = ServiceInstanceInfo.Unknown,
      Scope = ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = tenant, UserId = "user-a" }),
    }],
    LocalCommitSequence = commitSequence,
  };

  // ===== which models, which keys =====

  [Test]
  public async Task FoldsFanOutsInto_IsTrueOnlyForAModelAFanOutTargetsAsync() {
    var applier = _applier([_byCustomer]);

    await Assert.That(applier.FoldsFanOutsInto(typeof(Order))).IsTrue();
    await Assert.That(applier.FoldsFanOutsInto(typeof(Unrelated))).IsFalse();
    await Assert.That(() => applier.FoldsFanOutsInto(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task FanOutKeysOf_NamesEachStreamTheModelsKeysHold_OnceAsync() {
    var (customer, billing) = (Guid.CreateVersion7(), Guid.CreateVersion7());
    var applier = _applier([_byCustomer, _entry<Billed>(o => o.BillingId), _entry<Ignored>(o => o.CustomerId)]);

    await Assert.That(applier.FanOutKeysOf(typeof(Order), new Order { CustomerId = customer, BillingId = billing }))
      .IsEquivalentTo([customer, billing]).Because("two fan-outs by one key read its stream once");
    await Assert.That(applier.FanOutKeysOf(typeof(Order), new Order { CustomerId = customer, BillingId = customer }))
      .IsEquivalentTo([customer]).Because("two keys that hold one stream name it once");
    await Assert.That(applier.FanOutKeysOf(typeof(Order), new Order())).IsEmpty()
      .Because("a key holding the empty id holds no stream");
    await Assert.That(applier.FanOutKeysOf(typeof(Unrelated), new Unrelated())).IsEmpty()
      .Because("no fan-out targets the model, so no executor is even looked up");
    await Assert.That(() => applier.FanOutKeysOf(null!, new Order())).Throws<ArgumentNullException>();
    await Assert.That(() => applier.FanOutKeysOf(typeof(Order), null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task FanOutKeyOf_IsTheStreamTheKeyHolds_AStronglyTypedIdByItsGuidAsync() {
    var customer = Guid.CreateVersion7();
    ICollectiveInMemoryExecutor executor = new CollectiveInMemoryExecutor<Order>();
    var typed = _entry<Renamed>(o => o.TypedCustomer);

    await Assert.That(executor.FanOutKeyOf(_byCustomer, new Order { CustomerId = customer })).IsEqualTo(customer);
    await Assert.That(executor.FanOutKeyOf(typed, new Order { TypedCustomer = new OrderCustomerId(customer) })).IsEqualTo(customer)
      .Because("a strongly typed id is compared by its Guid, as its column stores it");
    await Assert.That(executor.FanOutKeyOf(typed, new Order())).IsNull().Because("an unset key holds no stream");
    await Assert.That(executor.FanOutKeyOf(_byCustomer, new Order())).IsNull().Because("nor does the empty id");
    await Assert.That(() => executor.FanOutKeyOf(null!, new Order())).Throws<ArgumentNullException>();
    await Assert.That(() => executor.FanOutKeyOf(_byCustomer, null!)).Throws<ArgumentNullException>();
    await Assert.That(() => CollectiveInMemoryEvaluator<Order>.ReadKey(null!, new Order())).Throws<ArgumentNullException>();
    await Assert.That(() => CollectiveInMemoryEvaluator<Order>.ReadKey(_byCustomer.KeySelector, null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task AnExecutorWrittenBeforeReplaysFoldedFanOuts_RefusesToReadAKeyAsync() {
    ICollectiveInMemoryExecutor executor = new OldExecutor();

    await Assert.That(() => executor.FanOutKeyOf(_byCustomer, new Order())).Throws<NotSupportedException>()
      .Because("a replay that could not read the key would fold the foreign events into no row, silently");
  }

  [Test]
  public async Task AFanOutModelWithNoRegisteredExecutor_GetsItsReplayTwinFromTheEntryAsync() {
    var customer = Guid.CreateVersion7();
    var made = _applier([_byCustomer]);
    var registered = _applier([_byCustomer], executors: [new OldExecutor()]);
    var neither = _applier([_entry<Renamed>(o => o.CustomerId, createsItsExecutor: false)]);

    await Assert.That(made.FanOutKeysOf(typeof(Order), new Order { CustomerId = customer })).IsEquivalentTo([customer]);
    await Assert.That(() => registered.FanOutKeysOf(typeof(Order), new Order { CustomerId = customer })).Throws<NotSupportedException>()
      .Because("a registered executor is kept, and the entry's is not made beside it");
    await Assert.That(() => neither.FanOutKeysOf(typeof(Order), new Order())).Throws<InvalidOperationException>()
      .Because("an entry written by hand with no executor and none registered cannot be folded, and says so");
  }

  [Test]
  public async Task ThePublicConstructor_RefusesNoFanOutListAsync() {
    await Assert.That(() => new CollectiveReplayApplier([], new ServiceCollection().BuildServiceProvider(),
      new StreamsEventStore([]), new NoQuery(), [], archive: null, fanOuts: null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task CollectiveEventTypeNamesFor_NamesTheModelsFanOutTypesAsync() {
    var applier = _applier([_byCustomer, _entry<Billed>(o => o.BillingId)]);

    await Assert.That(applier.CollectiveEventTypeNamesFor(TypeNameFormatter.FormatClrTypeName(typeof(Order)).Replace('+', '.')))
      .IsEquivalentTo([TypeNameFormatter.Format(typeof(Renamed)), TypeNameFormatter.Format(typeof(Billed))])
      .Because("to a replay a foreign event that changes the row is another stream's event, as a collective is");
    await Assert.That(applier.CollectiveEventTypeNamesFor(TypeNameFormatter.FormatClrTypeName(typeof(Unrelated)))).IsEmpty();
  }

  // ===== the merge =====

  [Test]
  public async Task InterleaveFanOutsAsync_MergesTheStreamsFanOutsAfterTheStart_InTheTenant_AsCollectivesAsync() {
    var customer = Guid.CreateVersion7();
    var before = _event(new Renamed("before"), 5);
    var otherTenant = _event(new Renamed("other"), 11, tenant: "tenant-b");
    var noTenant = _event(new Renamed("none"), 12, tenant: null);
    var first = _event(new Renamed("first"), 15);
    var second = _event(new Renamed("second"), 30);
    var store = new Dictionary<Guid, List<MessageEnvelope<IEvent>>> {
      [customer] = [before, otherTenant, noTenant, first, second],
    };
    var applier = _applier([_byCustomer, _entry<Billed>(o => o.CustomerId)], store);
    var own20 = _event(new Ignored(), 20);
    var own40 = _event(new Ignored(), 40);

    var merged = await applier.InterleaveFanOutsAsync(
      typeof(Order), [own20, own40], customer, new CollectiveReplayStart(TENANT, Guid.CreateVersion7(), 10), CancellationToken.None);

    await Assert.That(merged.Select(e => e.MessageId.Value))
      .IsEquivalentTo([first.MessageId.Value, own20.MessageId.Value, second.MessageId.Value, own40.MessageId.Value], CollectionOrdering.Matching)
      .Because("each fan-out after the start goes after every remaining event it was committed after");
    var scope = (FanOutScope)((ICollectiveEvent)merged[0].Payload).Scope;
    await Assert.That((scope.StreamId, scope.TenantId, scope.EventType)).IsEqualTo((customer, TENANT, typeof(Renamed)));
    var folded = (Order)applier.ApplyInMemory(typeof(Order), new Order { CustomerId = customer }, Guid.CreateVersion7(), merged[0].Payload);
    await Assert.That(folded.Name).IsEqualTo("first").Because("the collective carries the foreign event it wraps");
    await Assert.That(merged[0].Hops).IsEmpty().Because("the row keeps its own scope, as the live update never writes it");
    await Assert.That(merged[0].LocalCommitSequence).IsEqualTo(15L);
  }

  [Test]
  public async Task InterleaveFanOutsAsync_ForAModelWithNoFanOut_OrAStartWithNoTenant_ReturnsTheEventsUntouchedAsync() {
    var customer = Guid.CreateVersion7();
    var events = new StreamsEventStore([]);
    var applier = new CollectiveReplayApplier([], new ServiceCollection().BuildServiceProvider(), events, new NoQuery(), [],
      new CollectiveReplayApplier.ReplaySources(null, null, [_byCustomer]));
    IReadOnlyList<MessageEnvelope<IEvent>> remaining = [_event(new Ignored(), 1)];

    await Assert.That(await applier.InterleaveFanOutsAsync(typeof(Unrelated), remaining, customer, new CollectiveReplayStart(TENANT), CancellationToken.None))
      .IsSameReferenceAs(remaining);
    await Assert.That(await applier.InterleaveFanOutsAsync(typeof(Order), remaining, customer, new CollectiveReplayStart(null), CancellationToken.None))
      .IsSameReferenceAs(remaining).Because("a row with no tenant is reached by no fan-out, as live");
    await Assert.That(events.Reads).IsEmpty().Because("neither reads the store");
    await Assert.That(await applier.InterleaveFanOutsAsync(typeof(Order), remaining, customer, new CollectiveReplayStart(TENANT), CancellationToken.None))
      .IsSameReferenceAs(remaining).Because("a stream with no fan-out of the model merges nothing");
    await Assert.That(events.Reads.Single().Types).IsEquivalentTo([typeof(Renamed)]).Because("only the model's fan-out types are read");
    await Assert.That(async () => await applier.InterleaveFanOutsAsync(null!, remaining, customer, new CollectiveReplayStart(TENANT), CancellationToken.None))
      .Throws<ArgumentNullException>();
    await Assert.That(async () => await applier.InterleaveFanOutsAsync(typeof(Order), null!, customer, new CollectiveReplayStart(TENANT), CancellationToken.None))
      .Throws<ArgumentNullException>();
    await Assert.That(async () => await applier.InterleaveFanOutsAsync(typeof(Order), remaining, customer, null!, CancellationToken.None))
      .Throws<ArgumentNullException>();
  }

  // The decision (#2275): a received stream's events follow ReplayEventOrder, the producer's order.
  [Test]
  public async Task InterleaveFanOutsAsync_PutsAReceivedStreamsFanOutsInItsProducersOrderAsync() {
    var (customer, producer) = (Guid.CreateVersion7(), Guid.CreateVersion7());
    var second = _withOrigin(_event(new Renamed("second"), 20), producer, 2);
    var first = _withOrigin(_event(new Renamed("first"), 21), producer, 1);
    var applier = _applier([_byCustomer], new() { [customer] = [second, first] });

    var merged = await applier.InterleaveFanOutsAsync(typeof(Order), [], customer, new CollectiveReplayStart(TENANT), CancellationToken.None);

    await Assert.That(merged.Select(e => e.MessageId.Value))
      .IsEquivalentTo([first.MessageId.Value, second.MessageId.Value], CollectionOrdering.Matching)
      .Because("the producer renamed to first and then to second, whatever order this service stored them in");
  }

  private static MessageEnvelope<IEvent> _withOrigin(MessageEnvelope<IEvent> envelope, Guid origin, long originSequence) => new() {
    MessageId = envelope.MessageId,
    Payload = envelope.Payload,
    DispatchContext = envelope.DispatchContext,
    Hops = envelope.Hops,
    LocalCommitSequence = envelope.LocalCommitSequence,
    SourceServiceId = origin,
    SourceCommitSequence = originSequence,
  };

  // ===== the lookup after a live write (#2622) =====

  private static FanOutMissCheck _check(string? tenant = TENANT, IReadOnlyList<FanOutKeyTaken>? keys = null) =>
    new(Guid.CreateVersion7(), "Orders.Perspective", tenant, Guid.CreateVersion7(),
      keys ?? [new FanOutKeyTaken(Guid.CreateVersion7(), 10, Guid.CreateVersion7())], [Guid.CreateVersion7()]);

  [Test]
  public async Task RewindIfFanOutsMissedAsync_AsksTheDatabaseForTheRowsKeys_InTheModelsFanOutTypesAsync() {
    (IReadOnlyList<string> Types, FanOutMissCheck Check)? asked = null;
    var applier = _applier(
      [_byCustomer, _entry<Renamed>(o => o.BillingId), _entry<Billed>(o => o.CustomerId)],
      missed: (types, check, _) => {
        asked = (types, check);
        return Task.FromResult(1);
      });
    var check = _check();

    var rewound = await applier.RewindIfFanOutsMissedAsync(typeof(Order), check, CancellationToken.None);

    await Assert.That(rewound).IsEqualTo(1);
    await Assert.That(asked!.Value.Check).IsSameReferenceAs(check);
    await Assert.That(asked.Value.Types).IsEquivalentTo([TypeNameFormatter.Format(typeof(Renamed)), TypeNameFormatter.Format(typeof(Billed))])
      .Because("each of the model's fan-out types once, by the name the store records");
  }

  [Test]
  public async Task RewindIfFanOutsMissedAsync_AsksNothing_WithNoDatabase_NoFanOut_NoTenantOrNoKeyAsync() {
    var asks = 0;
    var applier = _applier([_byCustomer], missed: (_, _, _) => {
      asks++;
      return Task.FromResult(1);
    });

    await Assert.That(await _applier([_byCustomer]).RewindIfFanOutsMissedAsync(typeof(Order), _check(), CancellationToken.None)).IsEqualTo(0)
      .Because("an applier made with no database checks nothing");
    await Assert.That(await applier.RewindIfFanOutsMissedAsync(typeof(Unrelated), _check(), CancellationToken.None)).IsEqualTo(0);
    await Assert.That(await applier.RewindIfFanOutsMissedAsync(typeof(Order), _check(tenant: null), CancellationToken.None)).IsEqualTo(0);
    await Assert.That(await applier.RewindIfFanOutsMissedAsync(typeof(Order), _check(keys: []), CancellationToken.None)).IsEqualTo(0);
    await Assert.That(asks).IsEqualTo(0);
    await Assert.That(async () => await applier.RewindIfFanOutsMissedAsync(null!, _check(), CancellationToken.None)).Throws<ArgumentNullException>();
    await Assert.That(async () => await applier.RewindIfFanOutsMissedAsync(typeof(Order), null!, CancellationToken.None)).Throws<ArgumentNullException>();
  }

  // ===== the in-memory apply =====

  [Test]
  public async Task ApplyInMemory_FoldsAFanOut_OnlyWhileTheRowsKeyHoldsItsStream_ThroughEachFanOutOfItsTypeAsync() {
    var (customer, other) = (Guid.CreateVersion7(), Guid.CreateVersion7());
    var applier = _applier([_byCustomer, _entry<Billed>(o => o.CustomerId), _entry<Renamed>(o => o.BillingId)]);
    var rename = PerspectiveFanOutEntry.CollectiveEventFor(new Renamed("Ada"), customer, TENANT);

    var held = (Order)applier.ApplyInMemory(typeof(Order), new Order { CustomerId = customer, Name = "old" }, Guid.CreateVersion7(), rename);
    var moved = (Order)applier.ApplyInMemory(typeof(Order), new Order { CustomerId = other, Name = "old" }, Guid.CreateVersion7(), rename);
    var byTheOtherKey = (Order)applier.ApplyInMemory(
      typeof(Order), new Order { CustomerId = other, BillingId = customer, Name = "old" }, Guid.CreateVersion7(), rename);

    await Assert.That(held.Name).IsEqualTo("Ada");
    await Assert.That(moved.Name).IsEqualTo("old").Because("the row's key no longer holds the event's stream");
    await Assert.That(byTheOtherKey.Name).IsEqualTo("Ada").Because("the second fan-out of the type targets the row by its other key");
    await Assert.That(applier.IsInFlight(rename)).IsFalse().Because("no reach was read for it");
  }

  // #1970: a fan-out folds only into the rows the live sink's apply reached, or will reach.
  [Test]
  public async Task ApplyInMemory_FoldsAFanOutTheSinkIsApplying_OnlyIntoTheRowsItReached_AndSaysItIsInFlightAsync() {
    var customer = Guid.CreateVersion7();
    var through = Guid.Parse("20000000-0000-0000-0000-000000000000");
    var rename = _event(new Renamed("Ada"), 15);
    var applier = _applier([_byCustomer], new() { [customer] = [rename] },
      reach: (ids, _) => Task.FromResult<IReadOnlyDictionary<Guid, CollectiveApplyReach>>(CollectiveApplyReach.From([
        (ids[0], _byCustomer.EntryKey, (Guid?)through, false, false, false),
        (ids[0], null, null, false, false, true),
      ])));

    var merged = await applier.InterleaveFanOutsAsync(typeof(Order), [], customer, new CollectiveReplayStart(TENANT), CancellationToken.None);
    var fanOut = merged.Single().Payload;
    var reached = (Order)applier.ApplyInMemory(
      typeof(Order), new Order { CustomerId = customer }, Guid.Parse("10000000-0000-0000-0000-000000000000"), fanOut);
    var beyond = (Order)applier.ApplyInMemory(
      typeof(Order), new Order { CustomerId = customer }, Guid.Parse("30000000-0000-0000-0000-000000000000"), fanOut);

    await Assert.That((reached.Name, beyond.Name)).IsEqualTo(("Ada", string.Empty));
    await Assert.That(applier.IsInFlight(fanOut)).IsTrue();
  }
}
