using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Routing;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.Transports;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Locks the invariant that <see cref="IDispatcher.PublishAsync{TEvent}(TEvent, DispatchOptions)"/>
/// gates the in-process local-receptor invocation on <see cref="DispatchOptions.ScheduledFor"/>.
///
/// <para>Production incident (production saga <c>a-production-saga</c>, 2026-06-25):
/// when the saga watchdog re-armed via <c>_emitter.PublishAsync(nextTick, NOW + 30s)</c>, the
/// outbox row's <c>scheduled_for</c> column was correctly stamped, but the dispatcher also
/// invoked the local <c>SagaCompletionWatchdogTickEvent</c> receptor synchronously on the same
/// call. That receptor immediately re-armed again, which fired again, which re-armed again —
/// 5 ticks plus an <c>SagaCompletionAbandonedEvent</c> all landed in <c>wh_event_store</c>
/// within 86 ms of saga initiation, abandoning the saga before any item could complete.</para>
///
/// <para>The fix is that <c>ScheduledFor</c> must gate <b>both</b> the outbox row and the local
/// receptor: a future <c>ScheduledFor</c> means "this message is not yet visible to anyone." The
/// local receptor must wait for the same outbox-pickup gate (mig 040) as the cross-pod transport
/// path so re-arm bounces remain bounded by wall-clock time, not by the dispatcher's internal
/// concurrency.</para>
/// </summary>
[Category("Dispatcher")]
[Category("ScheduledFor")]
[NotInParallel]
public class DispatcherScheduledForLocalReceptorTests {

  // Test event with a registered receptor (see ScheduledForCascadeProbeReceptor below).
  // The receptor records every invocation in a static counter so tests can assert whether
  // it ran inline on PublishAsync or correctly waited for the scheduled wall-clock time.
  public record ScheduledForCascadeProbeEvent([property: StreamId] Guid StreamId) : IEvent;

  // Pure observer: counts invocations, does not re-arm or publish further. The cascade we
  // are guarding against would manifest as a single inline invocation here when ScheduledFor
  // is set in the future — that is the bug.
  public class ScheduledForCascadeProbeReceptor : IReceptor<ScheduledForCascadeProbeEvent> {
    private static int _invocationCount;
    public static int InvocationCount => Volatile.Read(ref _invocationCount);
    public static void ResetCounter() => Interlocked.Exchange(ref _invocationCount, 0);
    public ValueTask HandleAsync(ScheduledForCascadeProbeEvent message, CancellationToken cancellationToken = default) {
      Interlocked.Increment(ref _invocationCount);
      return ValueTask.CompletedTask;
    }
  }

  [Before(Test)]
  public void Reset() => ScheduledForCascadeProbeReceptor.ResetCounter();

  [Test]
  public async Task PublishAsync_WithScheduledForInFuture_DoesNotInvokeLocalReceptorInlineAsync() {
    var strategy = new StubWorkCoordinatorStrategy();
    var dispatcher = _createDispatcherWithStrategy(strategy);
    var @event = new ScheduledForCascadeProbeEvent(Guid.NewGuid());
    var fireAt = DateTimeOffset.UtcNow.AddMinutes(5);

    await dispatcher.PublishAsync(@event, new DispatchOptions().WithScheduledFor(fireAt));

    // The outbox row must still be queued (this is how the message will eventually be delivered
    // when the scheduled_for time elapses — wh_outbox mig 040 pickup query gates on it).
    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1)
      .Because("ScheduledFor in the future still needs an outbox row — that is what delivers " +
               "the message later. The bug isn't that the outbox doesn't get the row; it's that " +
               "the local receptor also runs inline.");
    await Assert.That(strategy.QueuedOutboxMessages[0].ScheduledFor).IsEqualTo((DateTimeOffset?)fireAt);

    // THE INVARIANT under test: the local receptor MUST NOT fire inline when ScheduledFor is in
    // the future. The pickup query honors scheduled_for for the cross-pod path; the local path
    // must honor it too, otherwise watchdog re-arm cascades to abandon in milliseconds — the
    // production 019f000e regression.
    await Assert.That(ScheduledForCascadeProbeReceptor.InvocationCount).IsEqualTo(0)
      .Because("DispatchOptions.ScheduledFor must gate the in-process local-receptor invocation " +
               "the same way it gates the outbox pickup. Inline invocation defeats the entire " +
               "scheduling contract — observed in production as the production saga 019f000e " +
               "watchdog cascading 5 ticks + Abandoned within 86 ms of saga init.");
  }

  [Test]
  public async Task PublishAsync_WithoutScheduledFor_InvokesLocalReceptorInlineAsync() {
    var strategy = new StubWorkCoordinatorStrategy();
    var dispatcher = _createDispatcherWithStrategy(strategy);
    var @event = new ScheduledForCascadeProbeEvent(Guid.NewGuid());

    await dispatcher.PublishAsync(@event, new DispatchOptions());

    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(strategy.QueuedOutboxMessages[0].ScheduledFor).IsNull();
    await Assert.That(ScheduledForCascadeProbeReceptor.InvocationCount).IsEqualTo(1)
      .Because("Without ScheduledFor, immediate dispatch semantics are unchanged: local receptors " +
               "must still fire inline. This anchors the negative direction of the fix so a future " +
               "over-correction doesn't accidentally suppress immediate local dispatch.");
  }

  [Test]
  public async Task PublishAsync_WithScheduledForInPast_InvokesLocalReceptorInlineAsync() {
    var strategy = new StubWorkCoordinatorStrategy();
    var dispatcher = _createDispatcherWithStrategy(strategy);
    var @event = new ScheduledForCascadeProbeEvent(Guid.NewGuid());
    var fireAt = DateTimeOffset.UtcNow.AddSeconds(-30);

    await dispatcher.PublishAsync(@event, new DispatchOptions().WithScheduledFor(fireAt));

    await Assert.That(strategy.QueuedOutboxMessages).Count().IsEqualTo(1);
    await Assert.That(strategy.QueuedOutboxMessages[0].ScheduledFor).IsEqualTo((DateTimeOffset?)fireAt);
    await Assert.That(ScheduledForCascadeProbeReceptor.InvocationCount).IsEqualTo(1)
      .Because("ScheduledFor in the past means 'should have already fired' — the local receptor " +
               "must run inline, matching how mig 040's pickup query treats past scheduled_for " +
               "as immediately publishable. Without this branch the fix would silently drop " +
               "stale-but-still-valid messages on the floor.");
  }

  // ─── stubs (shape mirrors DispatcherOutboxTests) ────────────────────────────────────────

  private sealed class StubWorkCoordinatorStrategy : IWorkCoordinatorStrategy {
    public List<OutboxMessage> QueuedOutboxMessages { get; } = [];
    public void QueueOutboxMessage(OutboxMessage message) => QueuedOutboxMessages.Add(message);
    public void QueueInboxMessage(InboxMessage message) { }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus completedStatus) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus completedStatus, string errorMessage) { }
    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) =>
      Task.FromResult(new WorkBatch { OutboxWork = [], InboxWork = [], PerspectiveWork = [] });
  }

  private sealed class StubEnvelopeSerializer : IEnvelopeSerializer {
    public SerializedEnvelope SerializeEnvelope<TMessage>(IMessageEnvelope<TMessage> envelope) {
      var jsonElement = System.Text.Json.JsonSerializer.SerializeToElement(new { });
      var jsonEnvelope = new MessageEnvelope<System.Text.Json.JsonElement> {
        MessageId = envelope.MessageId,
        Payload = jsonElement,
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
      };
      return new SerializedEnvelope(
        jsonEnvelope,
        typeof(MessageEnvelope<>).MakeGenericType(typeof(TMessage)).AssemblyQualifiedName!,
        typeof(TMessage).AssemblyQualifiedName!);
    }
    public object DeserializeMessage(MessageEnvelope<System.Text.Json.JsonElement> jsonEnvelope, string messageTypeName) =>
      throw new NotImplementedException();
  }

  private static IDispatcher _createDispatcherWithStrategy(IWorkCoordinatorStrategy strategy) {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(configuration: null));
    services.AddSingleton<IEnvelopeSerializer, StubEnvelopeSerializer>();
    services.AddScoped<IWorkCoordinatorStrategy>(_ => strategy);
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider().GetRequiredService<IDispatcher>();
  }
}
