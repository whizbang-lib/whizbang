using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Registry;
using Whizbang.Sagas.Models;
using Whizbang.Sagas.Services;

namespace Whizbang.Sagas.Tests;

/// <summary>
/// The framework's own saga events are stored on the saga's stream, not each on a stream of its own.
/// </summary>
/// <remarks>
/// <para>
/// The watchdog tick, the abandon event and the continuation request each carry the saga's id as
/// <c>StreamId</c>, and their doc comments said they were bound to the saga's stream. Nothing told the
/// dispatcher so: these types live in <c>Whizbang.Sagas</c>, which no source generator runs over, so no
/// stream id extractor knew them, and the outbox fell back to the message id. Every one of them was
/// stored as a single-event stream of its own.
/// </para>
/// <para>
/// Two things depended on the stream. The stranded-saga sweep asks which sagas still have a tick
/// coming by matching outbox and inbox rows on the saga's stream, so it never saw a tick it had armed
/// itself; and a perspective could not apply the abandon event to the saga's own row.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Sagas/SagaFrameworkEventStreamIds.cs</code-under-test>
[Category("Saga")]
public class SagaFrameworkEventStreamTests {
  private static readonly Guid _sagaId = Guid.Parse("01900000-0000-7000-8000-00000000a972");
  private static readonly Guid _entityId = Guid.Parse("01900000-0000-7000-8000-00000000e972");

  private static CapturedOutboxHost _host() => CapturedOutboxHost.Create();

  [Test]
  public async Task WatchdogTick_PublishedThroughTheDispatcher_IsStoredOnTheSagasStreamAsync() {
    await using var host = _host();
    var emitter = new DispatcherSagaEventEmitter(host.Dispatcher);
    var tick = new SagaCompletionWatchdogTickEvent { StreamId = _sagaId, SagaName = ProbeSaga.SagaName, EntityId = _entityId };

    await emitter.PublishAsync(tick, DateTimeOffset.UtcNow.AddMinutes(5));

    var row = host.Outbox.Rows.Single();
    await Assert.That(row.Message.StreamId).IsEqualTo(_sagaId)
      .Because("the sweep finds a pending tick by the saga's stream; a tick on a stream of its own is invisible to it");
    await Assert.That(row.Message.StreamId).IsNotEqualTo(row.Message.MessageId);
  }

  [Test]
  public async Task AbandonEvent_PublishedThroughTheDispatcher_IsStoredOnTheSagasStreamAsync() {
    await using var host = _host();
    var emitter = new DispatcherSagaEventEmitter(host.Dispatcher);

    await emitter.PublishAsync(new SagaCompletionAbandonedEvent { StreamId = _sagaId, SagaName = ProbeSaga.SagaName, EntityId = _entityId });

    await Assert.That(host.Outbox.Rows.Single().Message.StreamId).IsEqualTo(_sagaId)
      .Because("a perspective can only apply the abandonment to the saga's row when the event is on the saga's stream");
  }

  [Test]
  public async Task ContinuationRequest_PublishedThroughTheDispatcher_IsStoredOnTheFinishedSagasStreamAsync() {
    await using var host = _host();
    var emitter = new DispatcherSagaEventEmitter(host.Dispatcher);

    await emitter.PublishAsync(new SagaContinuationRequestedEvent {
      StreamId = _sagaId,
      SagaName = "NextSaga",
      EntityId = _entityId,
      ParentSagaName = ProbeSaga.SagaName,
      ParentSagaId = _sagaId,
    });

    await Assert.That(host.Outbox.Rows.Single().Message.StreamId).IsEqualTo(_sagaId);
  }

  /// <summary>
  /// The extractor answers for the three framework events and every saga stream event, and for nothing
  /// else, so it cannot claim a consumer's own event from the extractor generated for it.
  /// </summary>
  [Test]
  public async Task Registry_ResolvesTheFrameworkEventsStream_AndLeavesOtherMessagesAloneAsync() {
    var tick = new SagaCompletionWatchdogTickEvent { StreamId = _sagaId };
    var abandoned = new SagaCompletionAbandonedEvent { StreamId = _sagaId };
    var continuation = new SagaContinuationRequestedEvent { StreamId = _sagaId };
    var extractor = new SagaFrameworkEventStreamIds();

    await Assert.That(StreamIdExtractorRegistry.ExtractStreamId(tick, tick.GetType())).IsEqualTo(_sagaId)
      .Because("the extractor is registered when the saga assembly loads, with no call from the host");
    await Assert.That(extractor.ExtractStreamId(abandoned, abandoned.GetType())).IsEqualTo(_sagaId);
    await Assert.That(extractor.ExtractStreamId(continuation, continuation.GetType())).IsEqualTo(_sagaId);
    await Assert.That(extractor.ExtractStreamId(new ProbeSaga.InitiatedEvent { SagaId = _sagaId }, typeof(ProbeSaga.InitiatedEvent))).IsEqualTo(_sagaId)
      .Because("a generated saga event is a saga stream event, keyed by its saga id");
    await Assert.That(extractor.ExtractStreamId(new NotASagaEvent(), typeof(NotASagaEvent))).IsNull();
  }

  /// <summary>
  /// A tick with no stream of its own takes the one it is cascaded from, as any event with a stream id
  /// does; anything else is not the extractor's to set.
  /// </summary>
  [Test]
  public async Task SetStreamId_SetsTheFrameworkEventsStream_AndDeclinesOtherMessagesAsync() {
    var extractor = new SagaFrameworkEventStreamIds();
    var tick = new SagaCompletionWatchdogTickEvent();
    var abandoned = new SagaCompletionAbandonedEvent();
    var continuation = new SagaContinuationRequestedEvent();

    await Assert.That(extractor.SetStreamId(tick, _sagaId)).IsTrue();
    await Assert.That(extractor.SetStreamId(abandoned, _sagaId)).IsTrue();
    await Assert.That(extractor.SetStreamId(continuation, _sagaId)).IsTrue();
    var generated = new ProbeSaga.InitiatedEvent();
    await Assert.That(extractor.SetStreamId(generated, _sagaId)).IsTrue();
    await Assert.That(generated.SagaId).IsEqualTo(_sagaId);
    await Assert.That(extractor.SetStreamId(new NotASagaEvent(), _sagaId)).IsFalse();
    await Assert.That(tick.StreamId).IsEqualTo(_sagaId);
    await Assert.That(abandoned.StreamId).IsEqualTo(_sagaId);
    await Assert.That(continuation.StreamId).IsEqualTo(_sagaId);
  }

  /// <summary>
  /// The sweep sees the tick it armed itself, however many intervals pass while it waits in the outbox.
  /// </summary>
  /// <remarks>
  /// The wake lookup here answers from the rows the dispatcher stored, matched on stream and message
  /// type as the work coordinator's query matches them (that query has its own tests). Before the fix
  /// the row's stream was its message id, the lookup reported no tick coming, and the next interval's
  /// claim key let the sweep arm a second tick beside the first.
  /// </remarks>
  [Test]
  public async Task Sweep_ItsOwnTickStillInTheOutbox_ArmsNoSecondTickInALaterIntervalAsync() {
    await using var host = _host();
    var clock = new MovableClock(DateTimeOffset.UtcNow);
    var options = new SagaOptions { TimeProvider = clock };
    var saga = new BaseSagaModel {
      Id = _sagaId,
      SagaName = ProbeSaga.SagaName,
      EntityId = _entityId,
      TotalItems = 3,
      Status = SagaStatus.Running,
      UpdatedAt = clock.Now - TimeSpan.FromHours(2),
    };
    var svc = new ProbeSagaService(new DispatcherSagaEventEmitter(host.Dispatcher), [new IncompleteSaga(saga, TenantId: null)], options);
    var wakes = new OutboxWakes(host.Outbox);

    var first = await svc.ArmStrandedSagasAsync(wakes, CancellationToken.None);
    clock.Now += options.StrandedSagaRearmInterval * 3;
    var later = await svc.ArmStrandedSagasAsync(wakes, CancellationToken.None);

    await Assert.That(first).IsEqualTo(1);
    await Assert.That(later).IsEqualTo(0)
      .Because("the tick the sweep armed is still waiting; a second one would start a second chain beside it");
    await Assert.That(host.Outbox.Rows.Count(r => r.Payload is SagaCompletionWatchdogTickEvent)).IsEqualTo(1);
  }

  /// <summary>Which of the asked sagas have an unpublished tick among the stored rows.</summary>
  private sealed class OutboxWakes(CapturedOutboxHost.CapturingStrategy outbox) : ISagaWakeLookup {
    private static readonly string _tickType =
      EventTypeMatchingHelper.NormalizeTypeName(TypeNameFormatter.AssemblyQualifiedName(typeof(SagaCompletionWatchdogTickEvent)));

    public Task<IReadOnlySet<Guid>?> WithPendingWakeAsync(IReadOnlyList<Guid> sagaIds, CancellationToken cancellationToken) {
      IReadOnlySet<Guid> pending = outbox.Rows
        .Where(r => EventTypeMatchingHelper.NormalizeTypeName(r.Message.MessageType) == _tickType)
        .Select(r => r.Message.StreamId ?? Guid.Empty)
        .Where(sagaIds.Contains)
        .ToHashSet();
      return Task.FromResult<IReadOnlySet<Guid>?>(pending);
    }
  }

  private sealed class MovableClock(DateTimeOffset start) : TimeProvider {
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
  }

  /// <summary>An event that belongs to no saga.</summary>
  public sealed class NotASagaEvent : IEvent {
    [StreamId] public Guid Id { get; set; }
  }
}
