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
    const string sourceCode = """
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
    const string sourceCode = """
      using Marten.Events.Aggregation;

      public class OrderProjection : SingleStreamProjection<Order> {
        public void Apply(OrderCreated @event, Order state) { }
        public void Apply(OrderUpdated @event, Order state) { }
        public void Apply(OrderCanceled @event, Order state) { }
      }

      public class Order { }
      public record OrderCreated(string Id);
      public record OrderUpdated(string Id);
      public record OrderCanceled(string Id);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Projection.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IPerspectiveFor<Order, OrderCreated, OrderUpdated, OrderCanceled>");
  }

  [Test]
  public async Task TransformAsync_UpdatesUsingDirectives_Async() {
    // Arrange
    var transformer = new ProjectionToPerspectiveTransformer();
    const string sourceCode = """
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
    const string sourceCode = """
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
    const string sourceCode = """
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
    const string sourceCode = """
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
    const string sourceCode = """
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
    const string sourceCode = """
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
    const string sourceCode = """
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

  // ============================================================
  // Marten Pattern Scenarios (P01-P07)
  // ============================================================

  [Test]
  public async Task TransformAsync_P01_SingleStreamProjectionWithMultipleEvents_TransformsToIPerspectiveForAsync() {
    // Arrange - P01: Marten SingleStreamProjection with multiple event handlers
    var transformer = new ProjectionToPerspectiveTransformer();
    const string sourceCode = """
      using Marten.Events.Aggregation;

      public class OrderProjection : SingleStreamProjection<OrderModel> {
        public void Apply(OrderCreated @event, OrderModel state) {
          state.Id = @event.StreamId;
          state.CustomerId = @event.CustomerId;
          state.Status = OrderStatus.Created;
        }

        public void Apply(OrderShipped @event, OrderModel state) {
          state.Status = OrderStatus.Shipped;
          state.ShippedAt = @event.ShippedAt;
        }
      }

      public class OrderModel {
        public Guid Id { get; set; }
        public Guid CustomerId { get; set; }
        public OrderStatus Status { get; set; }
        public DateTimeOffset? ShippedAt { get; set; }
      }

      public record OrderCreated(Guid StreamId, Guid CustomerId);
      public record OrderShipped(Guid StreamId, DateTimeOffset ShippedAt);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Projection.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IPerspectiveFor<OrderModel, OrderCreated, OrderShipped>");
    await Assert.That(result.TransformedCode).DoesNotContain("SingleStreamProjection<");
    await Assert.That(result.Changes.Any(c =>
        c.ChangeType == ChangeType.BaseClassReplacement)).IsTrue();
  }

  [Test]
  public async Task TransformAsync_P02_MultiStreamProjection_TransformsToIGlobalPerspectiveForAsync() {
    // Arrange - P02: Marten MultiStreamProjection for cross-stream aggregation
    var transformer = new ProjectionToPerspectiveTransformer();
    const string sourceCode = """
      using Marten.Events.Aggregation;

      public class DailySalesProjection : MultiStreamProjection<DailySalesModel, DateTime> {
        public DailySalesProjection() {
          Identity<OrderCompleted>(e => e.SalesDate.Date);
        }

        public void Apply(OrderCompleted @event, DailySalesModel state) {
          state.Date = @event.SalesDate.Date;
          state.TotalOrders++;
          state.TotalRevenue += @event.TotalAmount;
        }
      }

      public class DailySalesModel {
        public DateTime Date { get; set; }
        public int TotalOrders { get; set; }
        public decimal TotalRevenue { get; set; }
      }

      public record OrderCompleted(DateTime SalesDate, decimal TotalAmount);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Projection.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IGlobalPerspectiveFor<DailySalesModel>");
    await Assert.That(result.TransformedCode).DoesNotContain("MultiStreamProjection<");
  }

  [Test]
  public async Task TransformAsync_P03_IdentityPartitionKey_TransformsToPartitionKeyExtractorAsync() {
    // Arrange - P03: Marten Identity<T>(e => e.Key) for partition key extraction
    var transformer = new ProjectionToPerspectiveTransformer();
    const string sourceCode = """
      using Marten.Events.Aggregation;

      public class CustomerOrdersProjection : MultiStreamProjection<CustomerOrdersModel, Guid> {
        public CustomerOrdersProjection() {
          // Identity specifies how to extract partition key from events
          Identity<ICustomerEvent>(e => e.CustomerId);
          Identity<OrderCreated>(e => e.CustomerId);
          Identity<OrderUpdated>(e => e.CustomerId);
        }

        public void Apply(OrderCreated @event, CustomerOrdersModel state) {
          state.CustomerId = @event.CustomerId;
          state.OrderCount++;
        }
      }

      public class CustomerOrdersModel {
        public Guid CustomerId { get; set; }
        public int OrderCount { get; set; }
      }

      public interface ICustomerEvent { Guid CustomerId { get; } }
      public record OrderCreated(Guid CustomerId) : ICustomerEvent;
      public record OrderUpdated(Guid CustomerId) : ICustomerEvent;
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Projection.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IGlobalPerspectiveFor<CustomerOrdersModel>");
    // Warning about Identity transformation
    await Assert.That(result.Warnings.Any(w =>
        w.Contains("Identity") || w.Contains("partition"))).IsTrue();
  }

  [Test]
  public async Task TransformAsync_P04_NestedModelAndProjection_TransformsToFlatPerspectiveAsync() {
    // Arrange - P04: Nested Model + Projection classes (common pattern in larger codebases)
    var transformer = new ProjectionToPerspectiveTransformer();
    const string sourceCode = """
      using Marten.Events.Aggregation;

      public static class OrderViews {
        // Nested model class
        public class Model {
          public Guid Id { get; set; }
          public string Status { get; set; }
        }

        // Nested projection class
        public class Projection : SingleStreamProjection<Model> {
          public void Apply(OrderCreated @event, Model state) {
            state.Id = @event.StreamId;
            state.Status = "Created";
          }
        }
      }

      public record OrderCreated(Guid StreamId);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderViews.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IPerspectiveFor<");
    // Warning about nested structure
    await Assert.That(result.Warnings.Any(w => w.Contains("nested"))).IsTrue();
  }

  [Test]
  public async Task TransformAsync_P05_ProjectionModelWithVersionProperty_TransformsWithVersionWarningAsync() {
    // Arrange - P05: Model with Version property for optimistic concurrency
    var transformer = new ProjectionToPerspectiveTransformer();
    const string sourceCode = """
      using Marten.Events.Aggregation;

      public class OrderModel {
        public Guid Id { get; set; }
        public Guid CustomerId { get; set; }
        public string Status { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public long Version { get; set; }  // Used for optimistic concurrency
      }

      public class OrderProjection : SingleStreamProjection<OrderModel> {
        public void Apply(OrderCreated @event, OrderModel state) {
          state.Id = @event.StreamId;
          state.CustomerId = @event.CustomerId;
          state.CreatedAt = @event.CreatedAt;
        }
      }

      public record OrderCreated(Guid StreamId, Guid CustomerId, DateTimeOffset CreatedAt);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Projection.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IPerspectiveFor<OrderModel, OrderCreated>");
    await Assert.That(result.TransformedCode).DoesNotContain("SingleStreamProjection<");
  }

  [Test]
  public async Task TransformAsync_P06_ProjectionWithShouldDelete_TransformsToModelActionAsync() {
    // Arrange - P06: Marten projection with ShouldDelete method
    var transformer = new ProjectionToPerspectiveTransformer();
    const string sourceCode = """
      using Marten.Events.Aggregation;

      public class OrderModel {
        public Guid Id { get; set; }
        public string Status { get; set; }
        public bool IsDeleted { get; set; }
      }

      public class OrderProjection : SingleStreamProjection<OrderModel> {
        public void Apply(OrderCreated @event, OrderModel state) {
          state.Id = @event.StreamId;
          state.Status = "Created";
        }

        public void Apply(OrderCanceled @event, OrderModel state) {
          state.Status = "Canceled";
        }

        public bool ShouldDelete(OrderPurged @event) => true;
      }

      public record OrderCreated(Guid StreamId);
      public record OrderCanceled(Guid StreamId);
      public record OrderPurged(Guid StreamId);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Projection.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IPerspectiveFor<OrderModel");
    // ShouldDelete transformation should be noted
    await Assert.That(result.Warnings.Any(w =>
        w.Contains("ShouldDelete") || w.Contains("ModelAction"))).IsTrue();
  }

  [Test]
  public async Task TransformAsync_P07_CrossServiceDuplicateProjections_EmitsWarningAsync() {
    // Arrange - P07: Cross-service duplicate projections (common anti-pattern in microservices)
    var transformer = new ProjectionToPerspectiveTransformer();
    const string sourceCode = """
      using Marten.Events.Aggregation;

      // This projection exists in multiple services with slightly different implementations
      // Comment: Duplicate of OrderService.OrderProjection
      public class OrderProjection : SingleStreamProjection<OrderReadModel> {
        public void Apply(OrderCreated @event, OrderReadModel state) {
          state.Id = @event.StreamId;
          // Note: This implementation differs from OrderService
          state.DisplayName = $"Order {state.Id}";
        }
      }

      public class OrderReadModel {
        public Guid Id { get; set; }
        public string DisplayName { get; set; }
      }

      public record OrderCreated(Guid StreamId);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderProjection.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IPerspectiveFor<OrderReadModel, OrderCreated>");
    // Warning about potential duplicate
    await Assert.That(result.Warnings.Any(w =>
        w.Contains("duplicate") ||
        w.Contains("Duplicate") ||
        w.Contains("cross-service") ||
        w.Contains("single source"))).IsTrue();
  }

  [Test]
  public async Task TransformAsync_ShouldDeleteWithConditionalLogic_LeavesAReviewMarkerAsync() {
    // Marten's ShouldDelete returns a bool per event; Whizbang's Apply returns a ModelAction.
    // A conditional body cannot be translated mechanically, so the transformer picks
    // ModelAction.Delete and flags it. Dropping that marker is the dangerous outcome: a rule
    // that deleted a row only under a condition becomes one that always deletes, the code
    // compiles, and the loss shows up as missing rows in production.
    var transformer = new ProjectionToPerspectiveTransformer();
    const string sourceCode = """
      using Marten.Events.Aggregation;

      public class OrderModel {
        public Guid Id { get; set; }
        public string Status { get; set; }
      }

      public class OrderProjection : SingleStreamProjection<OrderModel> {
        public bool ShouldDelete(OrderPurged @event) => @event.Force;
      }

      public record OrderPurged(Guid StreamId, bool Force);
      """;

    var result = await transformer.TransformAsync(sourceCode, "Projection.cs");

    await Assert.That(result.TransformedCode).Contains("ModelAction");
    await Assert.That(result.TransformedCode).Contains("TODO")
      .Because("a condition that could not be carried across has to be visible in the code, "
             + "not only in a report the operator may never read");
  }

  [Test]
  public async Task TransformAsync_ShouldDeleteWithABlockBody_LeavesAReviewMarkerAsync() {
    // Same contract through the statement-bodied form, which is how most non-trivial rules are
    // written.
    var transformer = new ProjectionToPerspectiveTransformer();
    const string sourceCode = """
      using Marten.Events.Aggregation;

      public class OrderModel {
        public Guid Id { get; set; }
        public string Status { get; set; }
      }

      public class OrderProjection : SingleStreamProjection<OrderModel> {
        public bool ShouldDelete(OrderPurged @event) {
          if (@event.Force) {
            return true;
          }
          return false;
        }
      }

      public record OrderPurged(Guid StreamId, bool Force);
      """;

    var result = await transformer.TransformAsync(sourceCode, "Projection.cs");

    await Assert.That(result.TransformedCode).Contains("TODO");
  }

  [Test]
  public async Task TransformAsync_ShouldDeleteThatAlwaysDeletes_NeedsNoReviewMarkerAsync() {
    // The control for the two above. An unconditional rule translates exactly, so marking it
    // for review would train an operator to skim past the markers that do matter.
    var transformer = new ProjectionToPerspectiveTransformer();
    const string sourceCode = """
      using Marten.Events.Aggregation;

      public class OrderModel {
        public Guid Id { get; set; }
      }

      public class OrderProjection : SingleStreamProjection<OrderModel> {
        public bool ShouldDelete(OrderPurged @event) {
          return true;
        }
      }

      public record OrderPurged(Guid StreamId);
      """;

    var result = await transformer.TransformAsync(sourceCode, "Projection.cs");

    await Assert.That(result.TransformedCode).Contains("ModelAction");
    await Assert.That(result.TransformedCode).DoesNotContain("TODO: Review")
      .Because("an exact translation must not carry a review marker, or the markers stop meaning anything");
  }


  [Test]
  public async Task TransformAsync_RunTwice_LeavesTheAlreadyMigratedFileAloneAsync() {
    // Operators re-run migrations: over a subset, after fixing a compile error, after pulling
    // more code in. The second pass sees a file whose base is already IPerspectiveFor<...>, and
    // it has to recognise that rather than migrate the migration -- a doubly-wrapped perspective
    // is not something the operator can unpick from the diff.
    var transformer = new ProjectionToPerspectiveTransformer();
    const string sourceCode = """
      using Marten.Events.Aggregation;

      public class OrderProjection : SingleStreamProjection<Order> {
        public void Apply(OrderCreated @event, Order state) {
          state.Id = @event.OrderId;
        }
      }

      public class Order { public string Id { get; set; } }
      public record OrderCreated(string OrderId);
      """;

    var first = await transformer.TransformAsync(sourceCode, "Projection.cs");
    var second = await transformer.TransformAsync(first.TransformedCode, "Projection.cs");

    await Assert.That(second.TransformedCode).IsEqualTo(first.TransformedCode)
      .Because("a second pass over migrated source is a no-op, not another migration");

    var wrappers = first.TransformedCode.Split("IPerspectiveFor<").Length - 1;
    await Assert.That(wrappers).IsEqualTo(1)
      .Because("one projection yields one perspective, however many times the tool is run");
  }

  [Test]
  public async Task TransformAsync_FileWithoutTheMartenUsing_IsLeftUntouchedAsync() {
    // The transformer walks every .cs file in the tree, and most of them have nothing to do with
    // Marten. Rewriting usings on a file that never imported the aggregation namespace would
    // edit code the migration has no business touching.
    var transformer = new ProjectionToPerspectiveTransformer();
    const string sourceCode = """
      using System;

      public class OrderService {
        public string Describe() => "orders";
      }
      """;

    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    await Assert.That(result.TransformedCode).IsEqualTo(sourceCode)
      .Because("a file with no Marten aggregation using is not this transformer's business");
    await Assert.That(result.Changes).IsEmpty()
      .Because("reporting a change that was not made misleads the operator reading the summary");
  }

  [Test]
  public async Task TransformAsync_ClassOnAnUnrelatedBase_IsNotTreatedAsAProjectionAsync() {
    // Base-class detection matches on the Marten projection types by name. A class that merely
    // has some other base sits in the same file and must come through unchanged -- converting it
    // would produce a perspective for a type that was never a projection.
    var transformer = new ProjectionToPerspectiveTransformer();
    const string sourceCode = """
      using Marten.Events.Aggregation;

      public class OrderReportBuilder : ReportBuilderBase {
        public void Build() { }
      }

      public class ReportBuilderBase { }
      """;

    var result = await transformer.TransformAsync(sourceCode, "OrderReportBuilder.cs");

    await Assert.That(result.TransformedCode).DoesNotContain("IPerspectiveFor<")
      .Because("only a Marten projection base makes a class a projection");
    await Assert.That(result.TransformedCode).Contains("ReportBuilderBase")
      .Because("the unrelated base class survives untouched");
  }


  [Test]
  public async Task TransformAsync_ApplyAlongsideCreate_SuggestsMustExistAsync() {
    // With a Create method present, Apply handles updates only -- so it runs against a model that
    // is assumed to exist. [MustExist] generates the null check that assumption needs. Migrating
    // silently leaves an Apply that dereferences whatever Create did not make, and the failure
    // surfaces as a null reference inside the perspective rather than as a migration gap.
    var transformer = new ProjectionToPerspectiveTransformer();
    const string sourceCode = """
      using Marten.Events.Aggregation;

      public class OrderProjection : SingleStreamProjection<Order> {
        public Order Create(OrderCreated @event) => new Order { Id = @event.OrderId };

        public void Apply(OrderShipped @event, Order state) {
          state.Shipped = true;
        }
      }

      public class Order { public string Id { get; set; } public bool Shipped { get; set; } }
      public record OrderCreated(string OrderId);
      public record OrderShipped(string OrderId);
      """;

    var result = await transformer.TransformAsync(sourceCode, "OrderProjection.cs");

    await Assert.That(result.Warnings.Any(w => w.Contains("MustExist", StringComparison.Ordinal)))
      .IsTrue()
      .Because("Apply beside Create assumes the model exists; without the attribute nothing "
             + "generates that check and the assumption fails at runtime instead");
  }

  [Test]
  public async Task TransformAsync_ShouldDelete_WarnsItBecomesModelActionAsync() {
    // Marten signals deletion with a bool from ShouldDelete. Whizbang returns a ModelAction, so
    // the signature changes shape rather than name -- a rename alone would leave a method whose
    // return value the framework ignores, and the row would silently never be deleted.
    var transformer = new ProjectionToPerspectiveTransformer();
    const string sourceCode = """
      using Marten.Events.Aggregation;

      public class OrderProjection : SingleStreamProjection<Order> {
        public bool ShouldDelete(OrderCancelled @event, Order state) => true;
      }

      public class Order { public string Id { get; set; } }
      public record OrderCancelled(string OrderId);
      """;

    var result = await transformer.TransformAsync(sourceCode, "OrderProjection.cs");

    await Assert.That(result.Warnings.Any(w => w.Contains("ModelAction", StringComparison.Ordinal)))
      .IsTrue()
      .Because("the return type changes shape, not just the name; a silent rename leaves the "
             + "framework ignoring the result and the row never being deleted");
  }

}
