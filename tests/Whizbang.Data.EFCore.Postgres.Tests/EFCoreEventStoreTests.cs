using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Sample event for testing event store functionality.
/// </summary>
public record OrderCreatedEvent : IEvent {
  [StreamId]
  public required Guid OrderId { get; init; }
  public required string CustomerName { get; init; }
}

/// <summary>
/// Second sample event for testing polymorphic event loading.
/// </summary>
public record OrderShippedEvent : IEvent {
  [StreamId]
  public required Guid OrderId { get; init; }
  public required string TrackingNumber { get; init; }
}

/// <summary>
/// Tests for EFCoreEventStore.
/// Verifies append-only event storage with stream-based organization and sequence numbers.
/// Uses PostgreSQL Testcontainers for real database testing with JsonDocument support.
/// Target: 100% branch coverage.
/// </summary>
public class EFCoreEventStoreTests : EFCoreTestBase {
  [Test]
  public async Task AppendAsync_WithValidEnvelope_AppendsEventToStreamAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();
    var envelope = new MessageEnvelope<OrderCreatedEvent> {
      MessageId = MessageId.New(),
      Payload = new OrderCreatedEvent {
        OrderId = Guid.NewGuid(),
        CustomerName = "John Doe"
      },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTime.UtcNow,
          ServiceInstance = new ServiceInstanceInfo {
            InstanceId = Guid.NewGuid(),
            ServiceName = "test-service",
            HostName = "test-host",
            ProcessId = 123
          }
        }
      ]
    };

    // Act
    await eventStore.AppendAsync(streamId, envelope);

    // Assert
    var events = context.Set<EventStoreRecord>().ToList();
    await Assert.That(events).Count().IsEqualTo(1);
    await Assert.That(events[0].StreamId).IsEqualTo(streamId);
  }

  [Test]
  public async Task AppendAsync_WithMultipleEvents_AssignsSequentialSequenceNumbersAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();

    // Act - Append three events
    for (int i = 0; i < 3; i++) {
      var envelope = new MessageEnvelope<OrderCreatedEvent> {
        MessageId = MessageId.New(),
        Payload = new OrderCreatedEvent {
          OrderId = Guid.NewGuid(),
          CustomerName = $"Customer {i}"
        },
        Hops = [
          new MessageHop {
            Type = HopType.Current,
            Timestamp = DateTime.UtcNow,
            ServiceInstance = new ServiceInstanceInfo {
              InstanceId = Guid.NewGuid(),
              ServiceName = "test-service",
              HostName = "test-host",
              ProcessId = 123
            }
          }
        ]
      };
      await eventStore.AppendAsync(streamId, envelope);
    }

    // Assert
    var events = context.Set<EventStoreRecord>()
      .Where(e => e.StreamId == streamId)
      .OrderBy(e => e.Version)
      .ToList();

    await Assert.That(events).Count().IsEqualTo(3);
    await Assert.That(events[0].Version).IsEqualTo(0);
    await Assert.That(events[1].Version).IsEqualTo(1);
    await Assert.That(events[2].Version).IsEqualTo(2);
  }

  [Test]
  public async Task ReadAsync_WithExistingEvents_ReturnsEventsInSequenceOrderAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();

    // Append events
    for (int i = 0; i < 3; i++) {
      var envelope = new MessageEnvelope<OrderCreatedEvent> {
        MessageId = MessageId.New(),
        Payload = new OrderCreatedEvent {
          OrderId = Guid.NewGuid(),
          CustomerName = $"Customer {i}"
        },
        Hops = [
          new MessageHop {
            Type = HopType.Current,
            Timestamp = DateTime.UtcNow,
            ServiceInstance = new ServiceInstanceInfo {
              InstanceId = Guid.NewGuid(),
              ServiceName = "test-service",
              HostName = "test-host",
              ProcessId = 123
            }
          }
        ]
      };
      await eventStore.AppendAsync(streamId, envelope);
    }

    // Act
    var events = new List<MessageEnvelope<OrderCreatedEvent>>();
    await foreach (var evt in eventStore.ReadAsync<OrderCreatedEvent>(streamId, fromSequence: 0)) {
      events.Add(evt);
    }

    // Assert
    await Assert.That(events).Count().IsEqualTo(3);
    await Assert.That(events[0].Payload.CustomerName).IsEqualTo("Customer 0");
    await Assert.That(events[1].Payload.CustomerName).IsEqualTo("Customer 1");
    await Assert.That(events[2].Payload.CustomerName).IsEqualTo("Customer 2");
  }

  [Test]
  public async Task GetLastSequenceAsync_WithEmptyStream_ReturnsMinusOneAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();

    // Act
    var lastSequence = await eventStore.GetLastSequenceAsync(streamId);

    // Assert
    await Assert.That(lastSequence).IsEqualTo(-1);
  }

  [Test]
  public async Task GetLastSequenceAsync_WithExistingEvents_ReturnsHighestSequenceAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();

    // Append events
    for (int i = 0; i < 5; i++) {
      var envelope = new MessageEnvelope<OrderCreatedEvent> {
        MessageId = MessageId.New(),
        Payload = new OrderCreatedEvent {
          OrderId = Guid.NewGuid(),
          CustomerName = $"Customer {i}"
        },
        Hops = [
          new MessageHop {
            Type = HopType.Current,
            Timestamp = DateTime.UtcNow,
            ServiceInstance = new ServiceInstanceInfo {
              InstanceId = Guid.NewGuid(),
              ServiceName = "test-service",
              HostName = "test-host",
              ProcessId = 123
            }
          }
        ]
      };
      await eventStore.AppendAsync(streamId, envelope);
    }

    // Act
    var lastSequence = await eventStore.GetLastSequenceAsync(streamId);

    // Assert
    await Assert.That(lastSequence).IsEqualTo(4);
  }

  /// <summary>
  /// Tests that GetEventsBetweenAsync returns events between two checkpoint positions (exclusive start, inclusive end).
  /// This method is used by lifecycle receptors to load events that were just processed by a perspective.
  /// </summary>
  [Test]
  public async Task GetEventsBetweenAsync_WithEventsInRange_ReturnsEventsBetweenCheckpointsAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();
    var eventIds = new List<Guid>();

    // Append 5 events
    for (int i = 0; i < 5; i++) {
      var envelope = new MessageEnvelope<OrderCreatedEvent> {
        MessageId = MessageId.New(),
        Payload = new OrderCreatedEvent {
          OrderId = Guid.NewGuid(),
          CustomerName = $"Customer {i}"
        },
        Hops = [
          new MessageHop {
            Type = HopType.Current,
            Timestamp = DateTime.UtcNow,
            ServiceInstance = new ServiceInstanceInfo {
              InstanceId = Guid.NewGuid(),
              ServiceName = "test-service",
              HostName = "test-host",
              ProcessId = 123
            }
          }
        ]
      };
      await eventStore.AppendAsync(streamId, envelope);
      eventIds.Add(envelope.MessageId.Value);
    }

    // Act - Get events between checkpoint 1 (exclusive) and checkpoint 3 (inclusive)
    // Should return events at indices 2 and 3
    var events = await eventStore.GetEventsBetweenAsync<OrderCreatedEvent>(
      streamId,
      afterEventId: eventIds[1],  // Exclusive
      upToEventId: eventIds[3],   // Inclusive
      CancellationToken.None
    );

    // Assert
    await Assert.That(events.Count).IsEqualTo(2);
    await Assert.That(events[0].MessageId.Value).IsEqualTo(eventIds[2]);
    await Assert.That(events[1].MessageId.Value).IsEqualTo(eventIds[3]);
  }

  /// <summary>
  /// Tests that GetEventsBetweenAsync with null afterEventId returns events from start.
  /// </summary>
  [Test]
  public async Task GetEventsBetweenAsync_NullAfterEventId_ReturnsFromStartAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();
    var eventIds = new List<Guid>();

    // Append 3 events
    for (int i = 0; i < 3; i++) {
      var envelope = new MessageEnvelope<OrderCreatedEvent> {
        MessageId = MessageId.New(),
        Payload = new OrderCreatedEvent {
          OrderId = Guid.NewGuid(),
          CustomerName = $"Customer {i}"
        },
        Hops = [
          new MessageHop {
            Type = HopType.Current,
            Timestamp = DateTime.UtcNow,
            ServiceInstance = new ServiceInstanceInfo {
              InstanceId = Guid.NewGuid(),
              ServiceName = "test-service",
              HostName = "test-host",
              ProcessId = 123
            }
          }
        ]
      };
      await eventStore.AppendAsync(streamId, envelope);
      eventIds.Add(envelope.MessageId.Value);
    }

    // Act - Get events from start (null) up to event 1 (inclusive)
    var events = await eventStore.GetEventsBetweenAsync<OrderCreatedEvent>(
      streamId,
      afterEventId: null,         // Start from beginning
      upToEventId: eventIds[1],   // Inclusive
      CancellationToken.None
    );

    // Assert - Should return first 2 events (indices 0 and 1)
    await Assert.That(events.Count).IsEqualTo(2);
    await Assert.That(events[0].MessageId.Value).IsEqualTo(eventIds[0]);
    await Assert.That(events[1].MessageId.Value).IsEqualTo(eventIds[1]);
  }

  /// <summary>
  /// Tests that GetEventsBetweenAsync returns empty list when no events in range.
  /// </summary>
  [Test]
  public async Task GetEventsBetweenAsync_NoEventsInRange_ReturnsEmptyListAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();

    // Append 2 events
    var firstEvent = new MessageEnvelope<OrderCreatedEvent> {
      MessageId = MessageId.New(),
      Payload = new OrderCreatedEvent {
        OrderId = Guid.NewGuid(),
        CustomerName = "Customer 0"
      },
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTime.UtcNow,
          ServiceInstance = new ServiceInstanceInfo {
            InstanceId = Guid.NewGuid(),
            ServiceName = "test-service",
            HostName = "test-host",
            ProcessId = 123
          }
        }
      ]
    };
    await eventStore.AppendAsync(streamId, firstEvent);

    // Act - Query for events between same checkpoint (no events in range)
    var events = await eventStore.GetEventsBetweenAsync<OrderCreatedEvent>(
      streamId,
      afterEventId: firstEvent.MessageId.Value,
      upToEventId: firstEvent.MessageId.Value,
      CancellationToken.None
    );

    // Assert
    await Assert.That(events.Count).IsEqualTo(0);
  }

  /// <summary>
  /// Tests that GetEventsBetweenAsync returns events in UUID v7 order (time-ordered).
  /// </summary>
  [Test]
  public async Task GetEventsBetweenAsync_MultipleEvents_ReturnsInUuidV7OrderAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();
    var eventIds = new List<Guid>();

    // Append 4 events with small delays to ensure UUID v7 ordering
    for (int i = 0; i < 4; i++) {
      var envelope = new MessageEnvelope<OrderCreatedEvent> {
        MessageId = MessageId.New(),
        Payload = new OrderCreatedEvent {
          OrderId = Guid.NewGuid(),
          CustomerName = $"Customer {i}"
        },
        Hops = [
          new MessageHop {
            Type = HopType.Current,
            Timestamp = DateTime.UtcNow,
            ServiceInstance = new ServiceInstanceInfo {
              InstanceId = Guid.NewGuid(),
              ServiceName = "test-service",
              HostName = "test-host",
              ProcessId = 123
            }
          }
        ]
      };
      await eventStore.AppendAsync(streamId, envelope);
      eventIds.Add(envelope.MessageId.Value);
      await Task.Delay(1); // Small delay to ensure UUID v7 time ordering
    }

    // Act - Get all events between first and last
    var events = await eventStore.GetEventsBetweenAsync<OrderCreatedEvent>(
      streamId,
      afterEventId: eventIds[0],
      upToEventId: eventIds[3],
      CancellationToken.None
    );

    // Assert - Events should be in time order (UUID v7)
    await Assert.That(events.Count).IsEqualTo(3);
    await Assert.That(events[0].MessageId.Value).IsEqualTo(eventIds[1]);
    await Assert.That(events[1].MessageId.Value).IsEqualTo(eventIds[2]);
    await Assert.That(events[2].MessageId.Value).IsEqualTo(eventIds[3]);

    // Verify UUID v7 time ordering (each ID should be >= previous)
    for (int i = 1; i < events.Count; i++) {
      await Assert.That(events[i].MessageId.Value).IsGreaterThanOrEqualTo(events[i - 1].MessageId.Value);
    }
  }

  /// <summary>
  /// Tests that GetEventsBetweenPolymorphicAsync returns mixed event types between two checkpoint positions.
  /// This method is used by lifecycle receptors to load events that were just processed by a perspective
  /// when the perspective handles multiple event types.
  /// </summary>
  [Test]
  public async Task GetEventsBetweenPolymorphicAsync_WithMixedEventTypes_ReturnsAllEventsAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();
    var orderId = Guid.NewGuid();
    var eventIds = new List<Guid>();

    // Append mixed events: Created, Shipped, Created, Shipped, Created
    var events = new List<IEvent> {
      new OrderCreatedEvent { OrderId = orderId, CustomerName = "Customer 1" },
      new OrderShippedEvent { OrderId = orderId, TrackingNumber = "TRACK001" },
      new OrderCreatedEvent { OrderId = orderId, CustomerName = "Customer 2" },
      new OrderShippedEvent { OrderId = orderId, TrackingNumber = "TRACK002" },
      new OrderCreatedEvent { OrderId = orderId, CustomerName = "Customer 3" }
    };

    foreach (var evt in events) {
      var envelope = evt switch {
        OrderCreatedEvent created => new MessageEnvelope<OrderCreatedEvent> {
          MessageId = MessageId.New(),
          Payload = created,
          Hops = [CreateTestHop()]
        } as object,
        OrderShippedEvent shipped => new MessageEnvelope<OrderShippedEvent> {
          MessageId = MessageId.New(),
          Payload = shipped,
          Hops = [CreateTestHop()]
        } as object,
        _ => throw new InvalidOperationException("Unknown event type")
      };

      if (envelope is MessageEnvelope<OrderCreatedEvent> createdEnv) {
        await eventStore.AppendAsync(streamId, createdEnv);
        eventIds.Add(createdEnv.MessageId.Value);
      } else if (envelope is MessageEnvelope<OrderShippedEvent> shippedEnv) {
        await eventStore.AppendAsync(streamId, shippedEnv);
        eventIds.Add(shippedEnv.MessageId.Value);
      }
    }

    // Act - Get events between indices 1 and 3 (Shipped, Created, Shipped)
    var eventTypes = new List<Type> { typeof(OrderCreatedEvent), typeof(OrderShippedEvent) };
    var resultEvents = await eventStore.GetEventsBetweenPolymorphicAsync(
      streamId,
      afterEventId: eventIds[0],  // Exclusive (OrderCreated)
      upToEventId: eventIds[3],   // Inclusive (OrderShipped)
      eventTypes,
      CancellationToken.None
    );

    // Assert
    await Assert.That(resultEvents.Count).IsEqualTo(3);
    await Assert.That(resultEvents[0].Payload).IsTypeOf<OrderShippedEvent>();
    await Assert.That(resultEvents[1].Payload).IsTypeOf<OrderCreatedEvent>();
    await Assert.That(resultEvents[2].Payload).IsTypeOf<OrderShippedEvent>();

    // Verify concrete values
    var shippedEvent1 = (OrderShippedEvent)resultEvents[0].Payload;
    await Assert.That(shippedEvent1.TrackingNumber).IsEqualTo("TRACK001");

    var createdEvent2 = (OrderCreatedEvent)resultEvents[1].Payload;
    await Assert.That(createdEvent2.CustomerName).IsEqualTo("Customer 2");
  }

  /// <summary>
  /// Tests that GetEventsBetweenPolymorphicAsync with null afterEventId returns events from start.
  /// </summary>
  [Test]
  public async Task GetEventsBetweenPolymorphicAsync_NullAfterEventId_ReturnsFromStartAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();
    var orderId = Guid.NewGuid();
    var eventIds = new List<Guid>();

    // Append 3 mixed events
    var created1 = new MessageEnvelope<OrderCreatedEvent> {
      MessageId = MessageId.New(),
      Payload = new OrderCreatedEvent { OrderId = orderId, CustomerName = "Customer 1" },
      Hops = [CreateTestHop()]
    };
    await eventStore.AppendAsync(streamId, created1);
    eventIds.Add(created1.MessageId.Value);

    var shipped1 = new MessageEnvelope<OrderShippedEvent> {
      MessageId = MessageId.New(),
      Payload = new OrderShippedEvent { OrderId = orderId, TrackingNumber = "TRACK001" },
      Hops = [CreateTestHop()]
    };
    await eventStore.AppendAsync(streamId, shipped1);
    eventIds.Add(shipped1.MessageId.Value);

    // Act - Query from start
    var eventTypes = new List<Type> { typeof(OrderCreatedEvent), typeof(OrderShippedEvent) };
    var events = await eventStore.GetEventsBetweenPolymorphicAsync(
      streamId,
      afterEventId: null,         // Start from beginning
      upToEventId: eventIds[1],   // Inclusive
      eventTypes,
      CancellationToken.None
    );

    // Assert
    await Assert.That(events.Count).IsEqualTo(2);
    await Assert.That(events[0].Payload).IsTypeOf<OrderCreatedEvent>();
    await Assert.That(events[1].Payload).IsTypeOf<OrderShippedEvent>();
  }

  /// <summary>
  /// Tests that GetEventsBetweenPolymorphicAsync returns empty list when no events in range.
  /// </summary>
  [Test]
  public async Task GetEventsBetweenPolymorphicAsync_NoEventsInRange_ReturnsEmptyListAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();
    var firstEvent = new MessageEnvelope<OrderCreatedEvent> {
      MessageId = MessageId.New(),
      Payload = new OrderCreatedEvent {
        OrderId = Guid.NewGuid(),
        CustomerName = "Customer 0"
      },
      Hops = [CreateTestHop()]
    };
    await eventStore.AppendAsync(streamId, firstEvent);

    // Act - Query for events between same checkpoint (no events in range)
    var eventTypes = new List<Type> { typeof(OrderCreatedEvent), typeof(OrderShippedEvent) };
    var events = await eventStore.GetEventsBetweenPolymorphicAsync(
      streamId,
      afterEventId: firstEvent.MessageId.Value,
      upToEventId: firstEvent.MessageId.Value,
      eventTypes,
      CancellationToken.None
    );

    // Assert
    await Assert.That(events.Count).IsEqualTo(0);
  }

  /// <summary>
  /// Tests that GetEventsBetweenPolymorphicAsync skips events whose type is not in provided list.
  /// This is by design - a perspective doesn't need all events from a stream.
  /// </summary>
  [Test]
  public async Task GetEventsBetweenPolymorphicAsync_UnknownEventType_SkipsUnknownEventsAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();

    // Append a Created event
    var created = new MessageEnvelope<OrderCreatedEvent> {
      MessageId = MessageId.New(),
      Payload = new OrderCreatedEvent { OrderId = Guid.NewGuid(), CustomerName = "Test" },
      Hops = [CreateTestHop()]
    };
    await eventStore.AppendAsync(streamId, created);

    // Append a Shipped event
    var shipped = new MessageEnvelope<OrderShippedEvent> {
      MessageId = MessageId.New(),
      Payload = new OrderShippedEvent { OrderId = Guid.NewGuid(), TrackingNumber = "TRACK001" },
      Hops = [CreateTestHop()]
    };
    await eventStore.AppendAsync(streamId, shipped);

    // Act - Query with only OrderCreatedEvent type (missing OrderShippedEvent)
    var eventTypes = new List<Type> { typeof(OrderCreatedEvent) };

    var result = await eventStore.GetEventsBetweenPolymorphicAsync(
      streamId,
      afterEventId: null,
      upToEventId: shipped.MessageId.Value,
      eventTypes,
      CancellationToken.None
    );

    // Assert - Should only return the OrderCreatedEvent, skipping the OrderShippedEvent
    await Assert.That(result).Count().IsEqualTo(1);
    await Assert.That(result[0].Payload).IsTypeOf<OrderCreatedEvent>();
    await Assert.That(result[0].MessageId).IsEqualTo(created.MessageId);
  }

  private static MessageHop CreateTestHop() => new() {
    Type = HopType.Current,
    Timestamp = DateTime.UtcNow,
    ServiceInstance = new ServiceInstanceInfo {
      InstanceId = Guid.NewGuid(),
      ServiceName = "test-service",
      HostName = "test-host",
      ProcessId = 123
    }
  };

  // === Scope Restoration Tests ===

  [Test]
  [Category("Integration")]
  public async Task ReadPolymorphicAsync_WithScopeColumn_RestoresScopeToFirstHopAsync() {
    // Arrange: Insert event with scope column populated but hops without scope
    await using var context = CreateDbContext();
    var streamId = Guid.NewGuid();
    var messageId = MessageId.New();
    Guid eventId = messageId;

    var record = new EventStoreRecord {
      Id = eventId,
      StreamId = streamId,
      AggregateId = streamId,
      AggregateType = "TestAggregate",
      Version = 1,
      EventType = TypeNameFormatter.Format(typeof(OrderCreatedEvent)),
      EventData = System.Text.Json.JsonDocument.Parse(
        $"{{\"OrderId\":\"{streamId}\",\"CustomerName\":\"Test\"}}").RootElement,
      Metadata = new EnvelopeMetadata {
        MessageId = messageId,
        Hops = [CreateTestHop()]
      },
      Scope = new PerspectiveScope {
        TenantId = "tenant-abc",
        UserId = "user-xyz",
        CustomerId = "cust-123",
        OrganizationId = "org-456"
      },
      CreatedAt = DateTime.UtcNow
    };

    context.Set<EventStoreRecord>().Add(record);
    await context.SaveChangesAsync();
    context.ChangeTracker.Clear();

    // Act: Read back via ReadPolymorphicAsync
    await using var readContext = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(readContext);

    var envelopes = new List<MessageEnvelope<IEvent>>();
    await foreach (var env in eventStore.ReadPolymorphicAsync(
        streamId, null, [typeof(OrderCreatedEvent)])) {
      envelopes.Add(env);
    }

    // Assert: Scope should be restored on first hop
    await Assert.That(envelopes).Count().IsEqualTo(1);

    var envelope = envelopes[0];
    await Assert.That(envelope.Hops).Count().IsEqualTo(1);
    await Assert.That(envelope.Hops[0].Scope).IsNotNull();

    // Verify scope values via ScopeDelta.ApplyTo
    var scopeContext = envelope.Hops[0].Scope!.ApplyTo(null);
    await Assert.That(scopeContext.Scope.TenantId).IsEqualTo("tenant-abc");
    await Assert.That(scopeContext.Scope.UserId).IsEqualTo("user-xyz");
    await Assert.That(scopeContext.Scope.CustomerId).IsEqualTo("cust-123");
    await Assert.That(scopeContext.Scope.OrganizationId).IsEqualTo("org-456");
  }

  [Test]
  [Category("Integration")]
  public async Task ReadPolymorphicAsync_WithScopeAlreadyInHops_DoesNotOverwriteAsync() {
    // Arrange: Insert event with scope in BOTH the scope column and hops
    await using var context = CreateDbContext();
    var streamId = Guid.NewGuid();
    var messageId = MessageId.New();
    Guid eventId = messageId;

    var existingScope = ScopeDelta.FromPerspectiveScope(new PerspectiveScope {
      TenantId = "original-tenant"
    });

    var hop = CreateTestHop() with { Scope = existingScope };

    var record = new EventStoreRecord {
      Id = eventId,
      StreamId = streamId,
      AggregateId = streamId,
      AggregateType = "TestAggregate",
      Version = 1,
      EventType = TypeNameFormatter.Format(typeof(OrderCreatedEvent)),
      EventData = System.Text.Json.JsonDocument.Parse(
        $"{{\"OrderId\":\"{streamId}\",\"CustomerName\":\"Test\"}}").RootElement,
      Metadata = new EnvelopeMetadata {
        MessageId = messageId,
        Hops = [hop]
      },
      Scope = new PerspectiveScope {
        TenantId = "column-tenant"
      },
      CreatedAt = DateTime.UtcNow
    };

    context.Set<EventStoreRecord>().Add(record);
    await context.SaveChangesAsync();
    context.ChangeTracker.Clear();

    // Act
    await using var readContext = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(readContext);

    var envelopes = new List<MessageEnvelope<IEvent>>();
    await foreach (var env in eventStore.ReadPolymorphicAsync(
        streamId, null, [typeof(OrderCreatedEvent)])) {
      envelopes.Add(env);
    }

    // Assert: Original hop scope should be preserved (not overwritten by column scope)
    await Assert.That(envelopes).Count().IsEqualTo(1);

    var envelope = envelopes[0];
    var scopeContext = envelope.Hops[0].Scope!.ApplyTo(null);
    await Assert.That(scopeContext.Scope.TenantId).IsEqualTo("original-tenant");
  }

  [Test]
  [Category("Integration")]
  public async Task ReadPolymorphicAsync_WithNullScopeColumn_ReturnsEnvelopeWithoutScopeAsync() {
    // Arrange: Insert event with null scope column (backward compat with old data)
    await using var context = CreateDbContext();
    var streamId = Guid.NewGuid();
    var messageId = MessageId.New();
    Guid eventId = messageId;

    var record = new EventStoreRecord {
      Id = eventId,
      StreamId = streamId,
      AggregateId = streamId,
      AggregateType = "TestAggregate",
      Version = 1,
      EventType = TypeNameFormatter.Format(typeof(OrderCreatedEvent)),
      EventData = System.Text.Json.JsonDocument.Parse(
        $"{{\"OrderId\":\"{streamId}\",\"CustomerName\":\"Test\"}}").RootElement,
      Metadata = new EnvelopeMetadata {
        MessageId = messageId,
        Hops = [CreateTestHop()]
      },
      Scope = null,
      CreatedAt = DateTime.UtcNow
    };

    context.Set<EventStoreRecord>().Add(record);
    await context.SaveChangesAsync();
    context.ChangeTracker.Clear();

    // Act
    await using var readContext = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(readContext);

    var envelopes = new List<MessageEnvelope<IEvent>>();
    await foreach (var env in eventStore.ReadPolymorphicAsync(
        streamId, null, [typeof(OrderCreatedEvent)])) {
      envelopes.Add(env);
    }

    // Assert: No scope injected, hop scope remains null
    await Assert.That(envelopes).Count().IsEqualTo(1);
    await Assert.That(envelopes[0].Hops[0].Scope).IsNull();
  }

  // === Constructor Tests ===

  [Test]
  public async Task Constructor_WithNullContext_ThrowsArgumentNullExceptionAsync() {
    // Act & Assert
    var action = () => new EFCoreEventStore<WorkCoordinationDbContext>(null!);
    await Assert.That(action).ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task Constructor_WithNullJsonOptions_UsesDefaultOptionsAsync() {
    // Arrange & Act
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context, jsonOptions: null);

    // Assert - should work without error (uses default options)
    var streamId = Guid.NewGuid();
    var lastSeq = await eventStore.GetLastSequenceAsync(streamId);
    await Assert.That(lastSeq).IsEqualTo(-1);
  }

  // === AppendAsync Null Argument Tests ===

  [Test]
  public async Task AppendAsync_WithNullEnvelope_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    // Act & Assert
    var action = () => eventStore.AppendAsync<OrderCreatedEvent>(Guid.NewGuid(), (MessageEnvelope<OrderCreatedEvent>)null!);
    await Assert.That(action).ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task AppendAsync_WithNullMessage_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    // Act & Assert
    var action = () => eventStore.AppendAsync<OrderCreatedEvent>(Guid.NewGuid(), (OrderCreatedEvent)null!);
    await Assert.That(action).ThrowsExactly<ArgumentNullException>();
  }

  // === AppendAsync Raw Message Overload Tests ===

  [Test]
  public async Task AppendAsync_WithRawMessage_CreatesEnvelopeAndAppendsAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();
    var message = new OrderCreatedEvent {
      OrderId = Guid.NewGuid(),
      CustomerName = "Jane Doe"
    };

    // Act
    await eventStore.AppendAsync(streamId, message);

    // Assert
    var records = context.Set<EventStoreRecord>().Where(e => e.StreamId == streamId).ToList();
    await Assert.That(records).Count().IsEqualTo(1);
    await Assert.That(records[0].StreamId).IsEqualTo(streamId);
    await Assert.That(records[0].Version).IsEqualTo(0);
  }

  [Test]
  public async Task AppendAsync_WithRawMessage_CreatesMinimalEnvelopeWithHopAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();
    var message = new OrderCreatedEvent {
      OrderId = Guid.NewGuid(),
      CustomerName = "Jane Doe"
    };

    // Act
    await eventStore.AppendAsync(streamId, message);

    // Assert - metadata should have at least one hop
    var record = context.Set<EventStoreRecord>().Single(e => e.StreamId == streamId);
    await Assert.That(record.Metadata.Hops).Count().IsEqualTo(1);
    await Assert.That(record.Metadata.Hops[0].ServiceInstance.ServiceName).IsEqualTo("Unknown");
  }

  // === AppendAsync with Null Hops Tests ===

  [Test]
  public async Task AppendAsync_WithNullHopsInEnvelope_CoalescesToEmptyListAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();
    var envelope = new MessageEnvelope<OrderCreatedEvent> {
      MessageId = MessageId.New(),
      Payload = new OrderCreatedEvent {
        OrderId = Guid.NewGuid(),
        CustomerName = "No Hops"
      },
      Hops = null!
    };

    // Act
    await eventStore.AppendAsync(streamId, envelope);

    // Assert
    var record = context.Set<EventStoreRecord>().Single(e => e.StreamId == streamId);
    await Assert.That(record.Metadata.Hops).Count().IsEqualTo(0);
  }

  // === ReadAsync by EventId Overload Tests ===

  [Test]
  public async Task ReadAsync_ByEventId_WithNullFromEventId_ReturnsAllEventsAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();

    for (int i = 0; i < 3; i++) {
      var envelope = new MessageEnvelope<OrderCreatedEvent> {
        MessageId = MessageId.New(),
        Payload = new OrderCreatedEvent {
          OrderId = Guid.NewGuid(),
          CustomerName = $"Customer {i}"
        },
        Hops = [CreateTestHop()]
      };
      await eventStore.AppendAsync(streamId, envelope);
    }

    // Act
    var events = new List<MessageEnvelope<OrderCreatedEvent>>();
    await foreach (var evt in eventStore.ReadAsync<OrderCreatedEvent>(streamId, fromEventId: null)) {
      events.Add(evt);
    }

    // Assert
    await Assert.That(events).Count().IsEqualTo(3);
    await Assert.That(events[0].Payload.CustomerName).IsEqualTo("Customer 0");
    await Assert.That(events[1].Payload.CustomerName).IsEqualTo("Customer 1");
    await Assert.That(events[2].Payload.CustomerName).IsEqualTo("Customer 2");
  }

  [Test]
  public async Task ReadAsync_ByEventId_WithFromEventId_ReturnsEventsAfterIdAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();
    var eventIds = new List<Guid>();

    for (int i = 0; i < 3; i++) {
      var envelope = new MessageEnvelope<OrderCreatedEvent> {
        MessageId = MessageId.New(),
        Payload = new OrderCreatedEvent {
          OrderId = Guid.NewGuid(),
          CustomerName = $"Customer {i}"
        },
        Hops = [CreateTestHop()]
      };
      await eventStore.AppendAsync(streamId, envelope);
      eventIds.Add(envelope.MessageId.Value);
    }

    // Act - Read starting after the first event
    var events = new List<MessageEnvelope<OrderCreatedEvent>>();
    await foreach (var evt in eventStore.ReadAsync<OrderCreatedEvent>(streamId, fromEventId: eventIds[0])) {
      events.Add(evt);
    }

    // Assert - should only return events after the first one
    await Assert.That(events).Count().IsEqualTo(2);
    await Assert.That(events[0].Payload.CustomerName).IsEqualTo("Customer 1");
    await Assert.That(events[1].Payload.CustomerName).IsEqualTo("Customer 2");
  }

  [Test]
  public async Task ReadAsync_ByEventId_WithNoEventsAfterEventId_ReturnsEmptyAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();
    var envelope = new MessageEnvelope<OrderCreatedEvent> {
      MessageId = MessageId.New(),
      Payload = new OrderCreatedEvent {
        OrderId = Guid.NewGuid(),
        CustomerName = "Only Event"
      },
      Hops = [CreateTestHop()]
    };
    await eventStore.AppendAsync(streamId, envelope);

    // Act - Read starting after the only event
    var events = new List<MessageEnvelope<OrderCreatedEvent>>();
    await foreach (var evt in eventStore.ReadAsync<OrderCreatedEvent>(streamId, fromEventId: envelope.MessageId.Value)) {
      events.Add(evt);
    }

    // Assert
    await Assert.That(events).Count().IsEqualTo(0);
  }

  // === ReadAsync by Sequence - Partial Read Tests ===

  [Test]
  public async Task ReadAsync_FromMiddleSequence_ReturnsSubsetOfEventsAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();

    for (int i = 0; i < 5; i++) {
      var envelope = new MessageEnvelope<OrderCreatedEvent> {
        MessageId = MessageId.New(),
        Payload = new OrderCreatedEvent {
          OrderId = Guid.NewGuid(),
          CustomerName = $"Customer {i}"
        },
        Hops = [CreateTestHop()]
      };
      await eventStore.AppendAsync(streamId, envelope);
    }

    // Act - Read from sequence 3 onwards
    var events = new List<MessageEnvelope<OrderCreatedEvent>>();
    await foreach (var evt in eventStore.ReadAsync<OrderCreatedEvent>(streamId, fromSequence: 3)) {
      events.Add(evt);
    }

    // Assert - Should return events at sequence 3 and 4
    await Assert.That(events).Count().IsEqualTo(2);
    await Assert.That(events[0].Payload.CustomerName).IsEqualTo("Customer 3");
    await Assert.That(events[1].Payload.CustomerName).IsEqualTo("Customer 4");
  }

  // === ReadPolymorphicAsync Tests ===

  [Test]
  [Category("Integration")]
  public async Task ReadPolymorphicAsync_WithFromEventId_ReturnsEventsAfterIdAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();
    var eventIds = new List<Guid>();

    var created = new MessageEnvelope<OrderCreatedEvent> {
      MessageId = MessageId.New(),
      Payload = new OrderCreatedEvent { OrderId = streamId, CustomerName = "Customer 1" },
      Hops = [CreateTestHop()]
    };
    await eventStore.AppendAsync(streamId, created);
    eventIds.Add(created.MessageId.Value);

    var shipped = new MessageEnvelope<OrderShippedEvent> {
      MessageId = MessageId.New(),
      Payload = new OrderShippedEvent { OrderId = streamId, TrackingNumber = "TRACK001" },
      Hops = [CreateTestHop()]
    };
    await eventStore.AppendAsync(streamId, shipped);
    eventIds.Add(shipped.MessageId.Value);

    // Act - Read after first event
    var envelopes = new List<MessageEnvelope<IEvent>>();
    await foreach (var env in eventStore.ReadPolymorphicAsync(
        streamId, fromEventId: eventIds[0], [typeof(OrderCreatedEvent), typeof(OrderShippedEvent)])) {
      envelopes.Add(env);
    }

    // Assert - only the shipped event should be returned
    await Assert.That(envelopes).Count().IsEqualTo(1);
    await Assert.That(envelopes[0].Payload).IsTypeOf<OrderShippedEvent>();
  }

  [Test]
  [Category("Integration")]
  public async Task ReadPolymorphicAsync_WithUnknownEventType_SkipsUnknownEventsAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();

    var created = new MessageEnvelope<OrderCreatedEvent> {
      MessageId = MessageId.New(),
      Payload = new OrderCreatedEvent { OrderId = streamId, CustomerName = "Customer 1" },
      Hops = [CreateTestHop()]
    };
    await eventStore.AppendAsync(streamId, created);

    var shipped = new MessageEnvelope<OrderShippedEvent> {
      MessageId = MessageId.New(),
      Payload = new OrderShippedEvent { OrderId = streamId, TrackingNumber = "TRACK001" },
      Hops = [CreateTestHop()]
    };
    await eventStore.AppendAsync(streamId, shipped);

    // Act - Only request OrderCreatedEvent type, not OrderShippedEvent
    var envelopes = new List<MessageEnvelope<IEvent>>();
    await foreach (var env in eventStore.ReadPolymorphicAsync(
        streamId, null, [typeof(OrderCreatedEvent)])) {
      envelopes.Add(env);
    }

    // Assert - should skip the shipped event
    await Assert.That(envelopes).Count().IsEqualTo(1);
    await Assert.That(envelopes[0].Payload).IsTypeOf<OrderCreatedEvent>();
  }

  // === GetEventsBetweenAsync with Guid.Empty Tests ===

  [Test]
  public async Task GetEventsBetweenAsync_WithGuidEmptyUpToEventId_ReturnsAllEventsInStreamAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();

    for (int i = 0; i < 3; i++) {
      var envelope = new MessageEnvelope<OrderCreatedEvent> {
        MessageId = MessageId.New(),
        Payload = new OrderCreatedEvent {
          OrderId = Guid.NewGuid(),
          CustomerName = $"Customer {i}"
        },
        Hops = [CreateTestHop()]
      };
      await eventStore.AppendAsync(streamId, envelope);
    }

    // Act - Use Guid.Empty to mean "no upper bound"
    var events = await eventStore.GetEventsBetweenAsync<OrderCreatedEvent>(
      streamId,
      afterEventId: null,
      upToEventId: Guid.Empty,
      CancellationToken.None
    );

    // Assert - Should return all events (no upper bound)
    await Assert.That(events.Count).IsEqualTo(3);
  }

  // === GetEventsBetweenPolymorphicAsync Additional Tests ===

  [Test]
  public async Task GetEventsBetweenPolymorphicAsync_WithNullEventTypes_ThrowsArgumentNullExceptionAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    // Act & Assert
    var action = () => eventStore.GetEventsBetweenPolymorphicAsync(
      Guid.NewGuid(),
      afterEventId: null,
      upToEventId: Guid.NewGuid(),
      eventTypes: null!,
      CancellationToken.None
    );
    await Assert.That(action).ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task GetEventsBetweenPolymorphicAsync_WithGuidEmptyUpToEventId_ReturnsAllEventsAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();

    var created = new MessageEnvelope<OrderCreatedEvent> {
      MessageId = MessageId.New(),
      Payload = new OrderCreatedEvent { OrderId = streamId, CustomerName = "Customer 1" },
      Hops = [CreateTestHop()]
    };
    await eventStore.AppendAsync(streamId, created);

    var shipped = new MessageEnvelope<OrderShippedEvent> {
      MessageId = MessageId.New(),
      Payload = new OrderShippedEvent { OrderId = streamId, TrackingNumber = "TRACK001" },
      Hops = [CreateTestHop()]
    };
    await eventStore.AppendAsync(streamId, shipped);

    // Act - Use Guid.Empty to mean "no upper bound"
    var eventTypes = new List<Type> { typeof(OrderCreatedEvent), typeof(OrderShippedEvent) };
    var events = await eventStore.GetEventsBetweenPolymorphicAsync(
      streamId,
      afterEventId: null,
      upToEventId: Guid.Empty,
      eventTypes,
      CancellationToken.None
    );

    // Assert - Should return all events (no upper bound filter)
    await Assert.That(events.Count).IsEqualTo(2);
  }

  // === Scope Restoration Edge Case Tests ===

  [Test]
  [Category("Integration")]
  public async Task ReadAsync_BySequence_WithScopeColumn_RestoresScopeToFirstHopAsync() {
    // Arrange: Insert event with scope column populated but hops without scope
    await using var context = CreateDbContext();
    var streamId = Guid.NewGuid();
    var messageId = MessageId.New();
    Guid eventId = messageId;

    var record = new EventStoreRecord {
      Id = eventId,
      StreamId = streamId,
      AggregateId = streamId,
      AggregateType = "TestAggregate",
      Version = 1,
      EventType = TypeNameFormatter.Format(typeof(OrderCreatedEvent)),
      EventData = System.Text.Json.JsonDocument.Parse(
        $"{{\"OrderId\":\"{streamId}\",\"CustomerName\":\"Test\"}}").RootElement,
      Metadata = new EnvelopeMetadata {
        MessageId = messageId,
        Hops = [CreateTestHop()]
      },
      Scope = new PerspectiveScope {
        TenantId = "tenant-scope-test"
      },
      CreatedAt = DateTime.UtcNow
    };

    context.Set<EventStoreRecord>().Add(record);
    await context.SaveChangesAsync();
    context.ChangeTracker.Clear();

    // Act: Read back via sequence-based ReadAsync
    await using var readContext = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(readContext);

    var envelopes = new List<MessageEnvelope<OrderCreatedEvent>>();
    await foreach (var env in eventStore.ReadAsync<OrderCreatedEvent>(streamId, fromSequence: 0)) {
      envelopes.Add(env);
    }

    // Assert: Scope should be restored on first hop
    await Assert.That(envelopes).Count().IsEqualTo(1);
    await Assert.That(envelopes[0].Hops[0].Scope).IsNotNull();
    var scopeContext = envelopes[0].Hops[0].Scope!.ApplyTo(null);
    await Assert.That(scopeContext.Scope.TenantId).IsEqualTo("tenant-scope-test");
  }

  [Test]
  [Category("Integration")]
  public async Task ReadAsync_ByEventId_WithScopeColumn_RestoresScopeToFirstHopAsync() {
    // Arrange: Insert event with scope column populated but hops without scope
    await using var context = CreateDbContext();
    var streamId = Guid.NewGuid();
    var messageId = MessageId.New();
    Guid eventId = messageId;

    var record = new EventStoreRecord {
      Id = eventId,
      StreamId = streamId,
      AggregateId = streamId,
      AggregateType = "TestAggregate",
      Version = 1,
      EventType = TypeNameFormatter.Format(typeof(OrderCreatedEvent)),
      EventData = System.Text.Json.JsonDocument.Parse(
        $"{{\"OrderId\":\"{streamId}\",\"CustomerName\":\"Test\"}}").RootElement,
      Metadata = new EnvelopeMetadata {
        MessageId = messageId,
        Hops = [CreateTestHop()]
      },
      Scope = new PerspectiveScope {
        TenantId = "tenant-eventid-test"
      },
      CreatedAt = DateTime.UtcNow
    };

    context.Set<EventStoreRecord>().Add(record);
    await context.SaveChangesAsync();
    context.ChangeTracker.Clear();

    // Act: Read back via eventId-based ReadAsync
    await using var readContext = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(readContext);

    var envelopes = new List<MessageEnvelope<OrderCreatedEvent>>();
    await foreach (var env in eventStore.ReadAsync<OrderCreatedEvent>(streamId, fromEventId: null)) {
      envelopes.Add(env);
    }

    // Assert: Scope should be restored on first hop
    await Assert.That(envelopes).Count().IsEqualTo(1);
    await Assert.That(envelopes[0].Hops[0].Scope).IsNotNull();
    var scopeContext = envelopes[0].Hops[0].Scope!.ApplyTo(null);
    await Assert.That(scopeContext.Scope.TenantId).IsEqualTo("tenant-eventid-test");
  }

  [Test]
  [Category("Integration")]
  public async Task RestoreScopeInHops_WithEmptyHopsList_ReturnsEmptyListAsync() {
    // Arrange: Insert event with scope column but empty hops list
    await using var context = CreateDbContext();
    var streamId = Guid.NewGuid();
    var messageId = MessageId.New();
    Guid eventId = messageId;

    var record = new EventStoreRecord {
      Id = eventId,
      StreamId = streamId,
      AggregateId = streamId,
      AggregateType = "TestAggregate",
      Version = 1,
      EventType = TypeNameFormatter.Format(typeof(OrderCreatedEvent)),
      EventData = System.Text.Json.JsonDocument.Parse(
        $"{{\"OrderId\":\"{streamId}\",\"CustomerName\":\"Test\"}}").RootElement,
      Metadata = new EnvelopeMetadata {
        MessageId = messageId,
        Hops = []
      },
      Scope = new PerspectiveScope {
        TenantId = "tenant-abc"
      },
      CreatedAt = DateTime.UtcNow
    };

    context.Set<EventStoreRecord>().Add(record);
    await context.SaveChangesAsync();
    context.ChangeTracker.Clear();

    // Act
    await using var readContext = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(readContext);

    var envelopes = new List<MessageEnvelope<IEvent>>();
    await foreach (var env in eventStore.ReadPolymorphicAsync(
        streamId, null, [typeof(OrderCreatedEvent)])) {
      envelopes.Add(env);
    }

    // Assert: Empty hops returned (scope column ignored when no hops to attach to)
    await Assert.That(envelopes).Count().IsEqualTo(1);
    await Assert.That(envelopes[0].Hops).Count().IsEqualTo(0);
  }

  [Test]
  [Category("Integration")]
  public async Task RestoreScopeInHops_WithEmptyScopeFields_DoesNotInjectScopeAsync() {
    // Arrange: Insert event with scope column that has all-null fields
    await using var context = CreateDbContext();
    var streamId = Guid.NewGuid();
    var messageId = MessageId.New();
    Guid eventId = messageId;

    var record = new EventStoreRecord {
      Id = eventId,
      StreamId = streamId,
      AggregateId = streamId,
      AggregateType = "TestAggregate",
      Version = 1,
      EventType = TypeNameFormatter.Format(typeof(OrderCreatedEvent)),
      EventData = System.Text.Json.JsonDocument.Parse(
        $"{{\"OrderId\":\"{streamId}\",\"CustomerName\":\"Test\"}}").RootElement,
      Metadata = new EnvelopeMetadata {
        MessageId = messageId,
        Hops = [CreateTestHop()]
      },
      Scope = new PerspectiveScope {
        // All fields null/empty - ScopeDelta.FromPerspectiveScope will return null
      },
      CreatedAt = DateTime.UtcNow
    };

    context.Set<EventStoreRecord>().Add(record);
    await context.SaveChangesAsync();
    context.ChangeTracker.Clear();

    // Act
    await using var readContext = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(readContext);

    var envelopes = new List<MessageEnvelope<IEvent>>();
    await foreach (var env in eventStore.ReadPolymorphicAsync(
        streamId, null, [typeof(OrderCreatedEvent)])) {
      envelopes.Add(env);
    }

    // Assert: Scope should remain null because ScopeDelta.FromPerspectiveScope returns null for empty scope
    await Assert.That(envelopes).Count().IsEqualTo(1);
    await Assert.That(envelopes[0].Hops[0].Scope).IsNull();
  }

  // === ReadAsync Uses record.Id as MessageId Tests ===

  [Test]
  [Category("Integration")]
  public async Task ReadAsync_BySequence_UsesRecordIdAsMessageIdAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();
    var envelope = new MessageEnvelope<OrderCreatedEvent> {
      MessageId = MessageId.New(),
      Payload = new OrderCreatedEvent {
        OrderId = Guid.NewGuid(),
        CustomerName = "Test"
      },
      Hops = [CreateTestHop()]
    };

    await eventStore.AppendAsync(streamId, envelope);

    // Act
    var events = new List<MessageEnvelope<OrderCreatedEvent>>();
    await foreach (var evt in eventStore.ReadAsync<OrderCreatedEvent>(streamId, fromSequence: 0)) {
      events.Add(evt);
    }

    // Assert - MessageId should match the record.Id (which is envelope.MessageId.Value)
    await Assert.That(events).Count().IsEqualTo(1);
    await Assert.That(events[0].MessageId).IsEqualTo(envelope.MessageId);
  }

  [Test]
  [Category("Integration")]
  public async Task ReadAsync_ByEventId_UsesRecordIdAsMessageIdAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();
    var envelope = new MessageEnvelope<OrderCreatedEvent> {
      MessageId = MessageId.New(),
      Payload = new OrderCreatedEvent {
        OrderId = Guid.NewGuid(),
        CustomerName = "Test"
      },
      Hops = [CreateTestHop()]
    };

    await eventStore.AppendAsync(streamId, envelope);

    // Act
    var events = new List<MessageEnvelope<OrderCreatedEvent>>();
    await foreach (var evt in eventStore.ReadAsync<OrderCreatedEvent>(streamId, fromEventId: null)) {
      events.Add(evt);
    }

    // Assert - MessageId should match the record.Id
    await Assert.That(events).Count().IsEqualTo(1);
    await Assert.That(events[0].MessageId).IsEqualTo(envelope.MessageId);
  }

  // === GetEventsBetweenAsync Scope Restoration Tests ===

  [Test]
  [Category("Integration")]
  public async Task GetEventsBetweenAsync_WithScopeColumn_RestoresScopeAsync() {
    // Arrange: Insert event with scope column
    await using var context = CreateDbContext();
    var streamId = Guid.NewGuid();
    var messageId = MessageId.New();
    Guid eventId = messageId;

    var record = new EventStoreRecord {
      Id = eventId,
      StreamId = streamId,
      AggregateId = streamId,
      AggregateType = "TestAggregate",
      Version = 1,
      EventType = TypeNameFormatter.Format(typeof(OrderCreatedEvent)),
      EventData = System.Text.Json.JsonDocument.Parse(
        $"{{\"OrderId\":\"{streamId}\",\"CustomerName\":\"Test\"}}").RootElement,
      Metadata = new EnvelopeMetadata {
        MessageId = messageId,
        Hops = [CreateTestHop()]
      },
      Scope = new PerspectiveScope {
        TenantId = "tenant-between-test"
      },
      CreatedAt = DateTime.UtcNow
    };

    context.Set<EventStoreRecord>().Add(record);
    await context.SaveChangesAsync();
    context.ChangeTracker.Clear();

    // Act
    await using var readContext = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(readContext);

    var events = await eventStore.GetEventsBetweenAsync<OrderCreatedEvent>(
      streamId,
      afterEventId: null,
      upToEventId: eventId,
      CancellationToken.None
    );

    // Assert
    await Assert.That(events.Count).IsEqualTo(1);
    await Assert.That(events[0].Hops[0].Scope).IsNotNull();
    var scopeContext = events[0].Hops[0].Scope!.ApplyTo(null);
    await Assert.That(scopeContext.Scope.TenantId).IsEqualTo("tenant-between-test");
  }

  // === ReadAsync from empty stream Tests ===

  [Test]
  public async Task ReadAsync_BySequence_FromEmptyStream_ReturnsEmptyAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    // Act
    var events = new List<MessageEnvelope<OrderCreatedEvent>>();
    await foreach (var evt in eventStore.ReadAsync<OrderCreatedEvent>(Guid.NewGuid(), fromSequence: 0)) {
      events.Add(evt);
    }

    // Assert
    await Assert.That(events).Count().IsEqualTo(0);
  }

  [Test]
  public async Task ReadAsync_ByEventId_FromEmptyStream_ReturnsEmptyAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    // Act
    var events = new List<MessageEnvelope<OrderCreatedEvent>>();
    await foreach (var evt in eventStore.ReadAsync<OrderCreatedEvent>(Guid.NewGuid(), fromEventId: null)) {
      events.Add(evt);
    }

    // Assert
    await Assert.That(events).Count().IsEqualTo(0);
  }

  [Test]
  [Category("Integration")]
  public async Task ReadPolymorphicAsync_FromEmptyStream_ReturnsEmptyAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    // Act
    var envelopes = new List<MessageEnvelope<IEvent>>();
    await foreach (var env in eventStore.ReadPolymorphicAsync(
        Guid.NewGuid(), null, [typeof(OrderCreatedEvent)])) {
      envelopes.Add(env);
    }

    // Assert
    await Assert.That(envelopes).Count().IsEqualTo(0);
  }

  // === EventType Format Tests ===

  [Test]
  public async Task AppendAsync_SetsEventTypeUsingTypeNameFormatterAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();
    var envelope = new MessageEnvelope<OrderCreatedEvent> {
      MessageId = MessageId.New(),
      Payload = new OrderCreatedEvent {
        OrderId = Guid.NewGuid(),
        CustomerName = "Test"
      },
      Hops = [CreateTestHop()]
    };

    // Act
    await eventStore.AppendAsync(streamId, envelope);

    // Assert - EventType should match TypeNameFormatter format
    var record = context.Set<EventStoreRecord>().Single(e => e.StreamId == streamId);
    var expectedTypeName = TypeNameFormatter.Format(typeof(OrderCreatedEvent));
    await Assert.That(record.EventType).IsEqualTo(expectedTypeName);
  }

  [Test]
  public async Task AppendAsync_SetsAggregateIdToStreamIdAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();
    var envelope = new MessageEnvelope<OrderCreatedEvent> {
      MessageId = MessageId.New(),
      Payload = new OrderCreatedEvent {
        OrderId = Guid.NewGuid(),
        CustomerName = "Test"
      },
      Hops = [CreateTestHop()]
    };

    // Act
    await eventStore.AppendAsync(streamId, envelope);

    // Assert - AggregateId should equal StreamId for backwards compatibility
    var record = context.Set<EventStoreRecord>().Single(e => e.StreamId == streamId);
    await Assert.That(record.AggregateId).IsEqualTo(streamId);
  }

  [Test]
  public async Task AppendAsync_SetsRecordIdToMessageIdValueAsync() {
    // Arrange
    await using var context = CreateDbContext();
    var eventStore = new EFCoreEventStore<WorkCoordinationDbContext>(context);

    var streamId = Guid.NewGuid();
    var messageId = MessageId.New();
    var envelope = new MessageEnvelope<OrderCreatedEvent> {
      MessageId = messageId,
      Payload = new OrderCreatedEvent {
        OrderId = Guid.NewGuid(),
        CustomerName = "Test"
      },
      Hops = [CreateTestHop()]
    };

    // Act
    await eventStore.AppendAsync(streamId, envelope);

    // Assert - Record Id should match the envelope's MessageId
    var record = context.Set<EventStoreRecord>().Single(e => e.StreamId == streamId);
    await Assert.That(record.Id).IsEqualTo(messageId.Value);
  }
}
