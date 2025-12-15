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
  public required Guid OrderId { get; init; }
  public required string CustomerName { get; init; }
}

/// <summary>
/// Test DbContext for EFCoreEventStore tests.
/// </summary>
public class EventStoreTestDbContext : DbContext {
  public EventStoreTestDbContext(DbContextOptions<EventStoreTestDbContext> options) : base(options) { }

  protected override void OnModelCreating(ModelBuilder modelBuilder) {
    modelBuilder.Entity<EventStoreRecord>(entity => {
      entity.ToTable("wh_events");
      entity.HasKey(e => e.Id);

      entity.Property(e => e.Id).HasColumnName("event_id");
      entity.Property(e => e.StreamId).HasColumnName("stream_id").IsRequired();
      entity.Property(e => e.AggregateId).HasColumnName("aggregate_id").IsRequired();
      entity.Property(e => e.AggregateType).HasColumnName("aggregate_type").IsRequired();
      entity.Property(e => e.Sequence).HasColumnName("sequence_number").IsRequired();
      entity.Property(e => e.Version).HasColumnName("version").IsRequired();
      entity.Property(e => e.EventType).HasColumnName("event_type").IsRequired();
      entity.Property(e => e.EventData).HasColumnName("event_data").IsRequired().HasColumnType("jsonb");
      entity.Property(e => e.Metadata).HasColumnName("metadata").IsRequired().HasColumnType("jsonb");
      entity.Property(e => e.Scope).HasColumnName("scope").HasColumnType("jsonb");
      entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

      entity.HasIndex(e => new { e.StreamId, e.Version }).IsUnique();
      entity.HasIndex(e => new { e.AggregateId, e.Version }).IsUnique();
      entity.HasIndex(e => new { e.StreamId, e.Sequence }).IsUnique();
    });
  }
}

/// <summary>
/// Tests for EFCoreEventStore.
/// Verifies append-only event storage with stream-based organization and sequence numbers.
/// Target: 100% branch coverage.
/// </summary>
public class EFCoreEventStoreTests {
  [Test]
  public async Task AppendAsync_WithValidEnvelope_AppendsEventToStreamAsync() {
    // Arrange
    var options = new DbContextOptionsBuilder<EventStoreTestDbContext>()
      .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
      .Options;

    await using var context = new EventStoreTestDbContext(options);
    var eventStore = new EFCoreEventStore<EventStoreTestDbContext>(context);

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
    await Assert.That(events).HasCount().EqualTo(1);
    await Assert.That(events[0].StreamId).IsEqualTo(streamId);
  }

  [Test]
  public async Task AppendAsync_WithMultipleEvents_AssignsSequentialSequenceNumbersAsync() {
    // Arrange
    var options = new DbContextOptionsBuilder<EventStoreTestDbContext>()
      .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
      .Options;

    await using var context = new EventStoreTestDbContext(options);
    var eventStore = new EFCoreEventStore<EventStoreTestDbContext>(context);

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

    await Assert.That(events).HasCount().EqualTo(3);
    await Assert.That(events[0].Sequence).IsEqualTo(0);
    await Assert.That(events[1].Sequence).IsEqualTo(1);
    await Assert.That(events[2].Sequence).IsEqualTo(2);
  }

  [Test]
  public async Task ReadAsync_WithExistingEvents_ReturnsEventsInSequenceOrderAsync() {
    // Arrange
    var options = new DbContextOptionsBuilder<EventStoreTestDbContext>()
      .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
      .Options;

    await using var context = new EventStoreTestDbContext(options);
    var eventStore = new EFCoreEventStore<EventStoreTestDbContext>(context);

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
    await Assert.That(events).HasCount().EqualTo(3);
    await Assert.That(events[0].Payload.CustomerName).IsEqualTo("Customer 0");
    await Assert.That(events[1].Payload.CustomerName).IsEqualTo("Customer 1");
    await Assert.That(events[2].Payload.CustomerName).IsEqualTo("Customer 2");
  }

  [Test]
  public async Task GetLastSequenceAsync_WithEmptyStream_ReturnsMinusOneAsync() {
    // Arrange
    var options = new DbContextOptionsBuilder<EventStoreTestDbContext>()
      .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
      .Options;

    await using var context = new EventStoreTestDbContext(options);
    var eventStore = new EFCoreEventStore<EventStoreTestDbContext>(context);

    var streamId = Guid.NewGuid();

    // Act
    var lastSequence = await eventStore.GetLastSequenceAsync(streamId);

    // Assert
    await Assert.That(lastSequence).IsEqualTo(-1);
  }

  [Test]
  public async Task GetLastSequenceAsync_WithExistingEvents_ReturnsHighestSequenceAsync() {
    // Arrange
    var options = new DbContextOptionsBuilder<EventStoreTestDbContext>()
      .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
      .Options;

    await using var context = new EventStoreTestDbContext(options);
    var eventStore = new EFCoreEventStore<EventStoreTestDbContext>(context);

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
}
