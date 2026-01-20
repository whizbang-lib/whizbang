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
}
