using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Sample event for testing event store functionality.
/// </summary>
public record OrderCreatedEvent : IEvent {
  [StreamKey]
  public required Guid OrderId { get; init; }
  public required string CustomerName { get; init; }
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
      Hops = new List<MessageHop> {
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
      }
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
        Hops = new List<MessageHop> {
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
        }
      };
      await eventStore.AppendAsync(streamId, envelope);
    }

    // Assert
    var events = context.Set<EventStoreRecord>()
      .Where(e => e.StreamId == streamId)
      .OrderBy(e => e.Sequence)
      .ToList();

    await Assert.That(events).Count().IsEqualTo(3);
    await Assert.That(events[0].Sequence).IsEqualTo(0);
    await Assert.That(events[1].Sequence).IsEqualTo(1);
    await Assert.That(events[2].Sequence).IsEqualTo(2);
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
        Hops = new List<MessageHop> {
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
        }
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
        Hops = new List<MessageHop> {
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
        }
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
        Hops = new List<MessageHop> {
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
        }
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
        Hops = new List<MessageHop> {
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
        }
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
      Hops = new List<MessageHop> {
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
      }
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
        Hops = new List<MessageHop> {
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
        }
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
}
