using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;
using Whizbang.Core.Transports;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Tests for IOutboxRoutingStrategy implementations.
/// Outbox strategies determine where a service publishes events.
/// </summary>
public class OutboxRoutingStrategyTests {
  #region DomainTopicOutboxStrategy

  [Test]
  public async Task DomainTopicOutboxStrategy_GetDestination_ReturnsFullNamespaceAsTopicAsync() {
    // Arrange
    var strategy = new DomainTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "outboxtesttypes.orders.events" };

    // Act - Event in OutboxTestTypes.Orders.Events namespace
    var destination = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Events.OrderCreated),
      ownedDomains,
      MessageKind.Event
    );

    // Assert - Topic is full namespace in lowercase
    await Assert.That(destination.Address).IsEqualTo("outboxtesttypes.orders.events");
  }

  [Test]
  public async Task DomainTopicOutboxStrategy_GetDestination_SetsRoutingKeyFromTypeNameAsync() {
    // Arrange
    var strategy = new DomainTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "outboxtesttypes.orders.events" };

    // Act
    var destination = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Events.OrderCreated),
      ownedDomains,
      MessageKind.Event
    );

    // Assert - Routing key is lowercase type name
    await Assert.That(destination.RoutingKey).IsEqualTo("ordercreated");
  }

  [Test]
  public async Task DomainTopicOutboxStrategy_GetDestination_WithCustomTopicResolver_UsesResolverAsync() {
    // Arrange
    var strategy = new DomainTopicOutboxStrategy(new NamespaceRoutingStrategy(
      type => "custom-domain"
    ));
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "outboxtesttypes.orders.events" };

    // Act
    var destination = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Events.OrderCreated),
      ownedDomains,
      MessageKind.Event
    );

    // Assert
    await Assert.That(destination.Address).IsEqualTo("custom-domain");
  }

  [Test]
  public async Task DomainTopicOutboxStrategy_GetDestination_NullOwnedDomains_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var strategy = new DomainTopicOutboxStrategy();

    // Act & Assert
    await Assert.That(() => strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Events.OrderCreated),
      null!,
      MessageKind.Event
    )).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task DomainTopicOutboxStrategy_GetDestination_NullMessageType_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var strategy = new DomainTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "outboxtesttypes.orders.events" };

    // Act & Assert
    await Assert.That(() => strategy.GetDestination(
      null!,
      ownedDomains,
      MessageKind.Event
    )).Throws<ArgumentNullException>();
  }

  #endregion

  #region SharedTopicOutboxStrategy - Command Routing

  [Test]
  public async Task SharedTopicOutboxStrategy_Command_RoutesToInboxTopicAsync() {
    // Arrange
    var strategy = new SharedTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "outboxtesttypes.orders.commands" };

    // Act - Command goes to shared inbox
    var destination = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Events.OrderCreated),
      ownedDomains,
      MessageKind.Command
    );

    // Assert - Commands go to shared inbox topic
    await Assert.That(destination.Address).IsEqualTo("inbox");
  }

  [Test]
  public async Task SharedTopicOutboxStrategy_Command_SetsNamespaceBasedRoutingKeyAsync() {
    // Arrange
    var strategy = new SharedTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "outboxtesttypes.orders.commands" };

    // Act
    var destination = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Events.OrderCreated),
      ownedDomains,
      MessageKind.Command
    );

    // Assert - Routing key is namespace.typename for command filtering
    await Assert.That(destination.RoutingKey).IsEqualTo("outboxtesttypes.orders.events.ordercreated");
  }

  [Test]
  public async Task SharedTopicOutboxStrategy_Command_WithCustomInboxTopic_UsesCustomTopicAsync() {
    // Arrange
    var strategy = new SharedTopicOutboxStrategy("my-inbox");
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "outboxtesttypes.orders.commands" };

    // Act
    var destination = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Events.OrderCreated),
      ownedDomains,
      MessageKind.Command
    );

    // Assert
    await Assert.That(destination.Address).IsEqualTo("my-inbox");
  }

  #endregion

  #region SharedTopicOutboxStrategy - Event Routing

  [Test]
  public async Task SharedTopicOutboxStrategy_Event_RoutesToNamespaceTopicAsync() {
    // Arrange
    var strategy = new SharedTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "outboxtesttypes.orders.events" };

    // Act - Event goes to namespace topic
    var destination = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Events.OrderCreated),
      ownedDomains,
      MessageKind.Event
    );

    // Assert - Events go to namespace-specific topic
    await Assert.That(destination.Address).IsEqualTo("outboxtesttypes.orders.events");
  }

  [Test]
  public async Task SharedTopicOutboxStrategy_Event_SetsTypeNameRoutingKeyAsync() {
    // Arrange
    var strategy = new SharedTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "outboxtesttypes.orders.events" };

    // Act
    var destination = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Events.OrderCreated),
      ownedDomains,
      MessageKind.Event
    );

    // Assert - Routing key is just the type name for events
    await Assert.That(destination.RoutingKey).IsEqualTo("ordercreated");
  }

  [Test]
  public async Task SharedTopicOutboxStrategy_Event_IncludesNamespaceInMetadataAsync() {
    // Arrange
    var strategy = new SharedTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "outboxtesttypes.orders.events" };

    // Act
    var destination = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Events.OrderCreated),
      ownedDomains,
      MessageKind.Event
    );

    // Assert - Metadata includes namespace for filtering
    await Assert.That(destination.Metadata is not null).IsTrue();
    await Assert.That(destination.Metadata!.ContainsKey("Namespace")).IsTrue();
  }

  [Test]
  public async Task SharedTopicOutboxStrategy_Event_IncludesKindInMetadataAsync() {
    // Arrange
    var strategy = new SharedTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "outboxtesttypes.orders.events" };

    // Act
    var destination = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Events.OrderCreated),
      ownedDomains,
      MessageKind.Event
    );

    // Assert - Metadata includes kind
    await Assert.That(destination.Metadata is not null).IsTrue();
    await Assert.That(destination.Metadata!.ContainsKey("Kind")).IsTrue();
  }

  #endregion

  #region SharedTopicOutboxStrategy - Validation

  [Test]
  public async Task SharedTopicOutboxStrategy_GetDestination_NullOwnedDomains_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var strategy = new SharedTopicOutboxStrategy();

    // Act & Assert
    await Assert.That(() => strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Events.OrderCreated),
      null!,
      MessageKind.Event
    )).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task SharedTopicOutboxStrategy_GetDestination_NullMessageType_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    var strategy = new SharedTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "outboxtesttypes.orders.events" };

    // Act & Assert
    await Assert.That(() => strategy.GetDestination(
      null!,
      ownedDomains,
      MessageKind.Event
    )).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task SharedTopicOutboxStrategy_DefaultInboxTopic_ReturnsInboxAsync() {
    // Assert - Static property returns default inbox topic
    await Assert.That(SharedTopicOutboxStrategy.DefaultInboxTopic).IsEqualTo("inbox");
  }

  #endregion

  #region Integration with NamespaceRoutingStrategy

  [Test]
  public async Task DomainTopicOutboxStrategy_ContractsNamespace_ReturnsFullNamespaceAsync() {
    // Arrange
    var strategy = new DomainTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "outboxtesttypes.contracts.events" };

    // Act - Type in flat namespace (Contracts.Events)
    var destination = strategy.GetDestination(
      typeof(OutboxTestTypes.Contracts.Events.ProductCreatedEvent),
      ownedDomains,
      MessageKind.Event
    );

    // Assert - Returns full namespace as topic
    await Assert.That(destination.Address).IsEqualTo("outboxtesttypes.contracts.events");
  }

  #endregion

  #region MessageKind.System branch (topology arc phase 3 - dormant capability)

  // System is not passed by any production call site yet. These tests LOCK the dormant
  // behavior: system traffic routes to the same entities commands use today. Phase 5/7
  // redirect this to the dedicated broadcast inbox.

  [Test]
  public async Task SharedTopicOutboxStrategy_SystemKind_RoutesToSharedInboxWithCommandShapedKeyAsync() {
    // Arrange
    var strategy = new SharedTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Act
    var destination = strategy.GetDestination(
      typeof(OutboxTestTypes.Orders.Commands.CreateOrder),
      ownedDomains,
      MessageKind.System
    );

    // Assert - same shared inbox as commands today, same "{namespace}.{typename}" key shape
    await Assert.That(destination.Address).IsEqualTo("inbox");
    await Assert.That(destination.RoutingKey).IsEqualTo("outboxtesttypes.orders.commands.createorder");
  }

  [Test]
  public async Task SharedTopicOutboxStrategy_AllThreeKinds_LockedOutputsAsync() {
    // Zero-behavior-change lock across every kind the strategy handles.
    var strategy = new SharedTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var type = typeof(OutboxTestTypes.Orders.Commands.CreateOrder);

    var command = strategy.GetDestination(type, ownedDomains, MessageKind.Command);
    var @event = strategy.GetDestination(type, ownedDomains, MessageKind.Event);
    var system = strategy.GetDestination(type, ownedDomains, MessageKind.System);

    // Command → shared inbox + namespace-qualified key
    await Assert.That(command.Address).IsEqualTo("inbox");
    await Assert.That(command.RoutingKey).IsEqualTo("outboxtesttypes.orders.commands.createorder");
    // Event → namespace topic + type-name key
    await Assert.That(@event.Address).IsEqualTo("outboxtesttypes.orders.commands");
    await Assert.That(@event.RoutingKey).IsEqualTo("createorder");
    // System (dormant) → SAME address+key as command today
    await Assert.That(system.Address).IsEqualTo(command.Address);
    await Assert.That(system.RoutingKey).IsEqualTo(command.RoutingKey);
  }

  [Test]
  public async Task DomainTopicOutboxStrategy_AllThreeKinds_LockedOutputsAsync() {
    // DomainTopicOutboxStrategy routes every kind identically (namespace topic + type-name
    // key); System must be no exception.
    var strategy = new DomainTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var type = typeof(OutboxTestTypes.Orders.Events.OrderCreated);

    var command = strategy.GetDestination(type, ownedDomains, MessageKind.Command);
    var @event = strategy.GetDestination(type, ownedDomains, MessageKind.Event);
    var system = strategy.GetDestination(type, ownedDomains, MessageKind.System);

    foreach (var destination in new[] { command, @event, system }) {
      await Assert.That(destination.Address).IsEqualTo("outboxtesttypes.orders.events");
      await Assert.That(destination.RoutingKey).IsEqualTo("ordercreated");
    }
  }

  #endregion

  #region GetCompositeGroupKey (topology arc phase 3)

  // Load-bearing invariant: SAME KEY ⇔ SAME DESTINATION. The composite group key is a
  // canonical projection of GetDestination — route, split, subscribe, provision must all
  // project from the same authority.

  [Test]
  public async Task GetCompositeGroupKey_SameKeyIffSameDestination_AcrossStrategiesTypesAndKindsAsync() {
    // Arrange - every built-in strategy × several message types × all message kinds.
    // NamespaceOutboxStrategy (phase 6) appears in all three flip states — unflipped,
    // one namespace flipped, everything flipped — because the invariant must hold across
    // the whole migration, not just at the endpoints.
    var partialFlip = new RoutingOptions().RouteCommandNamespaceToInbox("outboxtesttypes.orders.commands");
    var fullFlip = new RoutingOptions().RouteAllCommandNamespacesToInbox();
    var strategies = new IOutboxRoutingStrategy[] {
      new SharedTopicOutboxStrategy(),
      new DomainTopicOutboxStrategy(),
      new NamespaceOutboxStrategy(new RoutingOptions()),
      new NamespaceOutboxStrategy(partialFlip),
      new NamespaceOutboxStrategy(fullFlip)
    };
    var types = new[] {
      typeof(OutboxTestTypes.Orders.Events.OrderCreated),
      typeof(OutboxTestTypes.Orders.Events.OrderUpdated),
      typeof(OutboxTestTypes.Orders.Commands.CreateOrder),
      typeof(OutboxTestTypes.Users.Events.UserCreated),
      typeof(OutboxTestTypes.Users.Commands.CreateUser)
    };
    var kinds = new[] { MessageKind.Command, MessageKind.Event, MessageKind.Query, MessageKind.System };
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var strategy in strategies) {
      // Act - project every (type, kind) pair through both surfaces
      var projections = new List<(string Key, string Address, string? RoutingKey)>();
      foreach (var type in types) {
        foreach (var kind in kinds) {
          var key = strategy.GetCompositeGroupKey(type, ownedDomains, kind);
          var destination = strategy.GetDestination(type, ownedDomains, kind);
          projections.Add((key, destination.Address, destination.RoutingKey));
        }
      }

      // Assert - property-style: for every pair, key equality ⇔ (Address, RoutingKey) equality
      for (var i = 0; i < projections.Count; i++) {
        for (var j = 0; j < projections.Count; j++) {
          var sameKey = projections[i].Key == projections[j].Key;
          var sameDestination = projections[i].Address == projections[j].Address
            && projections[i].RoutingKey == projections[j].RoutingKey;
          await Assert.That(sameKey).IsEqualTo(sameDestination)
            .Because($"strategy {strategy.GetType().Name}: key[{i}] vs key[{j}] must agree with destination equality — same key ⇔ same destination.");
        }
      }
    }
  }

  [Test]
  public async Task GetCompositeGroupKey_CanonicalShape_AddressPipeRoutingKeyAsync() {
    // Lock the canonical key derivation: Address + "|" + (RoutingKey ?? "").
    // (Interface cast: GetCompositeGroupKey is a default interface member.)
    var strategy = new SharedTopicOutboxStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var type = typeof(OutboxTestTypes.Orders.Commands.CreateOrder);

    var key = ((IOutboxRoutingStrategy)strategy).GetCompositeGroupKey(type, ownedDomains, MessageKind.Command);

    await Assert.That(key).IsEqualTo("inbox|outboxtesttypes.orders.commands.createorder");
  }

  [Test]
  public async Task GetCompositeGroupKey_NullRoutingKey_UsesEmptySegmentAsync() {
    // A strategy returning a null routing key still yields a stable canonical key.
    var strategy = new NullRoutingKeyStrategy();
    var ownedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    var key = ((IOutboxRoutingStrategy)strategy).GetCompositeGroupKey(
      typeof(OutboxTestTypes.Orders.Events.OrderCreated), ownedDomains, MessageKind.Event);

    await Assert.That(key).IsEqualTo("fixed-address|");
  }

  /// <summary>Custom strategy proving the default interface member derives the key from
  /// GetDestination for implementations that predate the seam.</summary>
  private sealed class NullRoutingKeyStrategy : IOutboxRoutingStrategy {
    public TransportDestination GetDestination(
        Type messageType, IReadOnlySet<string> ownedDomains, MessageKind kind)
      => new("fixed-address", RoutingKey: null);
  }

  #endregion
}
