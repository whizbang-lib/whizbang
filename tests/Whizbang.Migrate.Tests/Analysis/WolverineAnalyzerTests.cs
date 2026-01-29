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

  // =============== Custom Base Class Warning Tests ===============

  [Test]
  public async Task AnalyzeAsync_HandlerWithCustomBaseClass_GeneratesWarning_Async() {
    // Arrange
    var analyzer = new WolverineAnalyzer();
    var sourceCode = """
      using Wolverine;

      namespace MyApp.Handlers;

      public abstract class BaseMessageHandler<T> {
        protected abstract Task HandleCore(T message);
      }

      [WolverineHandler]
      public class CreateOrderHandler : BaseMessageHandler<CreateOrderCommand> {
        protected override Task HandleCore(CreateOrderCommand command) => Task.CompletedTask;

        public Task Handle(CreateOrderCommand command) => HandleCore(command);
      }

      public record CreateOrderCommand(string OrderId);
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Handlers/CreateOrderHandler.cs");

    // Assert - should generate warning for custom base class
    await Assert.That(result.Warnings.Count).IsEqualTo(1);
    await Assert.That(result.Warnings[0].WarningKind).IsEqualTo(MigrationWarningKind.CustomHandlerBaseClass);
    await Assert.That(result.Warnings[0].ClassName).IsEqualTo("CreateOrderHandler");
    await Assert.That(result.Warnings[0].Details).IsEqualTo("BaseMessageHandler<CreateOrderCommand>");
  }

  [Test]
  public async Task AnalyzeAsync_HandlerWithIHandleInterface_DoesNotGenerateBaseClassWarning_Async() {
    // Arrange
    var analyzer = new WolverineAnalyzer();
    var sourceCode = """
      using Wolverine;

      public class CreateOrderHandler : IHandle<CreateOrderCommand> {
        public Task Handle(CreateOrderCommand command) => Task.CompletedTask;
      }

      public record CreateOrderCommand(string OrderId);
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Handlers/CreateOrderHandler.cs");

    // Assert - IHandle is a known Wolverine interface, should NOT generate warning
    await Assert.That(result.Warnings).IsEmpty();
  }

  [Test]
  public async Task AnalyzeAsync_HandlerWithMultipleBaseTypes_WarnsOnlyForUnknown_Async() {
    // Arrange
    var analyzer = new WolverineAnalyzer();
    var sourceCode = """
      using Wolverine;

      public interface ICustomLogger {
        void Log(string message);
      }

      public abstract class CustomBaseHandler {
        protected void LogEvent(string msg) { }
      }

      [WolverineHandler]
      public class CreateOrderHandler : CustomBaseHandler, IHandle<CreateOrderCommand>, ICustomLogger {
        public Task Handle(CreateOrderCommand command) => Task.CompletedTask;
        public void Log(string message) { }
      }

      public record CreateOrderCommand(string OrderId);
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Handlers/CreateOrderHandler.cs");

    // Assert - should warn about CustomBaseHandler (class) but not IHandle or ICustomLogger
    await Assert.That(result.Warnings.Count).IsEqualTo(1);
    await Assert.That(result.Warnings[0].Details).IsEqualTo("CustomBaseHandler");
  }

  [Test]
  public async Task AnalyzeAsync_NonHandlerClassWithBaseClass_DoesNotGenerateWarning_Async() {
    // Arrange
    var analyzer = new WolverineAnalyzer();
    var sourceCode = """
      public abstract class BaseService {
        protected void DoSomething() { }
      }

      public class OrderService : BaseService {
        public void ProcessOrder(string orderId) { }
      }
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Services/OrderService.cs");

    // Assert - not a handler, no warnings
    await Assert.That(result.Handlers).IsEmpty();
    await Assert.That(result.Warnings).IsEmpty();
  }

  // =============== Unknown Parameter Warning Tests ===============

  [Test]
  public async Task AnalyzeAsync_HandlerWithUnknownInterfaceParameter_GeneratesWarning_Async() {
    // Arrange
    var analyzer = new WolverineAnalyzer();
    var sourceCode = """
      using Wolverine;

      public interface IEventStoreContext {
        Task AppendEventAsync(object evt);
      }

      [WolverineHandler]
      public class CreateOrderHandler {
        public Task Handle(CreateOrderCommand command, IEventStoreContext context) {
          return Task.CompletedTask;
        }
      }

      public record CreateOrderCommand(string OrderId);
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Handlers/CreateOrderHandler.cs");

    // Assert - should warn about unknown interface parameter
    await Assert.That(result.Warnings.Count).IsEqualTo(1);
    await Assert.That(result.Warnings[0].WarningKind).IsEqualTo(MigrationWarningKind.UnknownInterfaceParameter);
    await Assert.That(result.Warnings[0].Details).IsEqualTo("IEventStoreContext");
  }

  [Test]
  public async Task AnalyzeAsync_HandlerWithKnownWolverineParameters_DoesNotGenerateWarning_Async() {
    // Arrange
    var analyzer = new WolverineAnalyzer();
    var sourceCode = """
      using Wolverine;

      public class CreateOrderHandler : IHandle<CreateOrderCommand> {
        public Task Handle(CreateOrderCommand command, IMessageBus bus, MessageContext context) {
          return Task.CompletedTask;
        }
      }

      public record CreateOrderCommand(string OrderId);
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Handlers/CreateOrderHandler.cs");

    // Assert - IMessageBus and MessageContext are known Wolverine types
    await Assert.That(result.Warnings).IsEmpty();
  }

  [Test]
  public async Task AnalyzeAsync_HandlerWithKnownMartenParameters_DoesNotGenerateWarning_Async() {
    // Arrange
    var analyzer = new WolverineAnalyzer();
    var sourceCode = """
      using Wolverine;
      using Marten;

      public class CreateOrderHandler : IHandle<CreateOrderCommand> {
        public Task Handle(CreateOrderCommand command, IDocumentSession session, IQuerySession query) {
          return Task.CompletedTask;
        }
      }

      public record CreateOrderCommand(string OrderId);
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Handlers/CreateOrderHandler.cs");

    // Assert - IDocumentSession and IQuerySession are known Marten types
    await Assert.That(result.Warnings).IsEmpty();
  }

  [Test]
  public async Task AnalyzeAsync_HandlerWithCustomContextParameter_GeneratesContextWarning_Async() {
    // Arrange
    var analyzer = new WolverineAnalyzer();
    var sourceCode = """
      using Wolverine;

      public class CustomContext {
        public string TenantId { get; set; }
        public IDocumentSession Session { get; set; }
      }

      [WolverineHandler]
      public class CreateOrderHandler {
        public Task Handle(CreateOrderCommand command, CustomContext context) {
          return Task.CompletedTask;
        }
      }

      public record CreateOrderCommand(string OrderId);
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Handlers/CreateOrderHandler.cs");

    // Assert - CustomContext is a class parameter with "Context" in the name
    await Assert.That(result.Warnings.Count).IsEqualTo(1);
    await Assert.That(result.Warnings[0].WarningKind).IsEqualTo(MigrationWarningKind.CustomContextParameter);
    await Assert.That(result.Warnings[0].Details).IsEqualTo("CustomContext");
  }

  [Test]
  public async Task AnalyzeAsync_HandlerWithCancellationToken_DoesNotGenerateWarning_Async() {
    // Arrange
    var analyzer = new WolverineAnalyzer();
    var sourceCode = """
      using Wolverine;
      using System.Threading;

      public class CreateOrderHandler : IHandle<CreateOrderCommand> {
        public Task Handle(CreateOrderCommand command, CancellationToken ct) {
          return Task.CompletedTask;
        }
      }

      public record CreateOrderCommand(string OrderId);
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Handlers/CreateOrderHandler.cs");

    // Assert - CancellationToken is a standard parameter
    await Assert.That(result.Warnings).IsEmpty();
  }

  [Test]
  public async Task AnalyzeAsync_HandlerWithMultipleUnknownParameters_GeneratesMultipleWarnings_Async() {
    // Arrange
    var analyzer = new WolverineAnalyzer();
    var sourceCode = """
      using Wolverine;

      public interface ICustomLogger { }
      public interface IEventStore { }

      [WolverineHandler]
      public class CreateOrderHandler {
        public Task Handle(CreateOrderCommand command, ICustomLogger logger, IEventStore store) {
          return Task.CompletedTask;
        }
      }

      public record CreateOrderCommand(string OrderId);
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Handlers/CreateOrderHandler.cs");

    // Assert - should have warnings for both unknown interfaces
    await Assert.That(result.Warnings.Count).IsEqualTo(2);
    await Assert.That(result.Warnings.All(w => w.WarningKind == MigrationWarningKind.UnknownInterfaceParameter)).IsTrue();
  }

  [Test]
  public async Task AnalyzeAsync_NestedHandlerClass_GeneratesNestedClassWarning_Async() {
    // Arrange
    var analyzer = new WolverineAnalyzer();
    var sourceCode = """
      using Wolverine;

      public static class OrderHandlers {
        public class CreateOrderHandler : IHandle<CreateOrderCommand> {
          public Task Handle(CreateOrderCommand command) => Task.CompletedTask;
        }
      }

      public record CreateOrderCommand(string OrderId);
      """;

    // Act
    var result = await analyzer.AnalyzeAsync(sourceCode, "Handlers/OrderHandlers.cs");

    // Assert - should warn about nested handler class
    await Assert.That(result.Warnings.Count).IsEqualTo(1);
    await Assert.That(result.Warnings[0].WarningKind).IsEqualTo(MigrationWarningKind.NestedHandlerClass);
    await Assert.That(result.Warnings[0].ClassName).IsEqualTo("CreateOrderHandler");
  }
}
