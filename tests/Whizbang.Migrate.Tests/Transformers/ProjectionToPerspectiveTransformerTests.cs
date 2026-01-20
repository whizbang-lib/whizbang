using Whizbang.Migrate.Transformers;

namespace Whizbang.Migrate.Tests.Transformers;

/// <summary>
/// Tests for the Projection to Perspective transformer that converts Marten projections to Whizbang perspectives.
/// </summary>
/// <tests>Whizbang.Migrate/Transformers/ProjectionToPerspectiveTransformer.cs:*</tests>
public class ProjectionToPerspectiveTransformerTests {
  [Test]
  public async Task TransformAsync_ConvertsSingleStreamProjectionToIPerspectiveFor_Async() {
    // Arrange
    var transformer = new ProjectionToPerspectiveTransformer();
    var sourceCode = """
      using Marten.Events.Aggregation;

      public class OrderProjection : SingleStreamProjection<Order> {
        public void Apply(OrderCreated @event, Order state) {
          state.Id = @event.OrderId;
        }
      }

      public class Order { public string Id { get; set; } }
      public record OrderCreated(string OrderId);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Projection.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IPerspectiveFor<Order, OrderCreated>");
    await Assert.That(result.TransformedCode).DoesNotContain("SingleStreamProjection<");
  }

  [Test]
  public async Task TransformAsync_ConvertsMultipleEventTypes_Async() {
    // Arrange
    var transformer = new ProjectionToPerspectiveTransformer();
    var sourceCode = """
      using Marten.Events.Aggregation;

      public class OrderProjection : SingleStreamProjection<Order> {
        public void Apply(OrderCreated @event, Order state) { }
        public void Apply(OrderUpdated @event, Order state) { }
        public void Apply(OrderCancelled @event, Order state) { }
      }

      public class Order { }
      public record OrderCreated(string Id);
      public record OrderUpdated(string Id);
      public record OrderCancelled(string Id);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Projection.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IPerspectiveFor<Order, OrderCreated, OrderUpdated, OrderCancelled>");
  }

  [Test]
  public async Task TransformAsync_UpdatesUsingDirectives_Async() {
    // Arrange
    var transformer = new ProjectionToPerspectiveTransformer();
    var sourceCode = """
      using Marten.Events.Aggregation;

      public class OrderProjection : SingleStreamProjection<Order> {
        public void Apply(OrderCreated @event, Order state) { }
      }

      public class Order { }
      public record OrderCreated(string Id);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Projection.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("using Whizbang.Core.Perspectives;");
    await Assert.That(result.TransformedCode).DoesNotContain("using Marten.Events.Aggregation;");
  }

  [Test]
  public async Task TransformAsync_ConvertsMultiStreamProjectionToIGlobalPerspectiveFor_Async() {
    // Arrange
    var transformer = new ProjectionToPerspectiveTransformer();
    var sourceCode = """
      using Marten.Events.Aggregation;

      public class OrderSummaryProjection : MultiStreamProjection<OrderSummary, Guid> {
        public void Apply(OrderCreated @event, OrderSummary state) { }
      }

      public class OrderSummary { public Guid Id { get; set; } }
      public record OrderCreated(string OrderId);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Projection.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IGlobalPerspectiveFor<OrderSummary>");
    await Assert.That(result.TransformedCode).DoesNotContain("MultiStreamProjection<");
  }

  [Test]
  public async Task TransformAsync_TracksChanges_Async() {
    // Arrange
    var transformer = new ProjectionToPerspectiveTransformer();
    var sourceCode = """
      using Marten.Events.Aggregation;

      public class OrderProjection : SingleStreamProjection<Order> {
        public void Apply(OrderCreated @event, Order state) { }
      }

      public class Order { }
      public record OrderCreated(string Id);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Projection.cs");

    // Assert
    await Assert.That(result.Changes.Count).IsGreaterThan(0);
    await Assert.That(result.Changes.Any(c => c.ChangeType == ChangeType.BaseClassReplacement)).IsTrue();
  }

  [Test]
  public async Task TransformAsync_PreservesApplyMethodBody_Async() {
    // Arrange
    var transformer = new ProjectionToPerspectiveTransformer();
    var sourceCode = """
      using Marten.Events.Aggregation;

      public class OrderProjection : SingleStreamProjection<Order> {
        public void Apply(OrderCreated @event, Order state) {
          state.Id = @event.OrderId;
          state.Status = "Created";
        }
      }

      public class Order { public string Id { get; set; } public string Status { get; set; } }
      public record OrderCreated(string OrderId);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Projection.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("state.Id = @event.OrderId;");
    await Assert.That(result.TransformedCode).Contains("state.Status = \"Created\";");
  }

  [Test]
  public async Task TransformAsync_NoProjections_ReturnsUnchanged_Async() {
    // Arrange
    var transformer = new ProjectionToPerspectiveTransformer();
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
  }

  [Test]
  public async Task TransformAsync_PreservesNamespace_Async() {
    // Arrange
    var transformer = new ProjectionToPerspectiveTransformer();
    var sourceCode = """
      using Marten.Events.Aggregation;

      namespace MyApp.Projections;

      public class OrderProjection : SingleStreamProjection<Order> {
        public void Apply(OrderCreated @event, Order state) { }
      }

      public class Order { }
      public record OrderCreated(string Id);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Projection.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("namespace MyApp.Projections;");
  }

  [Test]
  public async Task TransformAsync_HandlesMultipleProjectionsInFile_Async() {
    // Arrange
    var transformer = new ProjectionToPerspectiveTransformer();
    var sourceCode = """
      using Marten.Events.Aggregation;

      public class OrderProjection : SingleStreamProjection<Order> {
        public void Apply(OrderCreated @event, Order state) { }
      }

      public class CustomerProjection : SingleStreamProjection<Customer> {
        public void Apply(CustomerCreated @event, Customer state) { }
      }

      public class Order { }
      public class Customer { }
      public record OrderCreated(string Id);
      public record CustomerCreated(string Id);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Projections.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IPerspectiveFor<Order, OrderCreated>");
    await Assert.That(result.TransformedCode).Contains("IPerspectiveFor<Customer, CustomerCreated>");
  }
}
