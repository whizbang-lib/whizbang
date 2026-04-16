using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Serialization;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Tests for EFCoreEventStore.DeserializeStreamEvents — validates that StreamEventData (from SQL)
/// can be deserialized into typed MessageEnvelope&lt;IEvent&gt; with full scope and metadata reconstruction.
/// Uses InMemory DbContext to avoid PostgreSQL dependency (DeserializeStreamEvents is a pure function).
/// Uses OrderCreatedEvent which is registered in the generated MessageJsonContext.
/// </summary>
public class DeserializeStreamEventsTests {

  [Test]
  public async Task DeserializeStreamEvents_ReturnsTypedEvents_WhenEventTypeProviderAvailable_Async() {
    // Arrange — create event store with AOT-compatible JSON options
    var eventStore = _createEventStore();
    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var jsonOptions = _createJsonOptions();

    // Construct StreamEventData as if returned from SQL get_stream_events
    var payload = new OrderCreatedEvent { OrderId = streamId, CustomerName = "Test Customer" };
    var eventData = new StreamEventData {
      StreamId = streamId,
      EventId = eventId,
      EventType = TypeNameFormatter.Format(typeof(OrderCreatedEvent)),
      EventData = JsonSerializer.Serialize(payload, jsonOptions),
      Metadata = null,
      Scope = null,
      EventWorkId = Guid.NewGuid()
    };

    // Act — deserialize with the event type in the type list
    var results = eventStore.DeserializeStreamEvents([eventData], [typeof(OrderCreatedEvent)]);

    // Assert — should produce 1 typed event
    await Assert.That(results.Count).IsEqualTo(1);
    await Assert.That(results[0].Payload).IsTypeOf<OrderCreatedEvent>();
    var deserialized = (OrderCreatedEvent)results[0].Payload;
    await Assert.That(deserialized.OrderId).IsEqualTo(streamId);
    await Assert.That(deserialized.CustomerName).IsEqualTo("Test Customer");
  }

  [Test]
  public async Task DeserializeStreamEvents_FullEnvelope_HasScopeAndMetadata_Async() {
    // Arrange — construct StreamEventData with metadata and scope JSON
    var eventStore = _createEventStore();
    var eventId = Guid.NewGuid();
    var streamId = Guid.NewGuid();
    var originalMessageId = MessageId.New();
    var jsonOptions = _createJsonOptions();

    var metadata = new EnvelopeMetadata {
      MessageId = originalMessageId,
      Hops = [
        new MessageHop {
          Type = HopType.Current,
          Timestamp = DateTimeOffset.UtcNow,
          CorrelationId = CorrelationId.New(),
          CausationId = MessageId.New(),
          ServiceInstance = new ServiceInstanceInfo {
            InstanceId = Guid.NewGuid(),
            ServiceName = "TestService",
            HostName = "test-host",
            ProcessId = 1234
          }
        }
      ],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
    };

    var scope = new PerspectiveScope {
      TenantId = "tenant-123",
      UserId = "user-456"
    };

    var payload = new OrderCreatedEvent { OrderId = streamId, CustomerName = "Scoped Customer" };
    var eventData = new StreamEventData {
      StreamId = streamId,
      EventId = eventId,
      EventType = TypeNameFormatter.Format(typeof(OrderCreatedEvent)),
      EventData = JsonSerializer.Serialize(payload, jsonOptions),
      Metadata = JsonSerializer.Serialize(metadata, jsonOptions),
      Scope = JsonSerializer.Serialize(scope, jsonOptions),
      EventWorkId = Guid.NewGuid()
    };

    // Act
    var results = eventStore.DeserializeStreamEvents([eventData], [typeof(OrderCreatedEvent)]);

    // Assert — envelope should have MessageId from metadata and scope restored in first hop
    await Assert.That(results.Count).IsEqualTo(1);
    await Assert.That(results[0].MessageId.Value).IsEqualTo(originalMessageId.Value);
    await Assert.That(results[0].Hops).Count().IsGreaterThanOrEqualTo(1);

    // Scope should be restored into first hop as a ScopeDelta
    var firstHop = results[0].Hops[0];
    await Assert.That(firstHop.Scope).IsNotNull()
      .Because("PerspectiveScope should be restored into first hop's ScopeDelta");
    await Assert.That(firstHop.Scope!.HasChanges).IsTrue()
      .Because("ScopeDelta should contain the tenant/user scope values");
  }

  [Test]
  public async Task DeserializeStreamEvents_TypeMapMatches_EventStoreEventTypes_Async() {
    // Arrange — verify that TypeNameFormatter.Format produces the SAME string
    // that _resolveConcreteType uses for type map lookup
    var eventStore = _createEventStore();
    var streamId = Guid.NewGuid();
    var jsonOptions = _createJsonOptions();

    // The EventType stored in the database is TypeNameFormatter.Format(type)
    var storedEventType = TypeNameFormatter.Format(typeof(OrderCreatedEvent));

    var payload = new OrderCreatedEvent { OrderId = streamId, CustomerName = "Type Match" };
    var eventData = new StreamEventData {
      StreamId = streamId,
      EventId = Guid.NewGuid(),
      EventType = storedEventType,
      EventData = JsonSerializer.Serialize(payload, jsonOptions),
      Metadata = null,
      Scope = null,
      EventWorkId = Guid.NewGuid()
    };

    // Act — the type map is built from eventTypes, and _resolveConcreteType matches against EventType
    var results = eventStore.DeserializeStreamEvents([eventData], [typeof(OrderCreatedEvent)]);

    // Assert — if the type map matches, we get a deserialized result (not skipped)
    await Assert.That(results.Count).IsEqualTo(1)
      .Because($"TypeNameFormatter.Format produces '{storedEventType}' which must match the type map built from the same type");

    // Also verify the event type string format is "Namespace.TypeName, AssemblyName"
    await Assert.That(storedEventType).Contains(",")
      .Because("TypeNameFormatter.Format should produce 'TypeName, AssemblyName' format");
    await Assert.That(storedEventType).Contains("Whizbang.Data.EFCore.Postgres.Tests")
      .Because("Assembly name should be present in the formatted type name");
  }

  #region Helpers

  private static JsonSerializerOptions _createJsonOptions() {
    return JsonContextRegistry.CreateCombinedOptions();
  }

  private static EFCoreEventStore<MinimalTestDbContext> _createEventStore() {
    var options = new DbContextOptionsBuilder<MinimalTestDbContext>()
      .UseInMemoryDatabase($"deser-test-{Guid.NewGuid():N}")
      .Options;
    var context = new MinimalTestDbContext(options);
    return new EFCoreEventStore<MinimalTestDbContext>(context, _createJsonOptions());
  }

  /// <summary>
  /// Minimal DbContext for DeserializeStreamEvents tests.
  /// DeserializeStreamEvents doesn't use the context, so this is just a placeholder.
  /// </summary>
  private sealed class MinimalTestDbContext(DbContextOptions<MinimalTestDbContext> options) : DbContext(options);

  #endregion
}
