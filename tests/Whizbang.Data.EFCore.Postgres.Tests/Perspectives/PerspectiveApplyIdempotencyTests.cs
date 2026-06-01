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
/// Idempotency guard regressions for the generated <c>ActionTestPerspectiveRunner</c>.
/// <para>
/// The work-pump architecture commits the perspective row (<see cref="EFCorePostgresPerspectiveStore{TModel}"/>)
/// in one transaction and advances the cursor (<c>PerspectiveCompletionFlushWorker</c>) in another.
/// If a worker dies between those commits, the same events get re-claimed and the runner is invoked
/// again with <c>lastProcessedEventId = previous cursor</c> — which can be stale relative to the row.
/// </para>
/// <para>
/// The fix: the runner persists <see cref="PerspectiveMetadata.EventId"/> alongside the row, then on
/// the next run reads it back via <see cref="IPerspectiveStore{TModel}.GetMetadataByStreamIdAsync"/>
/// and skips events whose id is ≤ the persisted value (UUIDv7 lex compare = time order). Replays of
/// already-applied events become no-ops, which is what these tests pin down.
/// </para>
/// </summary>
public class PerspectiveApplyIdempotencyTests : EFCoreTestBase {

  private static async Task<IPerspectiveRunner> CreateRunnerAsync(
      IEventStore eventStore,
      EFCorePostgresPerspectiveStore<ActionTestModel> perspectiveStore) {

    var services = new ServiceCollection();
    services.AddTransient<ActionTestPerspective>();
    services.AddLogging();
    var sp = services.BuildServiceProvider();

    await Task.CompletedTask;

    var runnerType = typeof(PerspectiveApplyIdempotencyTests).Assembly.GetTypes()
        .Single(t => t.Name == "ActionTestPerspectiveRunner");

    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var createLoggerMethod = typeof(LoggerFactoryExtensions)
        .GetMethods()
        .Single(m => m.Name == "CreateLogger" && m.IsGenericMethod)
        .MakeGenericMethod(runnerType);
    var logger = createLoggerMethod.Invoke(null, [loggerFactory])!;

    var ctor = runnerType.GetConstructors().Single();
    return (IPerspectiveRunner)ctor.Invoke([
      sp,
      logger,
      (IEventStore)eventStore,
      (IPerspectiveStore<ActionTestModel>)perspectiveStore,
      sp.GetRequiredService<IServiceScopeFactory>(),
      null, // tracingOptions
      null, // snapshotStore
      null, // snapshotOptions
      null  // applyCoordinator
    ]);
  }

  private static async Task<MessageId> AppendEventAsync<TEvent>(
      InMemoryEventStore eventStore, Guid streamId, TEvent payload)
      where TEvent : IEvent {
    var envelope = new MessageEnvelope<TEvent> {
      MessageId = MessageId.New(),
      Payload = payload,
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      Hops = []
    };
    await eventStore.AppendAsync(streamId, envelope);
    return envelope.MessageId;
  }

  /// <summary>
  /// Persists EventId in metadata so a subsequent run can detect "already applied".
  /// </summary>
  [Test]
  public async Task RunAsync_PersistsLastAppliedEventIdInMetadataAsync() {
    var streamId = Guid.NewGuid();
    var eventStore = new InMemoryEventStore();
    var createdId = await AppendEventAsync(eventStore, streamId, new ActionTestCreatedEvent {
      StreamId = streamId,
      Name = "First",
      Value = 1
    });

    await using var storeContext = CreateDbContext();
    var perspectiveStore = new EFCorePostgresPerspectiveStore<ActionTestModel>(storeContext, "action_test");
    var runner = await CreateRunnerAsync(eventStore, perspectiveStore);

    var result = await runner.RunAsync(streamId, "action_test", null, CancellationToken.None);

    await Assert.That(result.EventsProcessed).IsEqualTo(1);
    await Assert.That(result.LastEventId).IsEqualTo(createdId.Value);

    await using var verifyContext = CreateDbContext();
    var verifyStore = new EFCorePostgresPerspectiveStore<ActionTestModel>(verifyContext, "action_test");
    var metadata = await verifyStore.GetMetadataByStreamIdAsync(streamId);

    await Assert.That(metadata).IsNotNull();
    await Assert.That(metadata!.EventId).IsEqualTo(createdId.Value.ToString("D"));
    await Assert.That(metadata.EventType).Contains(nameof(ActionTestCreatedEvent));
  }

  /// <summary>
  /// Simulates the worker-crash race: cursor not advanced, runner re-invoked with the
  /// same events. The idempotency filter must catch all of them so no second upsert
  /// occurs (Version and UpdatedAt unchanged).
  /// </summary>
  [Test]
  public async Task RunAsync_RerunWithSameEvents_SkipsAllAndDoesNotReWriteAsync() {
    var streamId = Guid.NewGuid();
    var eventStore = new InMemoryEventStore();
    await AppendEventAsync(eventStore, streamId, new ActionTestCreatedEvent {
      StreamId = streamId,
      Name = "Idempotent",
      Value = 42
    });

    await using (var ctx1 = CreateDbContext()) {
      var ps1 = new EFCorePostgresPerspectiveStore<ActionTestModel>(ctx1, "action_test");
      var runner1 = await CreateRunnerAsync(eventStore, ps1);
      var first = await runner1.RunAsync(streamId, "action_test", null, CancellationToken.None);
      await Assert.That(first.EventsProcessed).IsEqualTo(1);
    }

    int versionAfterFirst;
    DateTime updatedAtAfterFirst;
    await using (var probe = CreateDbContext()) {
      var row = await probe.Set<PerspectiveRow<ActionTestModel>>()
          .AsNoTracking()
          .FirstAsync(r => r.Id == streamId);
      versionAfterFirst = row.Version;
      updatedAtAfterFirst = row.UpdatedAt;
    }

    // Re-run with cursor=null — exactly what happens after a worker dies between row
    // upsert and cursor advance: the cursor still points at the previous checkpoint, so
    // claim_orphaned_perspective_events re-claims the same events and we get invoked
    // with lastProcessedEventId stale (or null on a fresh stream).
    await using (var ctx2 = CreateDbContext()) {
      var ps2 = new EFCorePostgresPerspectiveStore<ActionTestModel>(ctx2, "action_test");
      var runner2 = await CreateRunnerAsync(eventStore, ps2);
      var second = await runner2.RunAsync(streamId, "action_test", null, CancellationToken.None);

      await Assert.That(second.EventsProcessed).IsEqualTo(0);
      // Status MUST be Completed even though zero events were applied — otherwise the worker
      // skips _enqueueDrainModePerspectiveCompletions and the wh_perspective_events rows
      // never get deleted. claim_orphaned_perspective_events would then re-claim them every
      // safety-net interval forever (the JDNext "Skipped 1 already-applied" hot loop).
      await Assert.That(second.Status).IsEqualTo(PerspectiveProcessingStatus.Completed);
      await Assert.That(second.ProcessedEventIds).IsNotEmpty();
    }

    await using var verify = CreateDbContext();
    var finalRow = await verify.Set<PerspectiveRow<ActionTestModel>>()
        .AsNoTracking()
        .FirstAsync(r => r.Id == streamId);

    await Assert.That(finalRow.Version).IsEqualTo(versionAfterFirst);
    await Assert.That(finalRow.UpdatedAt).IsEqualTo(updatedAtAfterFirst);
    await Assert.That(finalRow.Data.Name).IsEqualTo("Idempotent");
    await Assert.That(finalRow.Data.Value).IsEqualTo(42);
  }

  /// <summary>
  /// Mixed batch: a re-claim that contains BOTH already-applied events AND new ones
  /// must apply only the new ones. This is the realistic JDNext shape — first refresh
  /// claims events 1-3, dies; second refresh claims 1-3 again plus a new event 4.
  /// </summary>
  [Test]
  public async Task RunAsync_RerunWithMixOfAppliedAndNewEvents_AppliesOnlyNewAsync() {
    var streamId = Guid.NewGuid();
    var eventStore = new InMemoryEventStore();
    await AppendEventAsync(eventStore, streamId, new ActionTestCreatedEvent {
      StreamId = streamId,
      Name = "Created",
      Value = 1
    });

    await using (var ctx1 = CreateDbContext()) {
      var ps1 = new EFCorePostgresPerspectiveStore<ActionTestModel>(ctx1, "action_test");
      var runner1 = await CreateRunnerAsync(eventStore, ps1);
      var first = await runner1.RunAsync(streamId, "action_test", null, CancellationToken.None);
      await Assert.That(first.EventsProcessed).IsEqualTo(1);
    }

    // Append a new event
    await AppendEventAsync(eventStore, streamId, new ActionTestUpdatedEvent {
      StreamId = streamId,
      NewValue = 99
    });

    // Re-run with cursor=null — claims both old + new event. Only the new one should apply.
    await using (var ctx2 = CreateDbContext()) {
      var ps2 = new EFCorePostgresPerspectiveStore<ActionTestModel>(ctx2, "action_test");
      var runner2 = await CreateRunnerAsync(eventStore, ps2);
      var second = await runner2.RunAsync(streamId, "action_test", null, CancellationToken.None);
      await Assert.That(second.EventsProcessed).IsEqualTo(1);
    }

    await using var verify = CreateDbContext();
    var row = await verify.Set<PerspectiveRow<ActionTestModel>>()
        .AsNoTracking()
        .FirstAsync(r => r.Id == streamId);

    await Assert.That(row.Data.Name).IsEqualTo("Created");
    await Assert.That(row.Data.Value).IsEqualTo(99);
  }

  /// <summary>
  /// Fresh stream — no row, no metadata — must apply every event normally. The filter
  /// path must be a no-op when no metadata exists. This is the "first ever run" check
  /// that guards against the filter accidentally suppressing first-time event delivery.
  /// </summary>
  [Test]
  public async Task RunAsync_FreshStream_NoMetadataMeansAllEventsAppliedAsync() {
    var streamId = Guid.NewGuid();
    var eventStore = new InMemoryEventStore();
    await AppendEventAsync(eventStore, streamId, new ActionTestCreatedEvent {
      StreamId = streamId,
      Name = "FirstRun",
      Value = 7
    });
    await AppendEventAsync(eventStore, streamId, new ActionTestUpdatedEvent {
      StreamId = streamId,
      NewValue = 700
    });

    await using var storeContext = CreateDbContext();
    var perspectiveStore = new EFCorePostgresPerspectiveStore<ActionTestModel>(storeContext, "action_test");
    var runner = await CreateRunnerAsync(eventStore, perspectiveStore);

    var result = await runner.RunAsync(streamId, "action_test", null, CancellationToken.None);

    await Assert.That(result.EventsProcessed).IsEqualTo(2);

    await using var verify = CreateDbContext();
    var row = await verify.Set<PerspectiveRow<ActionTestModel>>()
        .AsNoTracking()
        .FirstAsync(r => r.Id == streamId);
    await Assert.That(row.Data.Value).IsEqualTo(700);
  }

  /// <summary>
  /// Slot-3 regression (G2): the runner must persist <see cref="PerspectiveMetadata.CommitSequence"/>
  /// alongside <see cref="PerspectiveMetadata.EventId"/>. Without this, the idempotency filter has
  /// no commit-sequence floor to compare incoming events against — late-delivered events whose
  /// event_id sorts lex-smaller (UUIDv7 inversion) but whose commit_sequence is larger get
  /// silently dropped.
  /// </summary>
  [Test]
  public async Task RunAsync_PersistsLastAppliedCommitSequenceInMetadataAsync() {
    var streamId = Guid.NewGuid();
    var innerStore = new InMemoryEventStore();
    var stampingMap = new Dictionary<Guid, long?>();
    var stampingStore = new _CommitSequenceStampingEventStore(innerStore, stampingMap);

    var createdId = await AppendEventAsync(innerStore, streamId, new ActionTestCreatedEvent {
      StreamId = streamId,
      Name = "WithCommitSeq",
      Value = 1
    });
    const long expectedCommitSequence = 234198L;
    stampingMap[createdId.Value] = expectedCommitSequence;

    await using var storeContext = CreateDbContext();
    var perspectiveStore = new EFCorePostgresPerspectiveStore<ActionTestModel>(storeContext, "action_test");
    var runner = await CreateRunnerAsync(stampingStore, perspectiveStore);

    var result = await runner.RunAsync(streamId, "action_test", null, CancellationToken.None);

    await Assert.That(result.EventsProcessed).IsEqualTo(1);

    await using var verifyContext = CreateDbContext();
    var verifyStore = new EFCorePostgresPerspectiveStore<ActionTestModel>(verifyContext, "action_test");
    var metadata = await verifyStore.GetMetadataByStreamIdAsync(streamId);

    await Assert.That(metadata).IsNotNull();
    await Assert.That(metadata!.CommitSequence)
      .IsEqualTo(expectedCommitSequence)
      .Because("runner must stamp wh_event_store.commit_sequence onto metadata so the idempotency filter has a commit_sequence floor");
  }

  /// <summary>
  /// <see cref="IPerspectiveStore{TModel}.GetMetadataByStreamIdAsync"/> on a non-existent stream
  /// returns null — required so the runner's filter path knows it has never run for this stream
  /// and applies every event.
  /// </summary>
  [Test]
  public async Task GetMetadataByStreamIdAsync_WhenRowDoesNotExist_ReturnsNullAsync() {
    await using var storeContext = CreateDbContext();
    var perspectiveStore = new EFCorePostgresPerspectiveStore<ActionTestModel>(storeContext, "action_test");

    var metadata = await perspectiveStore.GetMetadataByStreamIdAsync(Guid.NewGuid());

    await Assert.That(metadata).IsNull();
  }

  /// <summary>
  /// Test wrapper: delegates to an inner <see cref="InMemoryEventStore"/> for everything, but
  /// returns canned <c>commit_sequence</c> values from an external map for
  /// <see cref="IEventStore.GetCommitSequenceAsync"/>. Lets the test prove the runner
  /// reads the value and threads it onto persisted metadata.
  /// </summary>
  private sealed class _CommitSequenceStampingEventStore(
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
    public IAsyncEnumerable<MessageEnvelope<IEvent>> ReadPolymorphicAsync(Guid streamId, Guid? fromEventId, IReadOnlyList<Type> eventTypes, CancellationToken ct = default) =>
      inner.ReadPolymorphicAsync(streamId, fromEventId, eventTypes, ct);
    public Task<List<MessageEnvelope<TMessage>>> GetEventsBetweenAsync<TMessage>(Guid streamId, Guid? afterEventId, Guid upToEventId, CancellationToken ct = default) =>
      inner.GetEventsBetweenAsync<TMessage>(streamId, afterEventId, upToEventId, ct);
    public Task<List<MessageEnvelope<IEvent>>> GetEventsBetweenPolymorphicAsync(Guid streamId, Guid? afterEventId, Guid upToEventId, IReadOnlyList<Type> eventTypes, CancellationToken ct = default) =>
      inner.GetEventsBetweenPolymorphicAsync(streamId, afterEventId, upToEventId, eventTypes, ct);
    public Task<long> GetLastSequenceAsync(Guid streamId, CancellationToken ct = default) =>
      inner.GetLastSequenceAsync(streamId, ct);
    public List<MessageEnvelope<IEvent>> DeserializeStreamEvents(IReadOnlyList<StreamEventData> streamEvents, IReadOnlyList<Type> eventTypes) =>
      inner.DeserializeStreamEvents(streamEvents, eventTypes);
  }
}
