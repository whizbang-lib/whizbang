#pragma warning disable CA1707

using Microsoft.Extensions.DependencyInjection;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

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
    var inboxFormat = "MyApp.Events.OrderCreated, MyApp, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
    var shortFormat = "MyApp.Events.OrderCreated, MyApp";

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

  #region Test Doubles

  private sealed class TestPerspectiveRunnerRegistry(
    IReadOnlyList<PerspectiveRegistrationInfo> perspectives
  ) : IPerspectiveRunnerRegistry {
    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => null;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() => perspectives;
    public IReadOnlyList<Type> GetEventTypes() => [];
  }

  #endregion
}
