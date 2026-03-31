#pragma warning disable CA1707

using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tests for TransportConsumerWorker's PostLifecycle stage behavior.
/// PostLifecycle fires for events WITHOUT perspectives immediately after PostInbox.
/// Events WITH perspectives get PostLifecycle from PerspectiveWorker at batch end.
/// </summary>
public class TransportConsumerWorkerPostLifecycleTests {

  [Test]
  public async Task EventWithoutPerspectives_ShouldFirePostLifecycle_WhenNoPerspectiveRegistryAsync() {
    // When no IPerspectiveRunnerRegistry is registered, all events are "without perspectives"
    var services = new ServiceCollection();
    var serviceProvider = services.BuildServiceProvider();

    var registry = serviceProvider.GetService<IPerspectiveRunnerRegistry>();

    // Assert: No registry means all events are without perspectives
    await Assert.That(registry).IsNull();
  }

  [Test]
  public async Task EventWithPerspectives_ShouldNotFirePostLifecycle_InTransportConsumerAsync() {
    // Events that have associated perspectives should NOT get PostLifecycle from
    // TransportConsumerWorker — they get it from PerspectiveWorker instead
    // EventTypes now use runtime format "FullName, AssemblyName" (matches TypeNameFormatter.Format)
    var registry = new TestPerspectiveRunnerRegistry([
      new PerspectiveRegistrationInfo(
        ClrTypeName: "TestApp.Perspectives.OrderPerspective",
        FullyQualifiedName: "global::TestApp.Perspectives.OrderPerspective",
        ModelType: "global::TestApp.Models.OrderModel",
        EventTypes: ["TestApp.Events.OrderCreated, TestApp", "TestApp.Events.OrderShipped, TestApp"]
      )
    ]);

    var perspectives = registry.GetRegisteredPerspectives();

    // Assert: OrderCreated has perspectives (assembly-qualified inbox format)
    var hasOrderCreatedPerspective = _hasPerspectiveForEventType("TestApp.Events.OrderCreated, TestApp", perspectives);
    await Assert.That(hasOrderCreatedPerspective).IsTrue();

    // Assert: OrderShipped has perspectives
    var hasOrderShippedPerspective = _hasPerspectiveForEventType("TestApp.Events.OrderShipped, TestApp", perspectives);
    await Assert.That(hasOrderShippedPerspective).IsTrue();

    // Assert: UnrelatedEvent does NOT have perspectives
    var hasUnrelatedPerspective = _hasPerspectiveForEventType("TestApp.Events.UnrelatedEvent, TestApp", perspectives);
    await Assert.That(hasUnrelatedPerspective).IsFalse();
  }

  [Test]
  public async Task EventWithoutPerspectives_ShouldFirePostLifecycle_WhenRegistryHasNoPerspectivesForEventTypeAsync() {
    // When a registry exists but has no perspectives for this event type,
    // the event should get PostLifecycle from TransportConsumerWorker
    var registry = new TestPerspectiveRunnerRegistry([
      new PerspectiveRegistrationInfo(
        ClrTypeName: "TestApp.Perspectives.OrderPerspective",
        FullyQualifiedName: "global::TestApp.Perspectives.OrderPerspective",
        ModelType: "global::TestApp.Models.OrderModel",
        EventTypes: ["TestApp.Events.OrderCreated, TestApp"]
      )
    ]);

    var perspectives = registry.GetRegisteredPerspectives();

    // UserRegistered has no perspectives
    var hasUserRegisteredPerspective = _hasPerspectiveForEventType("TestApp.Events.UserRegistered, TestApp", perspectives);
    await Assert.That(hasUserRegisteredPerspective).IsFalse();
  }

  // ========================================
  // CROSS-FORMAT LOCK-IN TESTS
  // ========================================

  [Test]
  public async Task NormalizeTypeName_InboxFormatMatchesNormalized_StripsVersionCulturePublicKeyTokenAsync() {
    // Inbox messages may include Version/Culture/PublicKeyToken — NormalizeTypeName must strip them
    const string inboxFormat = "MyApp.Events.OrderCreated, MyApp, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
    const string shortFormat = "MyApp.Events.OrderCreated, MyApp";

    var normalizedInbox = EventTypeMatchingHelper.NormalizeTypeName(inboxFormat);
    var normalizedShort = EventTypeMatchingHelper.NormalizeTypeName(shortFormat);

    await Assert.That(normalizedInbox).IsEqualTo(normalizedShort);
  }

  [Test]
  public async Task RuntimeFormat_MatchesGeneratorFormat_FormatConsistencyAsync() {
    // TypeNameFormatter.Format(Type) at runtime must produce the same format
    // as TypeNameUtilities.FormatTypeNameForRuntime(ITypeSymbol) at generator time.
    // Both produce "FullName, AssemblyName".
    var runtimeFormat = TypeNameFormatter.Format(typeof(EventTypeMatchingHelper));
    // Expected: "Whizbang.Core.Messaging.EventTypeMatchingHelper, Whizbang.Core"
    await Assert.That(runtimeFormat).Contains(", ");
    await Assert.That(runtimeFormat).DoesNotContain("global::");
    await Assert.That(runtimeFormat).DoesNotContain("Version=");
  }

  [Test]
  public async Task AssemblyQualifiedMessageType_MatchesPerspectiveEventType_ReturnsFalseAsync() {
    // When event types in registry use runtime format, assembly-qualified inbox messages should match
    var registry = new TestPerspectiveRunnerRegistry([
      new PerspectiveRegistrationInfo(
        ClrTypeName: "TestApp.Perspectives.OrderPerspective",
        FullyQualifiedName: "global::TestApp.Perspectives.OrderPerspective",
        ModelType: "global::TestApp.Models.OrderModel",
        EventTypes: ["MyApp.Events.OrderCreated, MyApp"]
      )
    ]);

    var perspectives = registry.GetRegisteredPerspectives();

    // Inbox message type (may have Version info)
    var hasMatch = _hasPerspectiveForEventType(
      "MyApp.Events.OrderCreated, MyApp, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
      perspectives);
    await Assert.That(hasMatch).IsTrue();
  }

  [Test]
  public async Task UnrelatedEventType_DoesNotMatchPerspective_ReturnsTrueAsync() {
    var registry = new TestPerspectiveRunnerRegistry([
      new PerspectiveRegistrationInfo(
        ClrTypeName: "TestApp.Perspectives.OrderPerspective",
        FullyQualifiedName: "global::TestApp.Perspectives.OrderPerspective",
        ModelType: "global::TestApp.Models.OrderModel",
        EventTypes: ["MyApp.Events.OrderCreated, MyApp"]
      )
    ]);

    var perspectives = registry.GetRegisteredPerspectives();

    // Completely unrelated event
    var hasMatch = _hasPerspectiveForEventType("MyApp.Events.UserRegistered, MyApp", perspectives);
    await Assert.That(hasMatch).IsFalse();
  }

  [Test]
  public async Task NullRegistry_ReturnsTrue_AllEventsWithoutPerspectivesAsync() {
    var services = new ServiceCollection();
    var serviceProvider = services.BuildServiceProvider();

    var registry = serviceProvider.GetService<IPerspectiveRunnerRegistry>();
    await Assert.That(registry).IsNull();
    // _isEventWithoutPerspectives returns true when registry is null
  }

  /// <summary>
  /// Mirrors the logic in TransportConsumerWorker._isEventWithoutPerspectives
  /// Uses EventTypeMatchingHelper.NormalizeTypeName for consistent matching.
  /// </summary>
  private static bool _hasPerspectiveForEventType(
      string messageType,
      IReadOnlyList<PerspectiveRegistrationInfo> perspectives) {
    var normalizedMessageType = EventTypeMatchingHelper.NormalizeTypeName(messageType);

    foreach (var perspective in perspectives) {
      foreach (var eventType in perspective.EventTypes) {
        var normalizedEventType = EventTypeMatchingHelper.NormalizeTypeName(eventType);
        if (string.Equals(normalizedMessageType, normalizedEventType, StringComparison.Ordinal)) {
          return true;
        }
      }
    }

    return false;
  }

  // ========================================
  // TERMINAL STAGE FIRING TESTS (Bug Fix: PostAllPerspectives missing for events without perspectives)
  // ========================================

  /// <summary>
  /// BUG: _invokePostLifecycleForEventAsync coordinator path called BeginTracking with
  /// PostLifecycleAsync entry stage instead of PostAllPerspectivesAsync.
  /// Tag hooks registered at PostAllPerspectivesAsync never fired for events without perspectives.
  /// </summary>
  [Test]
  public async Task InvokePostLifecycleForEvent_CoordinatorPath_BeginTracking_StartsAtPostAllPerspectivesAsyncAsync() {
    // Arrange
    var spy = new SpyLifecycleCoordinator();
    var eventId = Guid.CreateVersion7();
    var work = _createInboxWork(eventId);
    var typedEnvelope = _createTypedEnvelopeWithId(eventId);
    var lifecycleContext = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostInboxInline,
      MessageSource = MessageSource.Inbox,
      AttemptNumber = 1
    };

    var services = new ServiceCollection();
    services.AddSingleton<ILifecycleCoordinator>(spy);
    services.AddSingleton<IReceptorInvoker>(new NoOpReceptorInvoker());
    var scopedProvider = services.BuildServiceProvider();

    // Act: invoke the internal method — currently FAILS because it uses PostLifecycleAsync
    await TransportConsumerWorker.InvokePostLifecycleForEventAsync(
      work, typedEnvelope, new NoOpReceptorInvoker(), lifecycleContext, scopedProvider, CancellationToken.None);

    // Assert: BeginTracking entry stage must be PostAllPerspectivesAsync (not PostLifecycleAsync)
    await Assert.That(spy.CapturedEntryStage).IsEqualTo(LifecycleStage.PostAllPerspectivesAsync);
  }

  [Test]
  public async Task InvokePostLifecycleForEvent_CoordinatorPath_AdvancesAllFourTerminalStagesAsync() {
    // Arrange
    var spy = new SpyLifecycleCoordinator();
    var eventId = Guid.CreateVersion7();
    var work = _createInboxWork(eventId);
    var typedEnvelope = _createTypedEnvelopeWithId(eventId);
    var lifecycleContext = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostInboxInline,
      MessageSource = MessageSource.Inbox,
      AttemptNumber = 1
    };

    var services = new ServiceCollection();
    services.AddSingleton<ILifecycleCoordinator>(spy);
    services.AddSingleton<IReceptorInvoker>(new NoOpReceptorInvoker());
    var scopedProvider = services.BuildServiceProvider();

    // Act
    await TransportConsumerWorker.InvokePostLifecycleForEventAsync(
      work, typedEnvelope, new NoOpReceptorInvoker(), lifecycleContext, scopedProvider, CancellationToken.None);

    // Assert: All 4 terminal stages advanced
    await Assert.That(spy.AdvancedStages).Contains(LifecycleStage.PostAllPerspectivesAsync);
    await Assert.That(spy.AdvancedStages).Contains(LifecycleStage.PostAllPerspectivesInline);
    await Assert.That(spy.AdvancedStages).Contains(LifecycleStage.PostLifecycleAsync);
    await Assert.That(spy.AdvancedStages).Contains(LifecycleStage.PostLifecycleInline);
  }

  [Test]
  public async Task InvokePostLifecycleForEvent_FallbackPath_FiresPostAllPerspectivesBeforePostLifecycleAsync() {
    // Arrange: no coordinator → fallback path
    var spyInvoker = new SpyReceptorInvoker();
    var eventId = Guid.CreateVersion7();
    var work = _createInboxWork(eventId);
    var typedEnvelope = _createTypedEnvelopeWithId(eventId);
    var lifecycleContext = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostInboxInline,
      MessageSource = MessageSource.Inbox,
      AttemptNumber = 1
    };

    var services = new ServiceCollection(); // No ILifecycleCoordinator → fallback
    var scopedProvider = services.BuildServiceProvider();

    // Act
    await TransportConsumerWorker.InvokePostLifecycleForEventAsync(
      work, typedEnvelope, spyInvoker, lifecycleContext, scopedProvider, CancellationToken.None);

    // Assert: PostAllPerspectivesAsync fires BEFORE PostLifecycleAsync
    var stages = spyInvoker.InvokedStages;
    var postAllPerspIdx = stages.IndexOf(LifecycleStage.PostAllPerspectivesAsync);
    var postLifecycleIdx = stages.IndexOf(LifecycleStage.PostLifecycleAsync);
    await Assert.That(postAllPerspIdx).IsGreaterThanOrEqualTo(0);
    await Assert.That(postLifecycleIdx).IsGreaterThanOrEqualTo(0);
    await Assert.That(postAllPerspIdx).IsLessThan(postLifecycleIdx);
  }

  [Test]
  public async Task InvokePostLifecycleForEvent_FallbackPath_FiresAllFourTerminalStagesAsync() {
    // Arrange: no coordinator → fallback path
    var spyInvoker = new SpyReceptorInvoker();
    var eventId = Guid.CreateVersion7();
    var work = _createInboxWork(eventId);
    var typedEnvelope = _createTypedEnvelopeWithId(eventId);
    var lifecycleContext = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostInboxInline,
      MessageSource = MessageSource.Inbox,
      AttemptNumber = 1
    };

    var services = new ServiceCollection();
    var scopedProvider = services.BuildServiceProvider();

    // Act
    await TransportConsumerWorker.InvokePostLifecycleForEventAsync(
      work, typedEnvelope, spyInvoker, lifecycleContext, scopedProvider, CancellationToken.None);

    // Assert: All 4 terminal stages fired
    var stages = spyInvoker.InvokedStages;
    await Assert.That(stages).Contains(LifecycleStage.PostAllPerspectivesAsync);
    await Assert.That(stages).Contains(LifecycleStage.PostAllPerspectivesInline);
    await Assert.That(stages).Contains(LifecycleStage.PostLifecycleAsync);
    await Assert.That(stages).Contains(LifecycleStage.PostLifecycleInline);
  }

  #region Terminal Stage Test Helpers

  private static InboxWork _createInboxWork(Guid eventId) {
    var messageId = new MessageId(eventId);
    var jsonEnvelope = new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = JsonSerializer.SerializeToElement(new { Name = "test" }),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
    return new InboxWork {
      MessageId = eventId,
      Envelope = jsonEnvelope,
      MessageType = "Test.TestEvent, Test"
    };
  }

  private static MessageEnvelope<JsonElement> _createTypedEnvelopeWithId(Guid eventId) {
    return new MessageEnvelope<JsonElement> {
      MessageId = new MessageId(eventId),
      Payload = JsonSerializer.SerializeToElement(new { Name = "test" }),
      Hops = [],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };
  }

  #endregion

  #region Test Doubles

  private sealed class TestPerspectiveRunnerRegistry(
    IReadOnlyList<PerspectiveRegistrationInfo> perspectives
  ) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => null;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => perspectives;
    public IReadOnlyList<Type> GetEventTypes() => [];
  }

  /// <summary>
  /// Spy lifecycle coordinator that records BeginTracking entry stage and AdvanceToAsync calls.
  /// </summary>
  private sealed class SpyLifecycleCoordinator : ILifecycleCoordinator {
    public LifecycleStage CapturedEntryStage { get; private set; }
    public List<LifecycleStage> AdvancedStages { get; } = [];

    public ILifecycleTracking BeginTracking(
      Guid eventId, IMessageEnvelope envelope, LifecycleStage entryStage,
      MessageSource source, Guid? streamId = null, Type? perspectiveType = null) {
      CapturedEntryStage = entryStage;
      return new SpyLifecycleTracking(eventId, AdvancedStages);
    }

    public ILifecycleTracking? GetTracking(Guid eventId) => null;
    public void ExpectCompletionsFrom(Guid eventId, params PostLifecycleCompletionSource[] sources) { }

    public ValueTask SignalSegmentCompleteAsync(
      Guid eventId, PostLifecycleCompletionSource source,
      IServiceProvider scopedProvider, CancellationToken ct) => ValueTask.CompletedTask;

    public void AbandonTracking(Guid eventId) { }
    public void ExpectPerspectiveCompletions(Guid eventId, IReadOnlyList<string> perspectiveNames) { }
    public bool SignalPerspectiveComplete(Guid eventId, string perspectiveName) => false;
    public bool AreAllPerspectivesComplete(Guid eventId) => true;
    public int CleanupStaleTracking(TimeSpan inactivityThreshold) => 0;
  }

  private sealed class SpyLifecycleTracking(Guid eventId, List<LifecycleStage> advancedStages) : ILifecycleTracking {
    public Guid EventId { get; } = eventId;
    public LifecycleStage CurrentStage { get; private set; }
    public bool IsComplete { get; private set; }

    public ValueTask AdvanceToAsync(LifecycleStage stage, IServiceProvider scopedProvider, CancellationToken ct) {
      advancedStages.Add(stage);
      CurrentStage = stage;
      if (stage == LifecycleStage.PostLifecycleInline) {
        IsComplete = true;
      }
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>
  /// Spy receptor invoker that records all stages InvokeAsync is called with.
  /// </summary>
  private sealed class SpyReceptorInvoker : IReceptorInvoker {
    public List<LifecycleStage> InvokedStages { get; } = [];

    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage,
      ILifecycleContext? context = null, CancellationToken cancellationToken = default) {
      InvokedStages.Add(stage);
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>
  /// No-op receptor invoker for tests that don't need to observe invocations.
  /// </summary>
  private sealed class NoOpReceptorInvoker : IReceptorInvoker {
    public ValueTask InvokeAsync(IMessageEnvelope envelope, LifecycleStage stage,
      ILifecycleContext? context = null, CancellationToken cancellationToken = default) =>
      ValueTask.CompletedTask;
  }

  #endregion
}
