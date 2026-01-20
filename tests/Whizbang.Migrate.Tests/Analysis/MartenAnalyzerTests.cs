using Whizbang.Migrate.Analysis;

namespace Whizbang.Migrate.Tests.Analysis;

/// <summary>
/// Tests for the Marten pattern analyzer that detects projections and event store usage to migrate.
/// </summary>
/// <tests>Whizbang.Migrate/Analysis/MartenAnalyzer.cs:*</tests>
public class MartenAnalyzerTests {
  [Test]
  public async Task AnalyzeAsync_DetectsSingleStreamProjection_Async() {
    // Arrange
    var analyzer = new MartenAnalyzer();
    var sourceCode = """
      using Marten.Events.Aggregation;

      public class OrderProjection : SingleStreamProjection<Order> {
        public void Apply(OrderCreated @event, Order state) {
          state.Id = @event.OrderId;
        }
      }

      public class Order {
        public string Id { get; set; }
      }
      public record OrderCreated(string OrderId);
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Projections/OrderProjection.cs");

    // Assert
    await Assert.That(result.Projections.Count).IsEqualTo(1);
    await Assert.That(result.Projections[0].ClassName).IsEqualTo("OrderProjection");
    await Assert.That(result.Projections[0].AggregateType).IsEqualTo("Order");
    await Assert.That(result.Projections[0].ProjectionKind).IsEqualTo(ProjectionKind.SingleStream);
  }

  [Test]
  public async Task AnalyzeAsync_DetectsMultiStreamProjection_Async() {
    // Arrange
    var analyzer = new MartenAnalyzer();
    var sourceCode = """
      using Marten.Events.Aggregation;

      public class OrderSummaryProjection : MultiStreamProjection<OrderSummary, Guid> {
        public void Apply(OrderCreated @event, OrderSummary state) { }
        public void Apply(OrderCompleted @event, OrderSummary state) { }
      }

      public class OrderSummary {
        public Guid Id { get; set; }
      }
      public record OrderCreated(string OrderId);
      public record OrderCompleted(string OrderId);
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Projections/OrderSummaryProjection.cs");

    // Assert
    await Assert.That(result.Projections.Count).IsEqualTo(1);
    await Assert.That(result.Projections[0].ProjectionKind).IsEqualTo(ProjectionKind.MultiStream);
  }

  [Test]
  public async Task AnalyzeAsync_DetectsEventTypesInProjection_Async() {
    // Arrange
    var analyzer = new MartenAnalyzer();
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
    var result = await analyzer.AnalyzeAsync(sourceCode, "Projections/OrderProjection.cs");

    // Assert
    await Assert.That(result.Projections[0].EventTypes.Count).IsEqualTo(3);
    await Assert.That(result.Projections[0].EventTypes).Contains("OrderCreated");
    await Assert.That(result.Projections[0].EventTypes).Contains("OrderUpdated");
    await Assert.That(result.Projections[0].EventTypes).Contains("OrderCancelled");
  }

  [Test]
  public async Task AnalyzeAsync_DetectsDocumentStoreInjection_Async() {
    // Arrange
    var analyzer = new MartenAnalyzer();
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
    var result = await analyzer.AnalyzeAsync(sourceCode, "Services/OrderService.cs");

    // Assert
    await Assert.That(result.EventStoreUsages.Count).IsGreaterThanOrEqualTo(1);
    await Assert.That(result.EventStoreUsages.Any(u => u.UsageKind == EventStoreUsageKind.DocumentStoreInjection)).IsTrue();
  }

  [Test]
  public async Task AnalyzeAsync_DetectsDocumentSessionUsage_Async() {
    // Arrange
    var analyzer = new MartenAnalyzer();
    var sourceCode = """
      using Marten;

      public class OrderHandler {
        private readonly IDocumentSession _session;

        public OrderHandler(IDocumentSession session) {
          _session = session;
        }

        public async Task Handle(CreateOrder command) {
          _session.Store(new Order());
          await _session.SaveChangesAsync();
        }
      }

      public class Order { }
      public record CreateOrder();
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Handlers/OrderHandler.cs");

    // Assert
    await Assert.That(result.EventStoreUsages.Any(u => u.UsageKind == EventStoreUsageKind.DocumentSessionUsage)).IsTrue();
  }

  [Test]
  public async Task AnalyzeAsync_DetectsAddMartenRegistration_Async() {
    // Arrange
    var analyzer = new MartenAnalyzer();
    var sourceCode = """
      using Marten;
      using Microsoft.Extensions.DependencyInjection;

      public static class ServiceCollectionExtensions {
        public static void AddServices(this IServiceCollection services) {
          services.AddMarten(options => {
            options.Connection("connection-string");
          });
        }
      }
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Extensions/ServiceCollectionExtensions.cs");

    // Assert
    await Assert.That(result.DIRegistrations.Count).IsGreaterThanOrEqualTo(1);
    await Assert.That(result.DIRegistrations.Any(r => r.RegistrationKind == DIRegistrationKind.AddMarten)).IsTrue();
  }

  [Test]
  public async Task AnalyzeAsync_DetectsUseWolverineRegistration_Async() {
    // Arrange
    var analyzer = new MartenAnalyzer();
    var sourceCode = """
      using Wolverine;
      using Microsoft.AspNetCore.Builder;

      public class Program {
        public static void Main(string[] args) {
          var builder = WebApplication.CreateBuilder(args);
          builder.Host.UseWolverine();
        }
      }
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Program.cs");

    // Assert
    await Assert.That(result.DIRegistrations.Any(r => r.RegistrationKind == DIRegistrationKind.UseWolverine)).IsTrue();
  }

  [Test]
  public async Task AnalyzeAsync_IgnoresNonMartenClasses_Async() {
    // Arrange
    var analyzer = new MartenAnalyzer();
    var sourceCode = """
      public class OrderService {
        public void ProcessOrder(Order order) { }
      }

      public class Order { }
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Services/OrderService.cs");

    // Assert
    await Assert.That(result.Projections).IsEmpty();
    await Assert.That(result.EventStoreUsages).IsEmpty();
    await Assert.That(result.DIRegistrations).IsEmpty();
  }

  [Test]
  public async Task AnalyzeAsync_CapturesLineNumber_Async() {
    // Arrange
    var analyzer = new MartenAnalyzer();
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
    var result = await analyzer.AnalyzeAsync(sourceCode, "Projections/OrderProjection.cs");

    // Assert
    await Assert.That(result.Projections[0].LineNumber).IsGreaterThan(0);
  }

  [Test]
  public async Task AnalyzeAsync_HandlesEmptySourceCode_Async() {
    // Arrange
    var analyzer = new MartenAnalyzer();

    // Act
    var result = await analyzer.AnalyzeAsync("", "Empty.cs");

    // Assert
    await Assert.That(result.Handlers).IsEmpty();
    await Assert.That(result.Projections).IsEmpty();
    await Assert.That(result.EventStoreUsages).IsEmpty();
    await Assert.That(result.DIRegistrations).IsEmpty();
  }

  [Test]
  public async Task AnalyzeAsync_DetectsMultipleProjectionsInSameFile_Async() {
    // Arrange
    var analyzer = new MartenAnalyzer();
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
    var result = await analyzer.AnalyzeAsync(sourceCode, "Projections/AllProjections.cs");

    // Assert
    await Assert.That(result.Projections.Count).IsEqualTo(2);
  }
}
