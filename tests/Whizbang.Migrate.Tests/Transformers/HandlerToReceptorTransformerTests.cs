using Whizbang.Migrate.Transformers;

namespace Whizbang.Migrate.Tests.Transformers;

/// <summary>
/// Tests for the Handler to Receptor transformer that converts Wolverine handlers to Whizbang receptors.
/// </summary>
/// <tests>Whizbang.Migrate/Transformers/HandlerToReceptorTransformer.cs:*</tests>
public class HandlerToReceptorTransformerTests {
  [Test]
  public async Task TransformAsync_ConvertsIHandleToIReceptor_Async() {
    // Arrange
    var transformer = new HandlerToReceptorTransformer();
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
    var result = await transformer.TransformAsync(sourceCode, "Handler.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IReceptor<CreateOrderCommand>");
    await Assert.That(result.TransformedCode).DoesNotContain("IHandle<");
  }

  [Test]
  public async Task TransformAsync_ConvertsIHandleWithResultToIReceptorWithResult_Async() {
    // Arrange
    var transformer = new HandlerToReceptorTransformer();
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
    var result = await transformer.TransformAsync(sourceCode, "Handler.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IReceptor<GetOrderQuery, OrderResult>");
  }

  [Test]
  public async Task TransformAsync_RenamesHandleMethodToReceiveAsync_Async() {
    // Arrange
    var transformer = new HandlerToReceptorTransformer();
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
    var result = await transformer.TransformAsync(sourceCode, "Handler.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("ReceiveAsync(");
    await Assert.That(result.TransformedCode).DoesNotContain("Handle(CreateOrderCommand");
  }

  [Test]
  public async Task TransformAsync_UpdatesUsingDirectives_Async() {
    // Arrange
    var transformer = new HandlerToReceptorTransformer();
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
    var result = await transformer.TransformAsync(sourceCode, "Handler.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("using Whizbang.Core;");
    await Assert.That(result.TransformedCode).DoesNotContain("using Wolverine;");
  }

  [Test]
  public async Task TransformAsync_RemovesWolverineHandlerAttribute_Async() {
    // Arrange
    var transformer = new HandlerToReceptorTransformer();
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
    var result = await transformer.TransformAsync(sourceCode, "Handler.cs");

    // Assert
    await Assert.That(result.TransformedCode).DoesNotContain("[WolverineHandler]");
  }

  [Test]
  public async Task TransformAsync_TracksChanges_Async() {
    // Arrange
    var transformer = new HandlerToReceptorTransformer();
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
    var result = await transformer.TransformAsync(sourceCode, "Handler.cs");

    // Assert
    await Assert.That(result.Changes.Count).IsGreaterThan(0);
    await Assert.That(result.Changes.Any(c => c.ChangeType == ChangeType.InterfaceReplacement)).IsTrue();
  }

  [Test]
  public async Task TransformAsync_PreservesClassBody_Async() {
    // Arrange
    var transformer = new HandlerToReceptorTransformer();
    var sourceCode = """
      using Wolverine;

      public class CreateOrderHandler : IHandle<CreateOrderCommand> {
        private readonly IOrderRepository _repository;

        public CreateOrderHandler(IOrderRepository repository) {
          _repository = repository;
        }

        public async Task Handle(CreateOrderCommand command) {
          await _repository.CreateAsync(command.OrderId);
        }
      }

      public interface IOrderRepository {
        Task CreateAsync(string orderId);
      }
      public record CreateOrderCommand(string OrderId);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Handler.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("_repository");
    await Assert.That(result.TransformedCode).Contains("IOrderRepository");
    await Assert.That(result.TransformedCode).Contains("await _repository.CreateAsync");
  }

  [Test]
  public async Task TransformAsync_HandlesMultipleHandlersInFile_Async() {
    // Arrange
    var transformer = new HandlerToReceptorTransformer();
    var sourceCode = """
      using Wolverine;

      public class CreateOrderHandler : IHandle<CreateOrderCommand> {
        public Task Handle(CreateOrderCommand command) => Task.CompletedTask;
      }

      public class UpdateOrderHandler : IHandle<UpdateOrderCommand> {
        public Task Handle(UpdateOrderCommand command) => Task.CompletedTask;
      }

      public record CreateOrderCommand(string OrderId);
      public record UpdateOrderCommand(string OrderId);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Handlers.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IReceptor<CreateOrderCommand>");
    await Assert.That(result.TransformedCode).Contains("IReceptor<UpdateOrderCommand>");
  }

  [Test]
  public async Task TransformAsync_PreservesNonHandlerClasses_Async() {
    // Arrange
    var transformer = new HandlerToReceptorTransformer();
    var sourceCode = """
      using Wolverine;

      public class CreateOrderHandler : IHandle<CreateOrderCommand> {
        public Task Handle(CreateOrderCommand command) => Task.CompletedTask;
      }

      public class OrderService {
        public void DoSomething() { }
      }

      public record CreateOrderCommand(string OrderId);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Mixed.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("class OrderService");
    await Assert.That(result.TransformedCode).Contains("DoSomething");
  }

  [Test]
  public async Task TransformAsync_NoHandlers_ReturnsUnchanged_Async() {
    // Arrange
    var transformer = new HandlerToReceptorTransformer();
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
    var transformer = new HandlerToReceptorTransformer();
    var sourceCode = """
      using Wolverine;

      namespace MyApp.Handlers;

      public class CreateOrderHandler : IHandle<CreateOrderCommand> {
        public Task Handle(CreateOrderCommand command) => Task.CompletedTask;
      }

      public record CreateOrderCommand(string OrderId);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Handler.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("namespace MyApp.Handlers;");
  }
}
