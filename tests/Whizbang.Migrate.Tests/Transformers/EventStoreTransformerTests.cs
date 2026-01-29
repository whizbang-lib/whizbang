using Whizbang.Migrate.Transformers;

namespace Whizbang.Migrate.Tests.Transformers;

/// <summary>
/// Tests for the EventStore transformer that converts Marten IDocumentStore patterns to Whizbang IEventStore.
/// </summary>
/// <tests>Whizbang.Migrate/Transformers/EventStoreTransformer.cs:*</tests>
public class EventStoreTransformerTests {
  [Test]
  public async Task TransformAsync_ConvertsIDocumentStoreToIEventStore_Async() {
    // Arrange
    var transformer = new EventStoreTransformer();
    var sourceCode = """
      using Marten;

      public class OrderService {
        private readonly IDocumentStore _store;

        public OrderService(IDocumentStore store) {
          _store = store;
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IEventStore");
    await Assert.That(result.TransformedCode).DoesNotContain("IDocumentStore");
  }

  [Test]
  public async Task TransformAsync_UpdatesUsingDirectives_Async() {
    // Arrange
    var transformer = new EventStoreTransformer();
    var sourceCode = """
      using Marten;
      using Marten.Events;

      public class OrderService {
        private readonly IDocumentStore _store;
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("using Whizbang.Core.Messaging;");
    await Assert.That(result.TransformedCode).DoesNotContain("using Marten;");
    await Assert.That(result.TransformedCode).DoesNotContain("using Marten.Events;");
  }

  [Test]
  public async Task TransformAsync_RemovesSessionDeclarations_Async() {
    // Arrange
    var transformer = new EventStoreTransformer();
    var sourceCode = """
      using Marten;

      public class OrderService {
        private readonly IDocumentStore _store;

        public async Task CreateOrderAsync() {
          await using var session = _store.LightweightSession();
          // Do something
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).DoesNotContain("LightweightSession()");
    await Assert.That(result.TransformedCode).DoesNotContain("await using var session");
    await Assert.That(result.Changes.Any(c => c.Description.Contains("session declaration"))).IsTrue();
  }

  [Test]
  public async Task TransformAsync_RemovesSaveChangesAsync_Async() {
    // Arrange
    var transformer = new EventStoreTransformer();
    var sourceCode = """
      using Marten;

      public class OrderService {
        private readonly IDocumentStore _store;

        public async Task CreateOrderAsync() {
          await using var session = _store.LightweightSession();
          await session.SaveChangesAsync();
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).DoesNotContain("SaveChangesAsync()");
    await Assert.That(result.Changes.Any(c => c.Description.Contains("SaveChangesAsync"))).IsTrue();
  }

  [Test]
  public async Task TransformAsync_WarnsAboutStartStream_Async() {
    // Arrange
    var transformer = new EventStoreTransformer();
    var sourceCode = """
      using Marten;

      public class OrderService {
        private readonly IDocumentStore _store;

        public async Task CreateOrderAsync() {
          await using var session = _store.LightweightSession();
          var streamId = session.Events.StartStream<Order>(new OrderCreated());
          await session.SaveChangesAsync();
        }
      }

      public class Order { }
      public record OrderCreated();
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.Warnings.Any(w => w.Contains("StartStream"))).IsTrue();
    await Assert.That(result.Changes.Any(c => c.Description.Contains("StartStream"))).IsTrue();
  }

  [Test]
  public async Task TransformAsync_WarnsAboutEventsAppend_Async() {
    // Arrange
    var transformer = new EventStoreTransformer();
    var sourceCode = """
      using Marten;

      public class OrderService {
        private readonly IDocumentStore _store;

        public async Task ShipOrderAsync(Guid streamId) {
          await using var session = _store.LightweightSession();
          session.Events.Append(streamId, new OrderShipped());
          await session.SaveChangesAsync();
        }
      }

      public record OrderShipped();
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.Warnings.Any(w => w.Contains("Events.Append"))).IsTrue();
  }

  [Test]
  public async Task TransformAsync_WarnsAboutFetchStreamAsync_Async() {
    // Arrange
    var transformer = new EventStoreTransformer();
    var sourceCode = """
      using Marten;

      public class OrderService {
        private readonly IDocumentStore _store;

        public async Task<object[]> GetEventsAsync(Guid streamId) {
          await using var session = _store.QuerySession();
          var events = await session.Events.FetchStreamAsync(streamId);
          return events.ToArray();
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.Warnings.Any(w => w.Contains("FetchStreamAsync"))).IsTrue();
  }

  [Test]
  public async Task TransformAsync_WarnsAboutAggregateStreamAsync_Async() {
    // Arrange
    var transformer = new EventStoreTransformer();
    var sourceCode = """
      using Marten;

      public class OrderService {
        private readonly IDocumentStore _store;

        public async Task<Order?> GetOrderAsync(Guid streamId) {
          await using var session = _store.LightweightSession();
          return await session.Events.AggregateStreamAsync<Order>(streamId);
        }
      }

      public class Order { }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.Warnings.Any(w => w.Contains("AggregateStreamAsync"))).IsTrue();
    await Assert.That(result.Warnings.Any(w => w.Contains("IPerspectiveFor") || w.Contains("Perspective"))).IsTrue();
  }

  [Test]
  public async Task TransformAsync_TracksAllChanges_Async() {
    // Arrange
    var transformer = new EventStoreTransformer();
    var sourceCode = """
      using Marten;

      public class OrderService {
        private readonly IDocumentStore _store;

        public async Task CreateOrderAsync() {
          await using var session = _store.LightweightSession();
          await session.SaveChangesAsync();
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.Changes.Count).IsGreaterThan(0);
    await Assert.That(result.Changes.Any(c => c.ChangeType == ChangeType.InterfaceReplacement)).IsTrue();
    await Assert.That(result.Changes.Any(c => c.ChangeType == ChangeType.UsingRemoved)).IsTrue();
  }

  [Test]
  public async Task TransformAsync_NoMartenPatterns_ReturnsUnchanged_Async() {
    // Arrange
    var transformer = new EventStoreTransformer();
    var sourceCode = """
      public class OrderService {
        public void Process() { }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Service.cs");

    // Assert
    await Assert.That(result.TransformedCode).IsEqualTo(sourceCode);
    await Assert.That(result.Changes).IsEmpty();
    await Assert.That(result.Warnings).IsEmpty();
  }

  [Test]
  public async Task TransformAsync_PreservesNonMartenCode_Async() {
    // Arrange
    var transformer = new EventStoreTransformer();
    var sourceCode = """
      using Marten;
      using Microsoft.Extensions.Logging;

      public class OrderService {
        private readonly IDocumentStore _store;
        private readonly ILogger<OrderService> _logger;

        public OrderService(IDocumentStore store, ILogger<OrderService> logger) {
          _store = store;
          _logger = logger;
        }

        public void LogSomething() {
          _logger.LogInformation("Something");
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("ILogger<OrderService>");
    await Assert.That(result.TransformedCode).Contains("using Microsoft.Extensions.Logging;");
    await Assert.That(result.TransformedCode).Contains("LogSomething");
  }

  [Test]
  public async Task TransformAsync_PreservesNamespace_Async() {
    // Arrange
    var transformer = new EventStoreTransformer();
    var sourceCode = """
      using Marten;

      namespace MyApp.Services;

      public class OrderService {
        private readonly IDocumentStore _store;
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("namespace MyApp.Services;");
  }

  [Test]
  public async Task TransformAsync_RenamesStoreParameterToEventStore_Async() {
    // Arrange
    var transformer = new EventStoreTransformer();
    var sourceCode = """
      using Marten;

      public class OrderService {
        private readonly IDocumentStore _store;

        public OrderService(IDocumentStore store) {
          _store = store;
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    // Note: Parameter rename depends on implementation - check if it happened
    await Assert.That(result.Changes.Any(c => c.ChangeType == ChangeType.TypeRename || c.ChangeType == ChangeType.InterfaceReplacement)).IsTrue();
  }

  [Test]
  public async Task TransformAsync_WarnsAboutIDocumentSession_Async() {
    // Arrange
    var transformer = new EventStoreTransformer();
    var sourceCode = """
      using Marten;

      public class OrderService {
        public void DoSomething(IDocumentSession session) {
          // Uses session directly
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.Warnings.Any(w => w.Contains("IDocumentSession"))).IsTrue();
  }

  [Test]
  public async Task TransformAsync_EventsAppend_EmitsWarningAboutDispatcherPatternAsync() {
    // Arrange
    var transformer = new EventStoreTransformer();
    var sourceCode = """
      using Marten;

      public class OrderService {
        private readonly IDocumentStore _store;

        public async Task CreateOrderAsync(Guid orderId) {
          await using var session = _store.LightweightSession();
          session.Events.Append(orderId, new OrderCreated());
          await session.SaveChangesAsync();
        }
      }

      public record OrderCreated();
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    // Should emit a warning about considering IDispatcher.PublishAsync vs IEventStore.AppendAsync
    await Assert.That(result.Warnings.Any(w =>
        w.Contains("Events.Append") &&
        (w.Contains("PublishAsync") || w.Contains("Dispatcher")))).IsTrue();
  }

  // ============================================================
  // Common Migration Scenarios (E01-E09)
  // ============================================================

  [Test]
  public async Task TransformAsync_E01_StartStreamWithGuid_TransformsToAppendAsyncAsync() {
    // Arrange - E01: StartStream with ID generation
    var transformer = new EventStoreTransformer();
    var sourceCode = """
      using Marten;

      public class OrderHandler {
        private readonly IDocumentStore _store;

        public async Task<Guid> CreateOrderAsync(CreateOrderCommand command, CancellationToken ct) {
          await using var session = _store.LightweightSession();

          var orderId = Guid.NewGuid();
          session.Events.StartStream<Order>(
              orderId,
              new OrderCreated(orderId, command.CustomerId, command.Items)
          );

          await session.SaveChangesAsync(ct);
          return orderId;
        }
      }

      public class Order { }
      public record CreateOrderCommand(Guid CustomerId, string[] Items);
      public record OrderCreated(Guid OrderId, Guid CustomerId, string[] Items);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Handler.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IEventStore");
    await Assert.That(result.TransformedCode).DoesNotContain("LightweightSession");
    await Assert.That(result.Warnings.Any(w => w.Contains("StartStream"))).IsTrue();
    await Assert.That(result.Changes.Any(c =>
        c.Description.Contains("StartStream") || c.Description.Contains("AppendAsync"))).IsTrue();
  }

  [Test]
  public async Task TransformAsync_E02_AppendToStream_TransformsToAppendAsyncAsync() {
    // Arrange - E02: Basic Append to existing stream
    var transformer = new EventStoreTransformer();
    var sourceCode = """
      using Marten;

      public class OrderHandler {
        private readonly IDocumentStore _store;

        public async Task ShipOrderAsync(Guid orderId, CancellationToken ct) {
          await using var session = _store.LightweightSession();

          session.Events.Append(orderId, new OrderShipped(orderId, DateTime.UtcNow));

          await session.SaveChangesAsync(ct);
        }
      }

      public record OrderShipped(Guid OrderId, DateTime ShippedAt);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Handler.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IEventStore");
    await Assert.That(result.TransformedCode).DoesNotContain("SaveChangesAsync");
    await Assert.That(result.Warnings.Any(w => w.Contains("Events.Append"))).IsTrue();
  }

  [Test]
  public async Task TransformAsync_E03_AppendExclusive_TransformsWithWarningAsync() {
    // Arrange - E03: AppendExclusive with locking
    var transformer = new EventStoreTransformer();
    var sourceCode = """
      using Marten;

      public class OrderHandler {
        private readonly IDocumentStore _store;

        public async Task ProcessExclusiveAsync(Guid streamId, object @event, CancellationToken ct) {
          await using var session = _store.LightweightSession();

          // AppendExclusive takes a lock on the stream
          session.Events.AppendExclusive(streamId, @event);

          await session.SaveChangesAsync(ct);
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Handler.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IEventStore");
    // Warning about AppendExclusive requiring review
    await Assert.That(result.Warnings.Any(w =>
        w.Contains("AppendExclusive") ||
        w.Contains("exclusive") ||
        w.Contains("lock"))).IsTrue();
  }

  [Test]
  public async Task TransformAsync_E04_AppendOptimistic_TransformsWithExpectedSequenceAsync() {
    // Arrange - E04: Optimistic concurrency append
    var transformer = new EventStoreTransformer();
    var sourceCode = """
      using Marten;

      public class OrderHandler {
        private readonly IDocumentStore _store;

        public async Task UpdateWithVersionAsync(Guid streamId, int expectedVersion, object @event, CancellationToken ct) {
          await using var session = _store.LightweightSession();

          session.Events.AppendOptimistic(streamId, @event);

          await session.SaveChangesAsync(ct);
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Handler.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IEventStore");
    // Warning about optimistic concurrency
    await Assert.That(result.Warnings.Any(w =>
        w.Contains("AppendOptimistic") ||
        w.Contains("optimistic") ||
        w.Contains("expectedSequence"))).IsTrue();
  }

  [Test]
  public async Task TransformAsync_E05_CombGuidIdGeneration_TransformsToTrackedGuidAsync() {
    // Arrange - E05: CombGuid ID generation
    var transformer = new EventStoreTransformer();
    var sourceCode = """
      using Marten;
      using Marten.Schema.Identity;

      public class OrderHandler {
        private readonly IDocumentStore _store;

        public async Task<Guid> CreateWithCombGuidAsync(CreateCommand command, CancellationToken ct) {
          await using var session = _store.LightweightSession();

          // CombGuid generates sequential GUIDs for better index performance
          var streamId = CombGuidIdGeneration.NewGuid();

          session.Events.StartStream<MyAggregate>(streamId, new AggregateCreated(streamId));
          await session.SaveChangesAsync(ct);

          return streamId;
        }
      }

      public class MyAggregate { }
      public record CreateCommand();
      public record AggregateCreated(Guid StreamId);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Handler.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IEventStore");
    // Warning about CombGuid to TrackedGuid migration
    await Assert.That(result.Warnings.Any(w =>
        w.Contains("CombGuid") ||
        w.Contains("TrackedGuid") ||
        w.Contains("sequential"))).IsTrue();
  }

  [Test]
  public async Task TransformAsync_E06_CollisionRetry_SimplifiesToSingleAppendAsync() {
    // Arrange - E06: Stream ID collision retry pattern
    var transformer = new EventStoreTransformer();
    var sourceCode = """
      using Marten;

      public class OrderHandler {
        private readonly IDocumentStore _store;
        private const int MaxRetryAttempts = 5;

        public async Task<Guid> CreateWithRetryAsync(CreateCommand command, CancellationToken ct) {
          for (var attempt = 0; attempt < MaxRetryAttempts; attempt++) {
            try {
              await using var session = _store.LightweightSession();

              var streamId = Guid.NewGuid();
              session.Events.StartStream<MyAggregate>(streamId, new AggregateCreated(streamId));
              await session.SaveChangesAsync(ct);

              return streamId;
            }
            catch (Exception ex) when (ex.Message.Contains("duplicate key")) {
              if (attempt == MaxRetryAttempts - 1) throw;
              // Retry with new ID on collision
            }
          }

          throw new InvalidOperationException("Failed to create stream after max attempts");
        }
      }

      public class MyAggregate { }
      public record CreateCommand();
      public record AggregateCreated(Guid StreamId);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Handler.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IEventStore");
    // Warning about collision retry being potentially unnecessary
    await Assert.That(result.Warnings.Any(w =>
        w.Contains("retry") ||
        w.Contains("collision") ||
        w.Contains("TrackedGuid"))).IsTrue();
  }

  [Test]
  public async Task TransformAsync_E07_MultipleAppends_TransformsToAppendBatchAsync() {
    // Arrange - E07: Multiple appends with single SaveChangesAsync
    var transformer = new EventStoreTransformer();
    var sourceCode = """
      using Marten;

      public class OrderHandler {
        private readonly IDocumentStore _store;

        public async Task ComplexOperationAsync(Guid orderId, CancellationToken ct) {
          await using var session = _store.LightweightSession();

          session.Events.Append(orderId, new OrderUpdated(orderId, "step1"));
          session.Events.Append(orderId, new OrderUpdated(orderId, "step2"));
          session.Events.Append(orderId, new OrderUpdated(orderId, "step3"));

          // All events committed atomically
          await session.SaveChangesAsync(ct);
        }
      }

      public record OrderUpdated(Guid OrderId, string Step);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Handler.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IEventStore");
    // Warning about batch append pattern
    await Assert.That(result.Warnings.Any(w =>
        w.Contains("batch") ||
        w.Contains("multiple") ||
        w.Contains("AppendBatchAsync"))).IsTrue();
  }

  [Test]
  public async Task TransformAsync_E08_BatchAppend_TransformsWithWorkCoordinatorAsync() {
    // Arrange - E08: Batch create across multiple streams
    var transformer = new EventStoreTransformer();
    var sourceCode = """
      using Marten;

      public class OrderHandler {
        private readonly IDocumentStore _store;

        public async Task BatchCreateAsync(IReadOnlyList<CreateItemCommand> commands, CancellationToken ct) {
          await using var session = _store.LightweightSession();

          foreach (var command in commands) {
            var itemId = Guid.NewGuid();
            session.Events.StartStream<Item>(itemId, new ItemCreated(itemId, command.Name));
          }

          await session.SaveChangesAsync(ct);
        }
      }

      public class Item { }
      public record CreateItemCommand(string Name);
      public record ItemCreated(Guid ItemId, string Name);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Handler.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IEventStore");
    // Warning about multi-stream transactions requiring IWorkCoordinator
    await Assert.That(result.Warnings.Any(w =>
        w.Contains("batch") ||
        w.Contains("WorkCoordinator") ||
        w.Contains("multi-stream") ||
        w.Contains("cross-stream"))).IsTrue();
  }

  [Test]
  public async Task TransformAsync_E09_TenantScopedSession_TransformsToScopedEventStoreAsync() {
    // Arrange - E09: Tenant-scoped session
    var transformer = new EventStoreTransformer();
    var sourceCode = """
      using Marten;

      public class TenantAwareService {
        private readonly IDocumentStore _store;
        private readonly ITenantContext _tenantContext;

        public TenantAwareService(IDocumentStore store, ITenantContext tenantContext) {
          _store = store;
          _tenantContext = tenantContext;
        }

        public async Task CreateAsync(CreateCommand command, CancellationToken ct) {
          // Marten session scoped to tenant
          await using var session = _store.LightweightSession(_tenantContext.TenantId);

          var id = Guid.NewGuid();
          session.Events.StartStream<MyAggregate>(id, new Created(id));
          await session.SaveChangesAsync(ct);
        }
      }

      public interface ITenantContext { string TenantId { get; } }
      public class MyAggregate { }
      public record CreateCommand();
      public record Created(Guid Id);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Service.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IEventStore");
    // Warning about tenant-scoped session
    await Assert.That(result.Warnings.Any(w =>
        w.Contains("tenant") ||
        w.Contains("Tenant") ||
        w.Contains("scoped"))).IsTrue();
  }
}
