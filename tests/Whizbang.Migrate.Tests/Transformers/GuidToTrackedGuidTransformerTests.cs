using Whizbang.Migrate.Transformers;

namespace Whizbang.Migrate.Tests.Transformers;

/// <summary>
/// Tests for the GuidToTrackedGuidTransformer that converts Guid.NewGuid()/Guid.CreateVersion7()
/// to TrackedGuid.NewMedo() calls for UUIDv7 with sub-millisecond precision.
/// </summary>
/// <tests>Whizbang.Migrate/Transformers/GuidToTrackedGuidTransformer.cs:*</tests>
public class GuidToTrackedGuidTransformerTests {
  [Test]
  public async Task TransformAsync_GuidNewGuid_TransformsToTrackedGuidNewMedoAsync() {
    // Arrange
    var transformer = new GuidToTrackedGuidTransformer();
    var sourceCode = """
      using System;

      public class OrderService {
        public Guid CreateOrder() {
          return Guid.NewGuid();
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("TrackedGuid.NewMedo()");
    await Assert.That(result.TransformedCode).DoesNotContain("Guid.NewGuid()");
    await Assert.That(result.Changes.Any(c => c.ChangeType == ChangeType.MethodCallReplacement)).IsTrue();
  }

  [Test]
  public async Task TransformAsync_GuidCreateVersion7_TransformsToTrackedGuidNewMedoAsync() {
    // Arrange
    var transformer = new GuidToTrackedGuidTransformer();
    var sourceCode = """
      using System;

      public class OrderService {
        public Guid CreateOrder() {
          return Guid.CreateVersion7();
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("TrackedGuid.NewMedo()");
    await Assert.That(result.TransformedCode).DoesNotContain("Guid.CreateVersion7()");
    await Assert.That(result.Changes.Any(c => c.ChangeType == ChangeType.MethodCallReplacement)).IsTrue();
  }

  [Test]
  public async Task TransformAsync_SystemGuidNewGuid_TransformsToTrackedGuidNewMedoAsync() {
    // Arrange
    var transformer = new GuidToTrackedGuidTransformer();
    var sourceCode = """
      public class OrderService {
        public System.Guid CreateOrder() {
          return System.Guid.NewGuid();
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("TrackedGuid.NewMedo()");
    await Assert.That(result.TransformedCode).DoesNotContain("System.Guid.NewGuid()");
  }

  [Test]
  public async Task TransformAsync_SystemGuidCreateVersion7_TransformsToTrackedGuidNewMedoAsync() {
    // Arrange
    var transformer = new GuidToTrackedGuidTransformer();
    var sourceCode = """
      public class OrderService {
        public System.Guid CreateOrder() {
          return System.Guid.CreateVersion7();
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("TrackedGuid.NewMedo()");
    await Assert.That(result.TransformedCode).DoesNotContain("System.Guid.CreateVersion7()");
  }

  [Test]
  public async Task TransformAsync_NoGuidGeneration_ReturnsUnchangedAsync() {
    // Arrange
    var transformer = new GuidToTrackedGuidTransformer();
    var sourceCode = """
      using System;

      public class OrderService {
        public void Process(Guid existingId) {
          Console.WriteLine(existingId);
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).IsEqualTo(sourceCode);
    await Assert.That(result.Changes).IsEmpty();
    await Assert.That(result.Warnings).IsEmpty();
  }

  [Test]
  public async Task TransformAsync_AddsWhizbangCoreValueObjectsUsingAsync() {
    // Arrange
    var transformer = new GuidToTrackedGuidTransformer();
    var sourceCode = """
      using System;

      public class OrderService {
        public Guid CreateOrder() {
          return Guid.NewGuid();
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("using Whizbang.Core.ValueObjects;");
    await Assert.That(result.Changes.Any(c => c.ChangeType == ChangeType.UsingAdded)).IsTrue();
  }

  [Test]
  public async Task TransformAsync_PreservesExistingUsingsAsync() {
    // Arrange
    var transformer = new GuidToTrackedGuidTransformer();
    var sourceCode = """
      using System;
      using Microsoft.Extensions.Logging;

      public class OrderService {
        private readonly ILogger<OrderService> _logger;
        public Guid CreateOrder() {
          return Guid.NewGuid();
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("using Microsoft.Extensions.Logging;");
    await Assert.That(result.TransformedCode).Contains("using System;");
    await Assert.That(result.TransformedCode).Contains("using Whizbang.Core.ValueObjects;");
  }

  [Test]
  public async Task TransformAsync_MultipleGuidCalls_TransformsAllAsync() {
    // Arrange
    var transformer = new GuidToTrackedGuidTransformer();
    var sourceCode = """
      using System;

      public class OrderService {
        public Guid CreateOrder() {
          var trackingId = Guid.NewGuid();
          var orderId = Guid.CreateVersion7();
          return orderId;
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).DoesNotContain("Guid.NewGuid()");
    await Assert.That(result.TransformedCode).DoesNotContain("Guid.CreateVersion7()");
    // Should have two TrackedGuid.NewMedo() calls
    var count = result.TransformedCode.Split("TrackedGuid.NewMedo()").Length - 1;
    await Assert.That(count).IsEqualTo(2);
  }

  [Test]
  public async Task TransformAsync_GuidNewGuidInFieldInitializer_TransformsAsync() {
    // Arrange
    var transformer = new GuidToTrackedGuidTransformer();
    var sourceCode = """
      using System;

      public class Order {
        public Guid Id { get; init; } = Guid.NewGuid();
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Order.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("TrackedGuid.NewMedo()");
    await Assert.That(result.TransformedCode).DoesNotContain("Guid.NewGuid()");
  }

  [Test]
  public async Task TransformAsync_GuidNewGuidInConstructor_TransformsAsync() {
    // Arrange
    var transformer = new GuidToTrackedGuidTransformer();
    var sourceCode = """
      using System;

      public class Order {
        public Guid Id { get; }

        public Order() {
          Id = Guid.NewGuid();
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Order.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("TrackedGuid.NewMedo()");
    await Assert.That(result.TransformedCode).DoesNotContain("Guid.NewGuid()");
  }

  [Test]
  public async Task TransformAsync_PreservesNamespaceAsync() {
    // Arrange
    var transformer = new GuidToTrackedGuidTransformer();
    var sourceCode = """
      using System;

      namespace MyApp.Domain;

      public class OrderService {
        public Guid CreateOrder() {
          return Guid.NewGuid();
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("namespace MyApp.Domain;");
  }

  [Test]
  public async Task TransformAsync_DoesNotDuplicateUsingAsync() {
    // Arrange
    var transformer = new GuidToTrackedGuidTransformer();
    var sourceCode = """
      using System;
      using Whizbang.Core.ValueObjects;

      public class OrderService {
        public Guid CreateOrder() {
          return Guid.NewGuid();
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    // Should not add duplicate using directive
    var usingCount = result.TransformedCode.Split("using Whizbang.Core.ValueObjects;").Length - 1;
    await Assert.That(usingCount).IsEqualTo(1);
  }

  [Test]
  public async Task TransformAsync_TracksAllChangesAsync() {
    // Arrange
    var transformer = new GuidToTrackedGuidTransformer();
    var sourceCode = """
      using System;

      public class OrderService {
        public Guid CreateOrder() {
          return Guid.NewGuid();
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.Changes.Count).IsGreaterThan(0);
    await Assert.That(result.Changes.Any(c => c.ChangeType == ChangeType.UsingAdded)).IsTrue();
    await Assert.That(result.Changes.Any(c => c.ChangeType == ChangeType.MethodCallReplacement)).IsTrue();
  }

  [Test]
  public async Task TransformAsync_EmitsWarningAboutTypeChangeAsync() {
    // Arrange
    var transformer = new GuidToTrackedGuidTransformer();
    var sourceCode = """
      using System;

      public class OrderService {
        public Guid CreateOrder() {
          return Guid.NewGuid();
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    // Should warn that the return type is now TrackedGuid (implicitly convertible to Guid)
    await Assert.That(result.Warnings.Any(w =>
        w.Contains("TrackedGuid") ||
        w.Contains("return type") ||
        w.Contains("implicitly"))).IsTrue();
  }

  [Test]
  public async Task TransformAsync_GuidInLambda_TransformsAsync() {
    // Arrange
    var transformer = new GuidToTrackedGuidTransformer();
    var sourceCode = """
      using System;
      using System.Collections.Generic;
      using System.Linq;

      public class OrderService {
        public List<Guid> CreateMultipleOrders(int count) {
          return Enumerable.Range(0, count)
            .Select(_ => Guid.NewGuid())
            .ToList();
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("TrackedGuid.NewMedo()");
    await Assert.That(result.TransformedCode).DoesNotContain("Guid.NewGuid()");
  }

  // ============================================================
  // Common Migration Scenarios (G01-G04)
  // ============================================================

  [Test]
  public async Task TransformAsync_G01_GuidNewGuidInHandler_TransformsToTrackedGuidAsync() {
    // Arrange - G01: Guid.NewGuid() in handler to strongly-typed ID pattern
    var transformer = new GuidToTrackedGuidTransformer();
    var sourceCode = """
      using System;

      public class OrderHandler {
        public async Task HandleAsync(CreateOrderCommand command, CancellationToken ct) {
          var orderId = Guid.NewGuid();
          // Use orderId for event creation
          var @event = new OrderCreated(orderId, command.CustomerId);
        }
      }

      public record CreateOrderCommand(Guid CustomerId);
      public record OrderCreated(Guid OrderId, Guid CustomerId);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Handler.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("TrackedGuid.NewMedo()");
    await Assert.That(result.TransformedCode).DoesNotContain("Guid.NewGuid()");
    await Assert.That(result.Changes.Any(c =>
        c.ChangeType == ChangeType.MethodCallReplacement)).IsTrue();
  }

  [Test]
  public async Task TransformAsync_G02_CombGuidIdGeneration_TransformsToTrackedGuidAsync() {
    // Arrange - G02: CombGuidIdGeneration.NewGuid() from Marten
    var transformer = new GuidToTrackedGuidTransformer();
    var sourceCode = """
      using System;
      using Marten.Schema.Identity;

      public class StreamIdGenerator {
        public Guid GenerateStreamId() {
          // CombGuid generates sequential GUIDs for better database index performance
          return CombGuidIdGeneration.NewGuid();
        }
      }

      public class OrderHandler {
        private readonly StreamIdGenerator _idGenerator;

        public OrderHandler(StreamIdGenerator idGenerator) {
          _idGenerator = idGenerator;
        }

        public async Task HandleAsync(CreateOrderCommand command, CancellationToken ct) {
          var orderId = _idGenerator.GenerateStreamId();
          // ...
        }
      }

      public record CreateOrderCommand(string Data);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Handler.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("TrackedGuid.NewMedo()");
    await Assert.That(result.TransformedCode).DoesNotContain("CombGuidIdGeneration.NewGuid()");
    await Assert.That(result.TransformedCode).DoesNotContain("using Marten.Schema.Identity;");
    await Assert.That(result.Changes.Any(c =>
        c.ChangeType == ChangeType.MethodCallReplacement &&
        c.Description.Contains("CombGuid"))).IsTrue();
  }

  [Test]
  public async Task TransformAsync_G02_CombGuidInEventSourcing_TransformsToTrackedGuidAsync() {
    // Arrange - G02: CombGuid used for stream ID generation in event sourcing
    var transformer = new GuidToTrackedGuidTransformer();
    var sourceCode = """
      using System;
      using Marten;
      using Marten.Schema.Identity;

      public class OrderService {
        private readonly IDocumentStore _store;

        public async Task<Guid> CreateOrderAsync(CreateOrderCommand command, CancellationToken ct) {
          await using var session = _store.LightweightSession();

          // CombGuid for sequential stream IDs
          var streamId = CombGuidIdGeneration.NewGuid();

          session.Events.StartStream<Order>(streamId, new OrderCreated(streamId, command.CustomerId));
          await session.SaveChangesAsync(ct);

          return streamId;
        }
      }

      public class Order { }
      public record CreateOrderCommand(Guid CustomerId);
      public record OrderCreated(Guid StreamId, Guid CustomerId);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Service.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("TrackedGuid.NewMedo()");
    await Assert.That(result.TransformedCode).DoesNotContain("CombGuidIdGeneration.NewGuid()");
  }

  [Test]
  public async Task TransformAsync_G03_DefaultStreamIdCheck_EmitsWarningAsync() {
    // Arrange - G03: Default StreamId check pattern should emit warning
    var transformer = new GuidToTrackedGuidTransformer();
    var sourceCode = """
      using System;

      public class OrderHandler {
        public async Task HandleAsync(CreateOrderCommand command, CancellationToken ct) {
          var @event = new OrderCreated(Guid.Empty, command.CustomerId);

          // Pattern: Check if StreamId is default (not set), then generate
          if (@event.StreamId == default) {
            @event = @event with { StreamId = Guid.NewGuid() };
          }
        }
      }

      public record CreateOrderCommand(Guid CustomerId);
      public record OrderCreated(Guid StreamId, Guid CustomerId);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Handler.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("TrackedGuid.NewMedo()");
    // Should warn about the default check pattern
    await Assert.That(result.Warnings.Any(w =>
        w.Contains("default") ||
        w.Contains("Default") ||
        w.Contains("generate IDs at creation"))).IsTrue();
  }

  [Test]
  public async Task TransformAsync_G04_CollisionRetryPattern_EmitsWarningAsync() {
    // Arrange - G04: Collision retry pattern should emit warning about being unnecessary
    var transformer = new GuidToTrackedGuidTransformer();
    var sourceCode = """
      using System;

      public class OrderHandler {
        private const int MaxRetryAttempts = 5;

        public async Task<Guid> HandleAsync(CreateOrderCommand command, CancellationToken ct) {
          for (var attempt = 0; attempt < MaxRetryAttempts; attempt++) {
            try {
              var orderId = Guid.NewGuid();
              // Try to create with this ID
              await CreateOrderAsync(orderId);
              return orderId;
            }
            catch (Exception ex) when (ex.Message.Contains("duplicate key")) {
              if (attempt == MaxRetryAttempts - 1) throw;
              // Retry with new ID
            }
          }

          throw new InvalidOperationException("Failed after max attempts");
        }

        private Task CreateOrderAsync(Guid id) => Task.CompletedTask;
      }

      public record CreateOrderCommand(string Data);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Handler.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("TrackedGuid.NewMedo()");
    // Should warn about retry pattern potentially being unnecessary with TrackedGuid
    await Assert.That(result.Warnings.Any(w =>
        w.Contains("retry") ||
        w.Contains("collision") ||
        w.Contains("TrackedGuid") ||
        w.Contains("virtually collision-free"))).IsTrue();
  }

  [Test]
  public async Task TransformAsync_G02_MultipleCombGuidCalls_TransformsAllAsync() {
    // Arrange - G02: Multiple CombGuid calls in same file
    var transformer = new GuidToTrackedGuidTransformer();
    var sourceCode = """
      using System;
      using Marten.Schema.Identity;

      public class IdGenerator {
        public Guid GenerateOrderId() => CombGuidIdGeneration.NewGuid();
        public Guid GenerateCustomerId() => CombGuidIdGeneration.NewGuid();
        public Guid GenerateProductId() => CombGuidIdGeneration.NewGuid();
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "IdGenerator.cs");

    // Assert
    await Assert.That(result.TransformedCode).DoesNotContain("CombGuidIdGeneration.NewGuid()");
    // Should have three TrackedGuid.NewMedo() calls
    var count = result.TransformedCode.Split("TrackedGuid.NewMedo()").Length - 1;
    await Assert.That(count).IsEqualTo(3);
  }

  [Test]
  public async Task TransformAsync_G02_CombGuidWithMartenUsing_RemovesMartenUsingAsync() {
    // Arrange - G02: Remove Marten.Schema.Identity using when CombGuid is transformed
    var transformer = new GuidToTrackedGuidTransformer();
    var sourceCode = """
      using System;
      using Marten;
      using Marten.Schema.Identity;

      public class OrderService {
        public Guid CreateId() {
          return CombGuidIdGeneration.NewGuid();
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Service.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("TrackedGuid.NewMedo()");
    await Assert.That(result.TransformedCode).DoesNotContain("using Marten.Schema.Identity;");
    await Assert.That(result.Changes.Any(c =>
        c.ChangeType == ChangeType.UsingRemoved &&
        c.Description.Contains("Marten.Schema.Identity"))).IsTrue();
  }
}
