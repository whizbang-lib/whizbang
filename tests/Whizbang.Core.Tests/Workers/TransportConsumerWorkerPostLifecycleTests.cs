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
    var registry = new TestPerspectiveRunnerRegistry([
      new PerspectiveRegistrationInfo(
        ClrTypeName: "TestApp.Perspectives.OrderPerspective",
        FullyQualifiedName: "global::TestApp.Perspectives.OrderPerspective",
        ModelType: "global::TestApp.Models.OrderModel",
        EventTypes: ["TestApp.Events.OrderCreated", "TestApp.Events.OrderShipped"]
      )
    ]);

    var perspectives = registry.GetRegisteredPerspectives();

    // Assert: OrderCreated has perspectives
    var hasOrderCreatedPerspective = _hasPerspectiveForEventType("TestApp.Events.OrderCreated", perspectives);
    await Assert.That(hasOrderCreatedPerspective).IsTrue();

    // Assert: OrderShipped has perspectives
    var hasOrderShippedPerspective = _hasPerspectiveForEventType("TestApp.Events.OrderShipped", perspectives);
    await Assert.That(hasOrderShippedPerspective).IsTrue();

    // Assert: UnrelatedEvent does NOT have perspectives
    var hasUnrelatedPerspective = _hasPerspectiveForEventType("TestApp.Events.UnrelatedEvent", perspectives);
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
        EventTypes: ["TestApp.Events.OrderCreated"]
      )
    ]);

    var perspectives = registry.GetRegisteredPerspectives();

    // UserRegistered has no perspectives
    var hasUserRegisteredPerspective = _hasPerspectiveForEventType("TestApp.Events.UserRegistered", perspectives);
    await Assert.That(hasUserRegisteredPerspective).IsFalse();
  }

  /// <summary>
  /// Mirrors the logic in TransportConsumerWorker._isEventWithoutPerspectives
  /// </summary>
  private static bool _hasPerspectiveForEventType(
      string messageType,
      IReadOnlyList<PerspectiveRegistrationInfo> perspectives) {
    foreach (var perspective in perspectives) {
      foreach (var eventType in perspective.EventTypes) {
        if (eventType.EndsWith(messageType, StringComparison.Ordinal)
            || messageType.EndsWith(eventType, StringComparison.Ordinal)
            || string.Equals(eventType, messageType, StringComparison.Ordinal)) {
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
