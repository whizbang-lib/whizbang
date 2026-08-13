using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Reconcile × rewind scenario locks: what happens to a perspective's MODEL STATE when
/// stream-integrity reconciliation lands events LATE — after newer events of the same stream
/// have already been applied. Every existing rewind test asserts handler-fire counts or a
/// commutative counter, both of which are order-blind; these tests assert final field values,
/// which is the only thing that can catch an ordering clobber.
/// <para>
/// A backfilled event keeps its ORIGINAL event id and origin sequence but receives a FRESH
/// LOCAL commit_sequence when it lands — always higher than the cursor — so the cursor-inversion
/// detector (which compares local commit_sequence only) can never see it. The tests below pin
/// today's arrival-order contract precisely, including the clobber it permits; the origin-aware
/// inversion follow-up flips the documenting locks to the full-order-replay contract.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Generators/Templates/PerspectiveRunnerTemplate.cs</code-under-test>
public class ReconcileRewindScenarioTests : EFCoreTestBase {

  private const string PERSPECTIVE = "action_test";

  private static async Task<IPerspectiveRunner> CreateRunnerAsync(
      IEventStore eventStore,
      EFCorePostgresPerspectiveStore<ActionTestModel> perspectiveStore) {
    var services = new ServiceCollection();
    services.AddTransient<ActionTestPerspective>();
    services.AddLogging();
    var sp = services.BuildServiceProvider();
    await Task.CompletedTask;

    var runnerType = typeof(ReconcileRewindScenarioTests).Assembly.GetTypes()
        .Single(t => t.Name == "ActionTestPerspectiveRunner");
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var createLoggerMethod = typeof(LoggerFactoryExtensions)
        .GetMethods()
        .Single(m => m.Name == "CreateLogger" && m.IsGenericMethod)
        .MakeGenericMethod(runnerType);
    var logger = createLoggerMethod.Invoke(null, [loggerFactory])!;
    var ctor = runnerType.GetConstructors().Single();
    return (IPerspectiveRunner)ctor.Invoke([
      sp, logger, eventStore, (IPerspectiveStore<ActionTestModel>)perspectiveStore,
      sp.GetRequiredService<IServiceScopeFactory>(),
      null, null, null, null, null
    ]);
  }

  /// <summary>
  /// Mirrors production: <c>GetCommitSequenceAsync</c> answers from the store's stamp (the
  /// checkpoint metadata reads it there), and replay reads carry <c>LocalCommitSequence</c>.
  /// The bare in-memory store answers null for both, which is the stamper-lag shape — these
  /// scenarios need the settled shape.
  /// </summary>
  private sealed class _stampingEventStore(
      InMemoryEventStore inner, IReadOnlyDictionary<Guid, long?> stamps) : IEventStore {
    public Task AppendAsync<TMessage>(Guid streamId, MessageEnvelope<TMessage> envelope, CancellationToken ct = default) =>
      inner.AppendAsync(streamId, envelope, ct);
    public Task AppendAsync<TMessage>(Guid streamId, TMessage message, CancellationToken ct = default) where TMessage : notnull =>
      inner.AppendAsync(streamId, message, ct);
    public Task<long?> GetCommitSequenceAsync(Guid eventId, CancellationToken ct = default) =>
      Task.FromResult(stamps.TryGetValue(eventId, out var v) ? v : null);
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, long fromSequence, CancellationToken ct = default) =>
      inner.ReadAsync<TMessage>(streamId, fromSequence, ct);
    public IAsyncEnumerable<MessageEnvelope<TMessage>> ReadAsync<TMessage>(Guid streamId, Guid? fromEventId, CancellationToken ct = default) =>
      inner.ReadAsync<TMessage>(streamId, fromEventId, ct);
    public async IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(
        Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default) {
      await foreach (var envelope in inner.ReadPolymorphicAsync(streamId, fromEventId, eventTypes, ct)) {
        if (stamps.TryGetValue(envelope.MessageId.Value, out var seq)) {
          envelope.LocalCommitSequence = seq;
        }
        yield return envelope;
      }
    }
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken ct = default) =>
      inner.GetEventsBetweenAsync<TMessage>(streamId, afterEventId, upToEventId, ct);
    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken ct = default) =>
      inner.GetEventsBetweenPolymorphicAsync(streamId, afterEventId, upToEventId, eventTypes, ct);
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken ct = default) =>
      inner.GetLastSequenceAsync(streamId, ct);
    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) =>
      inner.DeserializeStreamEvents(streamEvents, eventTypes);
  }

  private static MessageEnvelope<IEvent> _envelope(Guid id, IEvent payload, long? localCommitSequence) => new() {
    MessageId = MessageId.From(id),
    Payload = payload,
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    Hops = [],
    LocalCommitSequence = localCommitSequence,
  };

  private async Task<ActionTestModel> _modelAsync(Guid streamId) {
    await using var verify = CreateDbContext();
    var row = await verify.Set<PerspectiveRow<ActionTestModel>>()
        .AsNoTracking()
        .FirstAsync(r => r.Id == streamId);
    return row.Data;
  }

  /// <summary>
  /// THE CLOBBER LOCK (documenting — current contract). A newer same-field writer is applied,
  /// then reconciliation backfills an OLDER event of the same field. The backfilled event
  /// carries its original (old) event id but a FRESH local commit_sequence, so the idempotency
  /// floor passes it and no inversion fires — it applies in ARRIVAL order and the model shows
  /// the OLD value. A full-order replay would show 999. When origin-aware inversion lands,
  /// this test flips to expect 999.
  /// </summary>
  [Test]
  public async Task Reconcile_OlderConflictingWriterBackfilled_ArrivalOrderClobbers_CurrentContractAsync() {
    var streamId = Guid.NewGuid();
    var eventStore = new InMemoryEventStore();

    var createdId = Guid.Parse("019e8000-0000-7000-8000-000000000001");
    var newerId = Guid.Parse("019e9000-0000-7000-8000-000000000002");
    var backfilledOldId = Guid.Parse("019e8800-0000-7000-8000-000000000003");
    var stamped = new _stampingEventStore(eventStore, new Dictionary<Guid, long?> {
      [createdId] = 100,
      [newerId] = 200,
      [backfilledOldId] = 300,
    });
    await eventStore.AppendAsync(streamId, _envelope(createdId,
      new ActionTestCreatedEvent { StreamId = streamId, Name = "Job", Value = 1 }, 100));
    await eventStore.AppendAsync(streamId, _envelope(newerId,
      new ActionTestUpdatedEvent { StreamId = streamId, NewValue = 999 }, 200));

    await using (var ctx = CreateDbContext()) {
      var ps = new EFCorePostgresPerspectiveStore<ActionTestModel>(ctx, PERSPECTIVE);
      var runner = await CreateRunnerAsync(stamped, ps);
      var live = await runner.RunAsync(streamId, PERSPECTIVE, null, CancellationToken.None);
      await Assert.That(live.EventsProcessed).IsEqualTo(2);
    }
    await Assert.That((await _modelAsync(streamId)).Value).IsEqualTo(999)
      .Because("precondition: the newer writer is applied");

    // The reconciled straggler: ORIGINAL (older) event id, origin-older payload — but the
    // local stamper hands it a fresh commit_sequence at landing time, exactly like a real
    // backfill. It sails past the commit-sequence idempotency floor (300 > 200).
    var straggler = _envelope(backfilledOldId,
      new ActionTestUpdatedEvent { StreamId = streamId, NewValue = 50 }, 300);
    await eventStore.AppendAsync(streamId, straggler);

    await using (var ctx2 = CreateDbContext()) {
      var ps = new EFCorePostgresPerspectiveStore<ActionTestModel>(ctx2, PERSPECTIVE);
      var runner = await CreateRunnerAsync(stamped, ps);
      var result = await runner.RunWithEventsAsync(
        streamId, PERSPECTIVE, newerId, [straggler], CancellationToken.None);
      await Assert.That(result.EventsProcessed).IsEqualTo(1)
        .Because("the fresh local commit_sequence passes the idempotency floor — the straggler applies");
    }

    await Assert.That((await _modelAsync(streamId)).Value).IsEqualTo(50)
      .Because("DOCUMENTING LOCK: the cursor-inversion detector compares LOCAL commit_sequence "
               + "only, and a backfilled event always lands with a fresh, higher one — reconcile "
               + "is invisible to the rewind, so arrival order wins and the OLDER value clobbers "
               + "the newer. A full-order replay would end at 999. The origin-aware inversion "
               + "follow-up flips this expectation to 999.");
  }

  /// <summary>
  /// The live-verified backfill shape: the stream's row was created by a later event that
  /// writes a DIFFERENT field; the backfilled initializer populates its own fields. The
  /// disjoint field lands correctly; the shared field regresses to the initializer's value —
  /// both halves of the arrival-order contract, pinned per field.
  /// </summary>
  [Test]
  public async Task Reconcile_LateInitializer_DisjointFieldLands_SharedFieldRegresses_CurrentContractAsync() {
    var streamId = Guid.NewGuid();
    var eventStore = new InMemoryEventStore();

    var newerId = Guid.Parse("019e9000-0000-7000-8000-000000000011");
    var backfilledInitId = Guid.Parse("019e8000-0000-7000-8000-000000000012");
    var stamped = new _stampingEventStore(eventStore, new Dictionary<Guid, long?> {
      [newerId] = 200,
      [backfilledInitId] = 300,
    });
    await eventStore.AppendAsync(streamId, _envelope(newerId,
      new ActionTestUpdatedEvent { StreamId = streamId, NewValue = 999 }, 200));

    await using (var ctx = CreateDbContext()) {
      var ps = new EFCorePostgresPerspectiveStore<ActionTestModel>(ctx, PERSPECTIVE);
      var runner = await CreateRunnerAsync(stamped, ps);
      _ = await runner.RunAsync(streamId, PERSPECTIVE, null, CancellationToken.None);
    }

    var initializer = _envelope(backfilledInitId,
      new ActionTestCreatedEvent { StreamId = streamId, Name = "Backfilled", Value = 7 }, 300);
    await eventStore.AppendAsync(streamId, initializer);

    await using (var ctx2 = CreateDbContext()) {
      var ps = new EFCorePostgresPerspectiveStore<ActionTestModel>(ctx2, PERSPECTIVE);
      var runner = await CreateRunnerAsync(stamped, ps);
      var result = await runner.RunWithEventsAsync(
        streamId, PERSPECTIVE, newerId, [initializer], CancellationToken.None);
      await Assert.That(result.EventsProcessed).IsEqualTo(1);
    }

    var model = await _modelAsync(streamId);
    await Assert.That(model.Name).IsEqualTo("Backfilled")
      .Because("the initializer is the ONLY writer of this field — the backfill must populate it "
               + "(this is the shape live convergence verified: fields with no competing writer land)");
    await Assert.That(model.Value).IsEqualTo(7)
      .Because("DOCUMENTING LOCK: the shared field regresses to the initializer's value under "
               + "arrival-order apply — the same missing-origin-aware-inversion gap as the "
               + "clobber lock; flips to 999 when it lands");
  }

  /// <summary>
  /// At-least-once bundle safety at the perspective layer: the SAME backfilled envelope
  /// (same event id, same local commit_sequence) redelivered a second time is a no-op —
  /// the commit-sequence idempotency floor holds for reconcile-shaped input.
  /// </summary>
  [Test]
  public async Task Reconcile_SameBundleRedeliveredTwice_SecondApplyIsNoOpAsync() {
    var streamId = Guid.NewGuid();
    var eventStore = new InMemoryEventStore();

    var createdId = Guid.Parse("019e8000-0000-7000-8000-000000000021");
    var backfilledId = Guid.Parse("019e8800-0000-7000-8000-000000000022");
    var stamped = new _stampingEventStore(eventStore, new Dictionary<Guid, long?> {
      [createdId] = 100,
      [backfilledId] = 250,
    });
    await eventStore.AppendAsync(streamId, _envelope(createdId,
      new ActionTestCreatedEvent { StreamId = streamId, Name = "Once", Value = 5 }, 100));
    await using (var ctx = CreateDbContext()) {
      var ps = new EFCorePostgresPerspectiveStore<ActionTestModel>(ctx, PERSPECTIVE);
      var runner = await CreateRunnerAsync(stamped, ps);
      _ = await runner.RunAsync(streamId, PERSPECTIVE, null, CancellationToken.None);
    }

    var bundleChild = _envelope(backfilledId,
      new ActionTestUpdatedEvent { StreamId = streamId, NewValue = 77 }, 250);
    await eventStore.AppendAsync(streamId, bundleChild);

    await using (var ctx2 = CreateDbContext()) {
      var ps = new EFCorePostgresPerspectiveStore<ActionTestModel>(ctx2, PERSPECTIVE);
      var runner = await CreateRunnerAsync(stamped, ps);
      var first = await runner.RunWithEventsAsync(
        streamId, PERSPECTIVE, createdId, [bundleChild], CancellationToken.None);
      await Assert.That(first.EventsProcessed).IsEqualTo(1);
    }

    int versionAfterFirst;
    await using (var probe = CreateDbContext()) {
      var row = await probe.Set<PerspectiveRow<ActionTestModel>>()
          .AsNoTracking().FirstAsync(r => r.Id == streamId);
      versionAfterFirst = row.Version;
      await Assert.That(row.Data.Value).IsEqualTo(77);
    }

    // The transport redelivers the SAME composite (at-least-once): identical child envelope.
    await using (var ctx3 = CreateDbContext()) {
      var ps = new EFCorePostgresPerspectiveStore<ActionTestModel>(ctx3, PERSPECTIVE);
      var runner = await CreateRunnerAsync(stamped, ps);
      var second = await runner.RunWithEventsAsync(
        streamId, PERSPECTIVE, createdId, [bundleChild], CancellationToken.None);
      await Assert.That(second.EventsProcessed).IsEqualTo(0)
        .Because("the commit-sequence floor (250 ≤ 250) catches the redelivered duplicate — "
                 + "at-least-once redelivery must not double-apply");
    }

    await using var verify = CreateDbContext();
    var finalRow = await verify.Set<PerspectiveRow<ActionTestModel>>()
        .AsNoTracking().FirstAsync(r => r.Id == streamId);
    await Assert.That(finalRow.Version).IsEqualTo(versionAfterFirst)
      .Because("no second upsert — the row is byte-stable across the duplicate");
    await Assert.That(finalRow.Data.Value).IsEqualTo(77);
  }

  /// <summary>
  /// The rewind replay must re-apply in COMMIT order, not event-id order. The inversion
  /// detector was made commit-sequence-authoritative (slices 26.13/26.18) because same-
  /// millisecond UUIDv7 ids can disagree with commit order — but the replay still sorted by
  /// message id, re-introducing exactly the ordering the detector stopped trusting. Events
  /// are crafted so id order and commit order disagree; the final model tells which one won.
  /// </summary>
  [Test]
  public async Task RewindReplay_CommitOrderDisagreesWithEventIdOrder_CommitOrderWinsAsync() {
    var streamId = Guid.NewGuid();
    var eventStore = new InMemoryEventStore();

    // Commit order: created (cs=100) THEN updated (cs=200).
    // Event-id order: updated's id sorts FIRST — an id-ordered replay applies updated
    // before created, and created's Value=1 clobbers updated's 999.
    var createdId = Guid.Parse("019eff00-0000-7000-8000-0000000000ff");   // lex-LARGE
    var updatedId = Guid.Parse("019e0000-0000-7000-8000-000000000001");   // lex-SMALL
    var stamped = new _stampingEventStore(eventStore, new Dictionary<Guid, long?> {
      [createdId] = 100,
      [updatedId] = 200,
    });
    await eventStore.AppendAsync(streamId, _envelope(createdId,
      new ActionTestCreatedEvent { StreamId = streamId, Name = "Order", Value = 1 }, 100));
    await eventStore.AppendAsync(streamId, _envelope(updatedId,
      new ActionTestUpdatedEvent { StreamId = streamId, NewValue = 999 }, 200));

    await using var ctx = CreateDbContext();
    var ps = new EFCorePostgresPerspectiveStore<ActionTestModel>(ctx, PERSPECTIVE);
    var runner = await CreateRunnerAsync(stamped, ps);

    // No snapshot exists → the rewind replays the whole stream from zero.
    var result = await runner.RewindAndRunAsync(
      streamId, PERSPECTIVE, updatedId, 200, CancellationToken.None);
    await Assert.That(result.EventsProcessed).IsEqualTo(2);

    await Assert.That((await _modelAsync(streamId)).Value).IsEqualTo(999)
      .Because("the replay must apply in COMMIT order (created cs=100 → updated cs=200) even "
               + "though the updated event's UUIDv7 sorts first — an id-ordered replay would "
               + "end at 1, silently undoing the newer write during the very rewind that "
               + "exists to fix ordering");
  }
}
