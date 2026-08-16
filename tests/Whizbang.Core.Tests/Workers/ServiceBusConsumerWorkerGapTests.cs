using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Security;
using Whizbang.Core.Transports;
using Whizbang.Core.Validation;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>Test event for the ServiceBusConsumerWorker gap-coverage suite — top-level so the
/// JsonSerializerContext source generator can fill in its abstract members.</summary>
public record SbcGapTestEvent : IEvent {
  [StreamId]
  public string Data { get; init; } = string.Empty;
}

/// <summary>JsonSerializerContext for the gap-coverage suite's strongly-typed envelope tests.</summary>
[JsonSerializable(typeof(SbcGapTestEvent))]
[JsonSerializable(typeof(MessageEnvelope<SbcGapTestEvent>))]
[JsonSerializable(typeof(EnvelopeMetadata))]
internal sealed partial class SbcGapJsonContext : JsonSerializerContext {
}

/// <summary>
/// Gap-coverage tests for ServiceBusConsumerWorker targeting branches missed by the existing
/// ServiceBusConsumerWorker* suites:
/// - Disabled concurrency semaphore (MessageProcessingOptions.MaxConcurrentMessages = 0)
/// - Explicit positive MaxConcurrentMessages (non-default semaphore construction branch)
/// - IEnvelopeSerializer resolved from the scoped provider (not constructor-injected)
/// - IEnvelopeSerializer missing everywhere → InvalidOperationException
/// - MessageEnvelope&lt;object&gt; carrying a JsonElement payload → double-serialization guard
/// - IEventTypeProvider branch of the isEvent determination (provider registered)
/// - StreamIdGuard.ThrowIfEmpty firing for events with Guid.Empty stream id
/// - _extractStreamId fallbacks: non-GUID string AggregateId and non-string ValueKind
/// - _startInboxActivity with a malformed (unparseable) TraceParent
/// - Empty transport batch (pending.Count == 0 short-circuit)
/// - Detached lifecycle stage receptor throwing → error contained, message still succeeds
/// - Drop gate skipped when the envelope type has no [[...]] inner type (parse miss must not drop)
/// - PreInbox gate 'continue' when neither compile-time nor runtime registry has receptors
/// - _isEventWithoutPerspectives with registered but NON-matching perspectives → PostLifecycle fires
/// </summary>
[Category("Workers")]
[NotInParallel("WhizbangBackgroundServiceTests")]
public class ServiceBusConsumerWorkerGapTests {

  private const string JSON_ENVELOPE_TYPE =
    "MessageEnvelope`1[[Whizbang.Core.Tests.Workers.SbcGapTestEvent, Whizbang.Core.Tests]], Whizbang.Core";

  // ========================================
  // Concurrency semaphore branches
  // ========================================

  /// <summary>
  /// Covers the MaxConcurrentMessages = 0 branch: the ternary in the _concurrencySemaphore
  /// field initializer takes the null arm, so _handleMessageAsync skips WaitAsync and the
  /// finally block's null-conditional Release is a no-op. The message must still flow
  /// through dedup (one flush) with no semaphore in play.
  /// </summary>
  [Test]
  public async Task HandleMessage_ConcurrencyLimitDisabled_ProcessesWithoutSemaphoreAsync() {
    var transport = new GapTransport();
    var strategy = new GapStrategy(() => new WorkBatch { InboxWork = [], OutboxWork = [], PerspectiveWork = [] });
    var worker = _createWorker(
      transport, strategy,
      messageProcessingOptions: new MessageProcessingOptions { MaxConcurrentMessages = 0 });

    await worker.StartAsync(CancellationToken.None);
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(5));

    var messageId = MessageId.New();
    await transport.CapturedBatchHandler!(
      [new TransportMessage(_createJsonEnvelope(messageId, Guid.NewGuid()), JSON_ENVELOPE_TYPE)],
      CancellationToken.None);

    await Assert.That(strategy.FlushCallCount).IsEqualTo(1);

    await worker.StopAsync(CancellationToken.None);
  }

  // ========================================
  // _serializeToNewInboxMessage serializer resolution branches
  // ========================================

  /// <summary>
  /// Covers the strongly-typed envelope path where _envelopeSerializer is null and the
  /// serializer is resolved from the scoped provider (scopeServiceProvider.GetService branch).
  /// Also covers the explicit non-null MessageProcessingOptions with a positive limit,
  /// exercising the semaphore construction from configured (non-default) options.
  /// </summary>
  [Test]
  public async Task HandleMessage_StronglyTypedEnvelope_ResolvesEnvelopeSerializerFromScopeAsync() {
    var transport = new GapTransport();
    var strategy = new GapStrategy(() => new WorkBatch { InboxWork = [], OutboxWork = [], PerspectiveWork = [] });
    var jsonOptions = new JsonSerializerOptions { TypeInfoResolver = SbcGapJsonContext.Default };
    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity(options => { options.AllowAnonymous = true; });
    services.AddSingleton<IWorkCoordinatorStrategy>(strategy);
    services.AddSingleton<IEnvelopeSerializer>(new EnvelopeSerializer(jsonOptions));

    var worker = _createWorker(
      transport, strategy, services,
      messageProcessingOptions: new MessageProcessingOptions { MaxConcurrentMessages = 2 });

    await worker.StartAsync(CancellationToken.None);
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(5));

    var envelope = _createTypedEnvelope(MessageId.New());
    var envelopeType = typeof(MessageEnvelope<SbcGapTestEvent>).AssemblyQualifiedName!;

    await transport.CapturedBatchHandler!(
      [new TransportMessage(envelope, envelopeType)],
      CancellationToken.None);

    // The scoped serializer converted the strongly-typed payload into JsonElement form.
    await Assert.That(strategy.CapturedInboxMessages.Count).IsEqualTo(1);
    var captured = strategy.CapturedInboxMessages[0];
    await Assert.That(captured.EnvelopeType).IsEqualTo(envelopeType);
    await Assert.That(captured.Envelope.Payload.ValueKind).IsEqualTo(JsonValueKind.Object);

    await worker.StopAsync(CancellationToken.None);
  }

  /// <summary>
  /// Covers the throw branch when a strongly-typed envelope arrives but IEnvelopeSerializer
  /// is neither constructor-injected nor registered in the scoped container: the worker must
  /// fail loudly with InvalidOperationException instead of storing an unserializable envelope.
  /// </summary>
  [Test]
  public async Task HandleMessage_StronglyTypedEnvelope_NoSerializerAnywhere_ThrowsAsync() {
    var transport = new GapTransport();
    var strategy = new GapStrategy(() => new WorkBatch { InboxWork = [], OutboxWork = [], PerspectiveWork = [] });
    // AllowAnonymous so the strongly-typed (non-JsonElement) payload clears the security
    // gate and reaches the serializer-resolution branch under test. No IEnvelopeSerializer
    // is registered anywhere, so _serializeToNewInboxMessage must throw InvalidOperationException.
    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity(options => { options.AllowAnonymous = true; });
    services.AddSingleton<IWorkCoordinatorStrategy>(strategy);
    var worker = _createWorker(transport, strategy, services);

    await worker.StartAsync(CancellationToken.None);
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(5));

    var envelope = _createTypedEnvelope(MessageId.New());
    var envelopeType = typeof(MessageEnvelope<SbcGapTestEvent>).AssemblyQualifiedName!;

    await Assert.That(async () =>
      await transport.CapturedBatchHandler!(
        [new TransportMessage(envelope, envelopeType)],
        CancellationToken.None)
    ).Throws<InvalidOperationException>();

    // Nothing may reach the storage path when serialization is impossible.
    await Assert.That(strategy.CapturedInboxMessages.Count).IsEqualTo(0);

    await worker.StopAsync(CancellationToken.None);
  }

  /// <summary>
  /// Covers the defensive double-serialization guard: an envelope typed as
  /// MessageEnvelope&lt;object&gt; whose payload is a boxed JsonElement fails the
  /// IMessageEnvelope&lt;JsonElement&gt; pattern match but hits payloadType == typeof(JsonElement),
  /// which must throw InvalidOperationException rather than silently re-serialize.
  /// </summary>
  [Test]
  public async Task HandleMessage_ObjectEnvelopeWithJsonElementPayload_ThrowsDoubleSerializationGuardAsync() {
    var transport = new GapTransport();
    var strategy = new GapStrategy(() => new WorkBatch { InboxWork = [], OutboxWork = [], PerspectiveWork = [] });
    var worker = _createWorker(transport, strategy);

    await worker.StartAsync(CancellationToken.None);
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(5));

    var envelope = new MessageEnvelope<object> {
      MessageId = MessageId.New(),
      Payload = JsonDocument.Parse("{\"Data\":\"boxed\"}").RootElement,
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    await Assert.That(async () =>
      await transport.CapturedBatchHandler!(
        [new TransportMessage(envelope, JSON_ENVELOPE_TYPE)],
        CancellationToken.None)
    ).Throws<InvalidOperationException>();

    await Assert.That(strategy.CapturedInboxMessages.Count).IsEqualTo(0);

    await worker.StopAsync(CancellationToken.None);
  }

  // ========================================
  // IEventTypeProvider + StreamIdGuard branches
  // ========================================

  /// <summary>
  /// Covers the IEventTypeProvider branch of the isEvent determination: with a provider
  /// registered, the worker matches the extracted message type name against the provider's
  /// event types (instead of the 'payload is IEvent' fallback, which is always false for
  /// JsonElement payloads) and marks the InboxMessage as an event.
  /// </summary>
  [Test]
  public async Task HandleMessage_EventTypeProviderRegistered_MarksInboxMessageAsEventAsync() {
    var transport = new GapTransport();
    var strategy = new GapStrategy(() => new WorkBatch { InboxWork = [], OutboxWork = [], PerspectiveWork = [] });
    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IWorkCoordinatorStrategy>(strategy);
    services.AddSingleton<IEventTypeProvider>(new GapEventTypeProvider());

    var worker = _createWorker(transport, strategy, services);
    await worker.StartAsync(CancellationToken.None);
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(5));

    var messageId = MessageId.New();
    await transport.CapturedBatchHandler!(
      [new TransportMessage(_createJsonEnvelope(messageId, Guid.NewGuid()), JSON_ENVELOPE_TYPE)],
      CancellationToken.None);

    await Assert.That(strategy.CapturedInboxMessages.Count).IsEqualTo(1);
    await Assert.That(strategy.CapturedInboxMessages[0].IsEvent).IsTrue();

    await worker.StopAsync(CancellationToken.None);
  }

  /// <summary>
  /// Covers the StreamIdGuard.ThrowIfEmpty branch: an event (per IEventTypeProvider) whose
  /// AggregateId metadata resolves to Guid.Empty must fail fast with InvalidStreamIdException
  /// instead of storing an inbox row with a broken stream identity.
  /// </summary>
  [Test]
  public async Task HandleMessage_EventWithEmptyStreamId_ThrowsInvalidStreamIdExceptionAsync() {
    var transport = new GapTransport();
    var strategy = new GapStrategy(() => new WorkBatch { InboxWork = [], OutboxWork = [], PerspectiveWork = [] });
    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity();
    services.AddSingleton<IWorkCoordinatorStrategy>(strategy);
    services.AddSingleton<IEventTypeProvider>(new GapEventTypeProvider());

    var worker = _createWorker(transport, strategy, services);
    await worker.StartAsync(CancellationToken.None);
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(5));

    var envelope = _createJsonEnvelope(MessageId.New(), Guid.Empty);

    await Assert.That(async () =>
      await transport.CapturedBatchHandler!(
        [new TransportMessage(envelope, JSON_ENVELOPE_TYPE)],
        CancellationToken.None)
    ).Throws<InvalidStreamIdException>();

    await Assert.That(strategy.CapturedInboxMessages.Count).IsEqualTo(0);

    await worker.StopAsync(CancellationToken.None);
  }

  // ========================================
  // _extractStreamId fallback branches
  // ========================================

  /// <summary>
  /// Covers the Guid.TryParse-failure branch of _extractStreamId: an AggregateId metadata
  /// value that is a string but not a parseable GUID must fall back to the MessageId.
  /// </summary>
  [Test]
  public async Task HandleMessage_AggregateIdNotAGuid_FallsBackToMessageIdAsync() {
    var transport = new GapTransport();
    var strategy = new GapStrategy(() => new WorkBatch { InboxWork = [], OutboxWork = [], PerspectiveWork = [] });
    var worker = _createWorker(transport, strategy);
    await worker.StartAsync(CancellationToken.None);
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(5));

    var messageId = MessageId.New();
    var envelope = _createJsonEnvelopeWithRawAggregateId(messageId, "\"not-a-guid\"");

    await transport.CapturedBatchHandler!(
      [new TransportMessage(envelope, JSON_ENVELOPE_TYPE)],
      CancellationToken.None);

    await Assert.That(strategy.CapturedInboxMessages.Count).IsEqualTo(1);
    await Assert.That(strategy.CapturedInboxMessages[0].StreamId).IsEqualTo(messageId.Value);

    await worker.StopAsync(CancellationToken.None);
  }

  /// <summary>
  /// Covers the ValueKind != String branch of _extractStreamId: an AggregateId metadata
  /// value that is a JSON number must be ignored, falling back to the MessageId.
  /// </summary>
  [Test]
  public async Task HandleMessage_AggregateIdNonStringValueKind_FallsBackToMessageIdAsync() {
    var transport = new GapTransport();
    var strategy = new GapStrategy(() => new WorkBatch { InboxWork = [], OutboxWork = [], PerspectiveWork = [] });
    var worker = _createWorker(transport, strategy);
    await worker.StartAsync(CancellationToken.None);
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(5));

    var messageId = MessageId.New();
    var envelope = _createJsonEnvelopeWithRawAggregateId(messageId, "12345");

    await transport.CapturedBatchHandler!(
      [new TransportMessage(envelope, JSON_ENVELOPE_TYPE)],
      CancellationToken.None);

    await Assert.That(strategy.CapturedInboxMessages.Count).IsEqualTo(1);
    await Assert.That(strategy.CapturedInboxMessages[0].StreamId).IsEqualTo(messageId.Value);

    await worker.StopAsync(CancellationToken.None);
  }

  // ========================================
  // _startInboxActivity malformed TraceParent branch
  // ========================================

  /// <summary>
  /// Covers the ActivityContext.TryParse-failure branch of _startInboxActivity: a hop with a
  /// non-null but malformed TraceParent must yield a null activity and processing must
  /// continue normally (one dedup flush).
  /// </summary>
  [Test]
  public async Task HandleMessage_MalformedTraceParent_ProcessesWithoutActivityAsync() {
    var transport = new GapTransport();
    var strategy = new GapStrategy(() => new WorkBatch { InboxWork = [], OutboxWork = [], PerspectiveWork = [] });
    var worker = _createWorker(transport, strategy);
    await worker.StartAsync(CancellationToken.None);
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(5));

    var envelope = _createJsonEnvelope(MessageId.New(), Guid.NewGuid(), traceParent: "this-is-not-a-w3c-traceparent");

    await transport.CapturedBatchHandler!(
      [new TransportMessage(envelope, JSON_ENVELOPE_TYPE)],
      CancellationToken.None);

    await Assert.That(strategy.FlushCallCount).IsEqualTo(1);

    await worker.StopAsync(CancellationToken.None);
  }

  // ========================================
  // Empty batch short-circuit
  // ========================================

  /// <summary>
  /// Covers the pending.Count == 0 branch of the batch subscription callback: an empty batch
  /// must complete without enqueueing anything into the per-stream serializer and without
  /// touching the work coordinator.
  /// </summary>
  [Test]
  public async Task BatchHandler_EmptyBatch_CompletesWithoutEnqueueingAsync() {
    var transport = new GapTransport();
    var strategy = new GapStrategy(() => new WorkBatch { InboxWork = [], OutboxWork = [], PerspectiveWork = [] });
    var worker = _createWorker(transport, strategy);
    await worker.StartAsync(CancellationToken.None);
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(5));

    await transport.CapturedBatchHandler!([], CancellationToken.None);

    await Assert.That(strategy.FlushCallCount).IsEqualTo(0);
    await Assert.That(strategy.CapturedInboxMessages.Count).IsEqualTo(0);

    await worker.StopAsync(CancellationToken.None);
  }

  // ========================================
  // Detached stage error containment
  // ========================================

  /// <summary>
  /// Covers the catch(Exception) branch of _fireDetachedStageAsync: a receptor throwing at
  /// PreInboxDetached must be contained (logged) inside the detached Task.Run. Observable
  /// invariants: the receptor actually fired, the inline pipeline still completed both
  /// flushes, and DrainDetachedAsync completes without surfacing the fault (a faulted
  /// tracked task would make Task.WhenAll throw).
  /// </summary>
  [Test]
  public async Task HandleMessage_DetachedStageReceptorThrows_ErrorIsContainedAndProcessingSucceedsAsync() {
    var transport = new GapTransport();
    var messageId = MessageId.New();
    var streamId = Guid.NewGuid();

    var inboxWork = new InboxWork {
      MessageId = messageId.Value,
      Envelope = _createJsonEnvelope(messageId, streamId),
      MessageType = "Whizbang.Core.Tests.Workers.SbcGapTestEvent, Whizbang.Core.Tests",
      Status = MessageProcessingStatus.None,
      Attempts = 0
    };
    var strategy = new GapStrategy(() => new WorkBatch { InboxWork = [inboxWork], OutboxWork = [], PerspectiveWork = [] });

    var receptorFired = false;
    var registry = new GapReceptorRegistry();
    registry.AddReceptor(LifecycleStage.PreInboxDetached, typeof(SbcGapTestEvent), new ReceptorInfo(
      MessageType: typeof(SbcGapTestEvent),
      ReceptorId: "gap_throwing_pre_inbox_detached",
      InvokeAsync: (_, _, _, _, _) => {
        receptorFired = true;
        throw new InvalidOperationException("Simulated detached receptor failure");
      }
    ));

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity(options => { options.AllowAnonymous = true; });
    services.AddSingleton<IWorkCoordinatorStrategy>(strategy);
    services.AddSingleton<IReceptorRegistry>(registry);
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));

    var worker = _createWorker(
      transport, strategy, services,
      lifecycleMessageDeserializer: new GapLifecycleDeserializer());

    await worker.StartAsync(CancellationToken.None);
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(5));

    await transport.CapturedBatchHandler!(
      [new TransportMessage(_createJsonEnvelope(messageId, streamId), JSON_ENVELOPE_TYPE)],
      CancellationToken.None);

    // Completion signal: DrainDetachedAsync awaits the detached task that contained the throw.
    await worker.DrainDetachedAsync();

    await Assert.That(receptorFired).IsTrue();
    await Assert.That(strategy.FlushCallCount).IsGreaterThanOrEqualTo(2);

    await worker.StopAsync(CancellationToken.None);
  }

  // ========================================
  // Drop gate parse-miss branch
  // ========================================

  /// <summary>
  /// Covers the ExtractInnerTypeName-returns-null branch of the receive-boundary drop gate:
  /// when the envelope type string has no [[...]] inner type, the gate must be skipped even
  /// though the registry reports no consumer — a registry-side parse miss must never drop a
  /// message. The message then reaches _serializeToNewInboxMessage, whose format validation
  /// throws (proving it was NOT silently dropped).
  /// </summary>
  [Test]
  public async Task HandleMessage_UnparseableEnvelopeType_SkipsDropGateAsync() {
    var transport = new GapTransport();
    var strategy = new GapStrategy(() => new WorkBatch { InboxWork = [], OutboxWork = [], PerspectiveWork = [] });
    var worker = _createWorker(
      transport, strategy,
      receptorRegistry: new GapReceptorRegistryQuery(hasAnyConsumer: _ => false));

    await worker.StartAsync(CancellationToken.None);
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(5));

    var envelope = _createJsonEnvelope(MessageId.New(), Guid.NewGuid());

    await Assert.That(async () =>
      await transport.CapturedBatchHandler!(
        [new TransportMessage(envelope, "NotAWrappedEnvelopeType, SomeAssembly")],
        CancellationToken.None)
    ).Throws<InvalidOperationException>();

    await worker.StopAsync(CancellationToken.None);
  }

  // ========================================
  // PreInbox gate 'continue' branch
  // ========================================

  /// <summary>
  /// Covers the PreInbox gate's 'continue' branch plus the null-runtime-registry arm of
  /// _runtimeHasReceptors: with a compile-time registry reporting no PreInbox receptors and
  /// no runtime registry supplied to the worker, PreInbox stages are skipped for the work
  /// item while the ungated PostInbox stages still fire.
  /// </summary>
  [Test]
  public async Task HandleMessage_PreInboxGate_NoReceptorsAnywhere_SkipsPreInboxButFiresPostInboxAsync() {
    var transport = new GapTransport();
    var messageId = MessageId.New();
    var streamId = Guid.NewGuid();

    var inboxWork = new InboxWork {
      MessageId = messageId.Value,
      Envelope = _createJsonEnvelope(messageId, streamId),
      MessageType = "Whizbang.Core.Tests.Workers.SbcGapTestEvent, Whizbang.Core.Tests",
      Status = MessageProcessingStatus.None,
      Attempts = 0
    };
    var strategy = new GapStrategy(() => new WorkBatch { InboxWork = [inboxWork], OutboxWork = [], PerspectiveWork = [] });

    var invokedStages = new System.Collections.Concurrent.ConcurrentBag<LifecycleStage>();
    var registry = new GapReceptorRegistry();
    foreach (var stage in new[] {
      LifecycleStage.PreInboxDetached, LifecycleStage.PreInboxInline,
      LifecycleStage.PostInboxDetached, LifecycleStage.PostInboxInline
    }) {
      var capturedStage = stage;
      registry.AddReceptor(stage, typeof(SbcGapTestEvent), new ReceptorInfo(
        MessageType: typeof(SbcGapTestEvent),
        ReceptorId: $"gap_gate_receptor_{stage}",
        InvokeAsync: (_, _, _, _, _) => {
          invokedStages.Add(capturedStage);
          return ValueTask.FromResult<object?>(null);
        }
      ));
    }

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity(options => { options.AllowAnonymous = true; });
    services.AddSingleton<IWorkCoordinatorStrategy>(strategy);
    services.AddSingleton<IReceptorRegistry>(registry);
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));

    var worker = _createWorker(
      transport, strategy, services,
      lifecycleMessageDeserializer: new GapLifecycleDeserializer(),
      // Compile-time query: no lifecycle receptors, but the type HAS a consumer so the
      // receive-boundary drop gate lets the message through to the PreInbox gate under test.
      receptorRegistry: new GapReceptorRegistryQuery(hasReceptors: (_, _) => false, hasAnyConsumer: _ => true));

    await worker.StartAsync(CancellationToken.None);
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(5));

    await transport.CapturedBatchHandler!(
      [new TransportMessage(_createJsonEnvelope(messageId, streamId), JSON_ENVELOPE_TYPE)],
      CancellationToken.None);
    await worker.DrainDetachedAsync();

    await Assert.That(invokedStages).DoesNotContain(LifecycleStage.PreInboxDetached)
      .Because("The PreInbox gate must skip firing when neither compile-time nor runtime registries have receptors.");
    await Assert.That(invokedStages).DoesNotContain(LifecycleStage.PreInboxInline)
      .Because("The PreInbox gate must skip firing when neither compile-time nor runtime registries have receptors.");
    await Assert.That(invokedStages).Contains(LifecycleStage.PostInboxInline)
      .Because("PostInbox stages are not gated and must still fire for the work item.");

    await worker.StopAsync(CancellationToken.None);
  }

  // ========================================
  // _isEventWithoutPerspectives non-matching branch
  // ========================================

  /// <summary>
  /// Covers the loop-without-match branch of _isEventWithoutPerspectives: a perspective
  /// registry IS registered but none of its perspectives handle this event type, so the
  /// method returns true after scanning and PostLifecycle fires via the no-coordinator
  /// fallback path.
  /// </summary>
  [Test]
  public async Task HandleMessage_PerspectivesRegisteredButNoneMatch_FiresPostLifecycleAsync() {
    var transport = new GapTransport();
    var messageId = MessageId.New();
    var streamId = Guid.NewGuid();

    var inboxWork = new InboxWork {
      MessageId = messageId.Value,
      Envelope = _createJsonEnvelope(messageId, streamId),
      MessageType = "Whizbang.Core.Tests.Workers.SbcGapTestEvent, Whizbang.Core.Tests",
      Status = MessageProcessingStatus.None,
      Attempts = 0
    };
    var strategy = new GapStrategy(() => new WorkBatch { InboxWork = [inboxWork], OutboxWork = [], PerspectiveWork = [] });

    var invokedStages = new System.Collections.Concurrent.ConcurrentBag<LifecycleStage>();
    var registry = new GapReceptorRegistry();
    foreach (var stage in new[] {
      LifecycleStage.PostLifecycleDetached, LifecycleStage.PostLifecycleInline
    }) {
      var capturedStage = stage;
      registry.AddReceptor(stage, typeof(SbcGapTestEvent), new ReceptorInfo(
        MessageType: typeof(SbcGapTestEvent),
        ReceptorId: $"gap_postlc_receptor_{stage}",
        InvokeAsync: (_, _, _, _, _) => {
          invokedStages.Add(capturedStage);
          return ValueTask.FromResult<object?>(null);
        }
      ));
    }

    // Perspective registry that only handles a DIFFERENT event type — the scan must not match.
    var perspectiveRegistry = new GapPerspectiveRunnerRegistry([
      new PerspectiveRegistrationInfo(
        ClrTypeName: "UnrelatedPerspective",
        FullyQualifiedName: "Test.UnrelatedPerspective",
        ModelType: "UnrelatedModel",
        EventTypes: ["Some.Other.Namespace.UnrelatedEvent, Some.Other.Assembly"]
      )
    ]);

    var services = new ServiceCollection();
    services.AddWhizbangMessageSecurity(options => { options.AllowAnonymous = true; });
    services.AddSingleton<IWorkCoordinatorStrategy>(strategy);
    services.AddSingleton<IReceptorRegistry>(registry);
    services.AddScoped<IReceptorInvoker>(sp => new ReceptorInvoker(registry, sp));
    services.AddSingleton<IPerspectiveRunnerRegistry>(perspectiveRegistry);
    // NOTE: no ILifecycleCoordinator → fallback path fires PostLifecycle directly.

    var worker = _createWorker(
      transport, strategy, services,
      lifecycleMessageDeserializer: new GapLifecycleDeserializer());

    await worker.StartAsync(CancellationToken.None);
    await worker.SubscriptionsReady.WaitAsync(TimeSpan.FromSeconds(5));

    await transport.CapturedBatchHandler!(
      [new TransportMessage(_createJsonEnvelope(messageId, streamId), JSON_ENVELOPE_TYPE)],
      CancellationToken.None);
    await worker.DrainDetachedAsync();

    await Assert.That(invokedStages).Contains(LifecycleStage.PostLifecycleInline)
      .Because("An event whose type matches NO registered perspective must get PostLifecycle from the consumer worker itself.");
    await Assert.That(invokedStages).Contains(LifecycleStage.PostLifecycleDetached)
      .Because("The fallback path fires PostLifecycleDetached via a tracked detached task.");

    await worker.StopAsync(CancellationToken.None);
  }

  // ========================================
  // Helper Methods
  // ========================================

  private static ServiceBusConsumerWorker _createWorker(
    GapTransport transport,
    GapStrategy strategy,
    ServiceCollection? services = null,
    ILifecycleMessageDeserializer? lifecycleMessageDeserializer = null,
    MessageProcessingOptions? messageProcessingOptions = null,
    IReceptorRegistryQuery? receptorRegistry = null) {
    if (services is null) {
      services = new ServiceCollection();
      services.AddWhizbangMessageSecurity();
      services.AddSingleton<IWorkCoordinatorStrategy>(strategy);
    }
    var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    var jsonOptions = new JsonSerializerOptions { TypeInfoResolver = SbcGapJsonContext.Default };

    return new ServiceBusConsumerWorker(
      transport,
      scopeFactory,
      jsonOptions,
      new TestLogger<ServiceBusConsumerWorker>(),
      new OrderedStreamProcessor(parallelizeStreams: false, logger: null),
      new ServiceBusConsumerOptions {
        Subscriptions = [new TopicSubscription("gap-topic", "gap-sub")]
      },
      lifecycleMessageDeserializer,
      envelopeSerializer: null,
      messageProcessingOptions: messageProcessingOptions,
      receptorRegistry: receptorRegistry,
      runtimeReceptorRegistry: null
    );
  }

  private static MessageEnvelope<JsonElement> _createJsonEnvelope(
    MessageId messageId, Guid streamId, string? traceParent = null) {
    return _createJsonEnvelopeWithRawAggregateId(messageId, $"\"{streamId}\"", traceParent);
  }

  private static MessageEnvelope<JsonElement> _createJsonEnvelopeWithRawAggregateId(
    MessageId messageId, string rawAggregateIdJson, string? traceParent = null) {
    var payload = JsonDocument.Parse("{\"Data\":\"gap-test-data\"}").RootElement;
    return new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = payload,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          TraceParent = traceParent,
          ServiceInstance = new ServiceInstanceInfo {
            InstanceId = Guid.NewGuid(),
            ServiceName = "GapTestService",
            HostName = "gap-test-host",
            ProcessId = 4321
          },
          Metadata = new Dictionary<string, JsonElement> {
            ["AggregateId"] = JsonDocument.Parse(rawAggregateIdJson).RootElement
          }
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  private static MessageEnvelope<SbcGapTestEvent> _createTypedEnvelope(MessageId messageId) {
    return new MessageEnvelope<SbcGapTestEvent> {
      MessageId = messageId,
      Payload = new SbcGapTestEvent { Data = "typed-payload" },
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  // ========================================
  // Test Doubles
  // ========================================

  /// <summary>Transport that captures the batch handler so tests can deliver batches directly.</summary>
  private sealed class GapTransport : ITransport {
    public Func<IReadOnlyList<TransportMessage>, CancellationToken, Task>? CapturedBatchHandler { get; private set; }
    public bool IsInitialized => true;
    public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<ISubscription> SubscribeAsync(
      Func<IMessageEnvelope, string?, CancellationToken, Task> handler,
      TransportDestination destination,
      CancellationToken cancellationToken = default) {
      return Task.FromResult<ISubscription>(new GapSubscription());
    }

    public Task<ISubscription> SubscribeBatchAsync(
      Func<IReadOnlyList<TransportMessage>, CancellationToken, Task> batchHandler,
      TransportDestination destination,
      TransportBatchOptions batchOptions,
      CancellationToken cancellationToken = default) {
      CapturedBatchHandler = batchHandler;
      return Task.FromResult<ISubscription>(new GapSubscription());
    }

    public Task PublishAsync(IMessageEnvelope envelope, TransportDestination destination,
      string? envelopeType = null, ReadOnlyMemory<byte>? preSerializedBytes = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IMessageEnvelope> SendAsync<TRequest, TResponse>(IMessageEnvelope envelope,
      TransportDestination destination, CancellationToken cancellationToken = default)
      where TRequest : notnull where TResponse : notnull =>
      throw new NotImplementedException();
  }

  private sealed class GapSubscription : ISubscription {
    public bool IsActive { get; private set; } = true;
#pragma warning disable CS0067 // Event required by interface but unused in test double
    public event EventHandler<SubscriptionDisconnectedEventArgs>? OnDisconnected;
#pragma warning restore CS0067
    public Task PauseAsync() {
      IsActive = false;
      return Task.CompletedTask;
    }
    public Task ResumeAsync() {
      IsActive = true;
      return Task.CompletedTask;
    }
    public void Dispose() {
      IsActive = false;
    }
  }

  /// <summary>
  /// Work coordinator strategy that captures queued inbox messages, counts flushes, and
  /// returns a configurable batch.
  /// </summary>
  private sealed class GapStrategy(Func<WorkBatch> flushFunc) : IWorkCoordinatorStrategy {
    public int FlushCallCount { get; private set; }
    public List<InboxMessage> CapturedInboxMessages { get; } = [];

    public void QueueOutboxMessage(OutboxMessage message) { }
    public void QueueInboxMessage(InboxMessage message) {
      CapturedInboxMessages.Add(message);
    }
    public void QueueOutboxCompletion(Guid messageId, MessageProcessingStatus status) { }
    public void QueueOutboxFailure(Guid messageId, MessageProcessingStatus partialStatus, string error) { }
    public void QueueInboxCompletion(Guid messageId, MessageProcessingStatus status) { }
    public void QueueInboxFailure(Guid messageId, MessageProcessingStatus partialStatus, string error) { }

    public Task FlushAsync(WorkBatchOptions flags, CancellationToken ct = default) {
      return FlushAndGetBatchAsync(flags, ct);
    }

    public Task<WorkBatch> FlushAndGetBatchAsync(WorkBatchOptions flags, CancellationToken ct = default) {
      FlushCallCount++;
      return Task.FromResult(flushFunc());
    }
  }

  /// <summary>Lifecycle message deserializer that always returns an SbcGapTestEvent.</summary>
  private sealed class GapLifecycleDeserializer : ILifecycleMessageDeserializer {
    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope, string envelopeTypeName) {
      return new SbcGapTestEvent { Data = "deserialized" };
    }

    public object DeserializeFromEnvelope(IMessageEnvelope<JsonElement> envelope) {
      return new SbcGapTestEvent { Data = "deserialized" };
    }

    public object DeserializeFromBytes(byte[] jsonBytes, string messageTypeName) {
      return new SbcGapTestEvent { Data = "deserialized" };
    }

    public object DeserializeFromJsonElement(JsonElement jsonElement, string messageTypeName) {
      return new SbcGapTestEvent { Data = "deserialized" };
    }
  }

  /// <summary>Runtime receptor registry with test-registered ReceptorInfo entries.</summary>
  private sealed class GapReceptorRegistry : IReceptorRegistry {
    private readonly Dictionary<(Type, LifecycleStage), List<ReceptorInfo>> _receptors = [];

    public void AddReceptor(LifecycleStage stage, Type messageType, ReceptorInfo receptor) {
      var key = (messageType, stage);
      if (!_receptors.TryGetValue(key, out var list)) {
        list = [];
        _receptors[key] = list;
      }
      list.Add(receptor);
    }

    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) {
      var key = (messageType, stage);
      return _receptors.TryGetValue(key, out var list) ? list : Array.Empty<ReceptorInfo>();
    }

    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }

  /// <summary>Compile-time registry query double with test-controllable answers.</summary>
  private sealed class GapReceptorRegistryQuery(
      Func<LifecycleStage, string, bool>? hasReceptors = null,
      Func<string, bool>? hasAnyConsumer = null) : IReceptorRegistryQuery {
    public bool HasReceptors(LifecycleStage stage, string messageType)
      => hasReceptors?.Invoke(stage, messageType) ?? false;
    public bool HasInboxHandler(string messageType) => false;
    public bool HasAnyConsumer(string messageType)
      => hasAnyConsumer?.Invoke(messageType) ?? true;
  }

  /// <summary>Event type provider exposing the gap suite's test event type.</summary>
  private sealed class GapEventTypeProvider : IEventTypeProvider {
    public IReadOnlyList<Type> GetEventTypes() => [typeof(SbcGapTestEvent)];
  }

  /// <summary>Perspective runner registry reporting a fixed set of registrations.</summary>
  private sealed class GapPerspectiveRunnerRegistry(
    IReadOnlyList<PerspectiveRegistrationInfo> perspectives) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => null;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => perspectives;
    public IReadOnlyList<Type> GetEventTypes() => [];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }
}
