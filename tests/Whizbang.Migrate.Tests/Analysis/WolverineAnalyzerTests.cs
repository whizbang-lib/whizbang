using Whizbang.Migrate.Analysis;

namespace Whizbang.Migrate.Tests.Analysis;

/// <summary>
/// Tests for the Wolverine pattern analyzer that detects handlers to migrate.
/// </summary>
/// <tests>Whizbang.Migrate/Analysis/WolverineAnalyzer.cs:*</tests>
public class WolverineAnalyzerTests {
  [Test]
  public async Task AnalyzeAsync_DetectsIHandleInterface_Async() {
    // Arrange
    var analyzer = new WolverineAnalyzer();
    var sourceCode = """
      using Wolverine;

      public class CreateOrderHandler : IHandle<CreateOrderCommand> {
        public Task Handle(CreateOrderCommand command) {
          return Task.CompletedTask;
        }
      }

      public record CreateOrderCommand(string OrderId);
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Handlers/CreateOrderHandler.cs");

    // Assert
    await Assert.That(result.Handlers.Count).IsEqualTo(1);
    await Assert.That(result.Handlers[0].ClassName).IsEqualTo("CreateOrderHandler");
    await Assert.That(result.Handlers[0].MessageType).IsEqualTo("CreateOrderCommand");
    await Assert.That(result.Handlers[0].HandlerKind).IsEqualTo(HandlerKind.IHandleInterface);
  }

  [Test]
  public async Task AnalyzeAsync_DetectsIHandleWithResult_Async() {
    // Arrange
    var analyzer = new WolverineAnalyzer();
    var sourceCode = """
      using Wolverine;

      public class GetOrderHandler : IHandle<GetOrderQuery, OrderResult> {
        public Task<OrderResult> Handle(GetOrderQuery query) {
          return Task.FromResult(new OrderResult());
        }
      }

      public record GetOrderQuery(string OrderId);
      public record OrderResult();
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Handlers/GetOrderHandler.cs");

    // Assert
    await Assert.That(result.Handlers.Count).IsEqualTo(1);
    await Assert.That(result.Handlers[0].MessageType).IsEqualTo("GetOrderQuery");
    await Assert.That(result.Handlers[0].ReturnType).IsEqualTo("OrderResult");
  }

  [Test]
  public async Task AnalyzeAsync_DetectsWolverineHandlerAttribute_Async() {
    // Arrange
    var analyzer = new WolverineAnalyzer();
    var sourceCode = """
      using Wolverine;

      [WolverineHandler]
      public class NotificationHandler {
        public Task Handle(SendNotificationCommand command) {
          return Task.CompletedTask;
        }
      }

      public record SendNotificationCommand(string Message);
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Handlers/NotificationHandler.cs");

    // Assert
    await Assert.That(result.Handlers.Count).IsEqualTo(1);
    await Assert.That(result.Handlers[0].ClassName).IsEqualTo("NotificationHandler");
    await Assert.That(result.Handlers[0].HandlerKind).IsEqualTo(HandlerKind.WolverineAttribute);
  }

  [Test]
  public async Task AnalyzeAsync_DetectsConventionBasedHandler_Async() {
    // Arrange
    var analyzer = new WolverineAnalyzer();
    var sourceCode = """
      public class OrderHandlers {
        public static Task Handle(CreateOrderCommand command) {
          return Task.CompletedTask;
        }

        public static Task<OrderResult> HandleAsync(GetOrderQuery query) {
          return Task.FromResult(new OrderResult());
        }
      }

      public record CreateOrderCommand(string OrderId);
      public record GetOrderQuery(string OrderId);
      public record OrderResult();
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Handlers/OrderHandlers.cs");

    // Assert
    await Assert.That(result.Handlers.Count).IsEqualTo(2);
    await Assert.That(result.Handlers.All(h => h.HandlerKind == HandlerKind.ConventionBased)).IsTrue();
  }

  [Test]
  public async Task AnalyzeAsync_DetectsMultipleHandlersInSameFile_Async() {
    // Arrange
    var analyzer = new WolverineAnalyzer();
    var sourceCode = """
      using Wolverine;

      public class CreateOrderHandler : IHandle<CreateOrderCommand> {
        public Task Handle(CreateOrderCommand command) => Task.CompletedTask;
      }

      public class UpdateOrderHandler : IHandle<UpdateOrderCommand> {
        public Task Handle(UpdateOrderCommand command) => Task.CompletedTask;
      }

      public class DeleteOrderHandler : IHandle<DeleteOrderCommand> {
        public Task Handle(DeleteOrderCommand command) => Task.CompletedTask;
      }

      public record CreateOrderCommand(string OrderId);
      public record UpdateOrderCommand(string OrderId);
      public record DeleteOrderCommand(string OrderId);
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Handlers/OrderHandlers.cs");

    // Assert
    await Assert.That(result.Handlers.Count).IsEqualTo(3);
  }

  [Test]
  public async Task AnalyzeAsync_IgnoresNonHandlerClasses_Async() {
    // Arrange
    var analyzer = new WolverineAnalyzer();
    var sourceCode = """
      public class OrderService {
        public Task ProcessOrder(Order order) {
          return Task.CompletedTask;
        }
      }

      public class Order {
        public string Id { get; set; }
      }
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Services/OrderService.cs");

    // Assert
    await Assert.That(result.Handlers).IsEmpty();
  }

  [Test]
  public async Task AnalyzeAsync_CapturesLineNumber_Async() {
    // Arrange
    var analyzer = new WolverineAnalyzer();
    var sourceCode = """
      using Wolverine;

      namespace MyApp.Handlers;

      public class CreateOrderHandler : IHandle<CreateOrderCommand> {
        public Task Handle(CreateOrderCommand command) => Task.CompletedTask;
      }

      public record CreateOrderCommand(string OrderId);
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Handlers/CreateOrderHandler.cs");

    // Assert
    await Assert.That(result.Handlers[0].LineNumber).IsGreaterThan(0);
  }

  [Test]
  public async Task AnalyzeAsync_CapturesFullyQualifiedName_Async() {
    // Arrange
    var analyzer = new WolverineAnalyzer();
    var sourceCode = """
      using Wolverine;

      namespace MyApp.Handlers;

      public class CreateOrderHandler : IHandle<CreateOrderCommand> {
        public Task Handle(CreateOrderCommand command) => Task.CompletedTask;
      }

      public record CreateOrderCommand(string OrderId);
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Handlers/CreateOrderHandler.cs");

    // Assert
    await Assert.That(result.Handlers[0].FullyQualifiedName).IsEqualTo("MyApp.Handlers.CreateOrderHandler");
  }

  [Test]
  public async Task AnalyzeAsync_HandlesEmptySourceCode_Async() {
    // Arrange
    var analyzer = new WolverineAnalyzer();

    // Act
    var result = await analyzer.AnalyzeAsync("", "Empty.cs");

    // Assert
    await Assert.That(result.Handlers).IsEmpty();
    await Assert.That(result.Projections).IsEmpty();
    await Assert.That(result.EventStoreUsages).IsEmpty();
    await Assert.That(result.DIRegistrations).IsEmpty();
  }

  [Test]
  public async Task AnalyzeAsync_HandlesInvalidSyntax_Async() {
    // Arrange
    var analyzer = new WolverineAnalyzer();
    var sourceCode = "this is not valid C# code { } class";

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Invalid.cs");

    // Assert - should not throw, just return empty results
    await Assert.That(result.Handlers).IsEmpty();
  }

  [Test]
  public async Task AnalyzeAsync_DetectsInstanceHandler_Async() {
    // Arrange
    var analyzer = new WolverineAnalyzer();
    var sourceCode = """
      public class OrderHandler {
        private readonly IOrderRepository _repository;

        public OrderHandler(IOrderRepository repository) {
          _repository = repository;
        }

        public async Task Handle(CreateOrderCommand command) {
          await _repository.CreateAsync(command);
        }
      }

      public interface IOrderRepository {
        Task CreateAsync(CreateOrderCommand command);
      }
      public record CreateOrderCommand(string OrderId);
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Handlers/OrderHandler.cs");

    // Assert
    await Assert.That(result.Handlers.Count).IsEqualTo(1);
    await Assert.That(result.Handlers[0].HandlerKind).IsEqualTo(HandlerKind.ConventionBased);
  }

  [Test]
  public async Task AnalyzeAsync_FiltersOutPrivateHandleMethods_Async() {
    // Arrange
    var analyzer = new WolverineAnalyzer();
    var sourceCode = """
      public class OrderHandler {
        public Task Handle(CreateOrderCommand command) => _handleInternal(command);

        private Task _handleInternal(CreateOrderCommand command) {
          return Task.CompletedTask;
        }
      }

      public record CreateOrderCommand(string OrderId);
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Handlers/OrderHandler.cs");

    // Assert - should only detect the public Handle method
    await Assert.That(result.Handlers.Count).IsEqualTo(1);
  }
}
