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

  // ============================================================
  // Marten/Wolverine Pattern Scenarios (H01-H07)
  // ============================================================

  [Test]
  public async Task TransformAsync_H01_WolverineHandlerWithDocumentSession_TransformsToIReceptorAsync() {
    // Arrange - H01: Wolverine IHandle<T> with Marten IDocumentSession
    var transformer = new HandlerToReceptorTransformer();
    var sourceCode = """
      using Wolverine;

      public class CreateOrderHandler : IHandle<CreateOrderCommand> {
        private readonly IDocumentSession _session;

        public CreateOrderHandler(IDocumentSession session) {
          _session = session;
        }

        public async Task Handle(CreateOrderCommand command) {
          var orderId = Guid.NewGuid();
          _session.Events.StartStream<Order>(orderId, new OrderCreated(orderId));
          await _session.SaveChangesAsync();
        }
      }

      public record CreateOrderCommand(Guid CustomerId);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Handler.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IReceptor<CreateOrderCommand");
    await Assert.That(result.TransformedCode).DoesNotContain("IHandle<");
    await Assert.That(result.TransformedCode).Contains("ReceiveAsync(");
    await Assert.That(result.Changes.Any(c =>
        c.ChangeType == ChangeType.InterfaceReplacement)).IsTrue();
  }

  [Test]
  public async Task TransformAsync_H02_NestedStaticClassHandlers_TransformsToSeparateReceptorsAsync() {
    // Arrange - H02: Nested static class handlers
    var transformer = new HandlerToReceptorTransformer();
    var sourceCode = """
      using Wolverine;

      public static class OrderHandlers {
        public class CreateHandler : IHandle<CreateOrderCommand> {
          public Task Handle(CreateOrderCommand command) => Task.CompletedTask;
        }

        public class UpdateHandler : IHandle<UpdateOrderCommand> {
          public Task Handle(UpdateOrderCommand command) => Task.CompletedTask;
        }
      }

      public record CreateOrderCommand(string OrderId);
      public record UpdateOrderCommand(string OrderId);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderHandlers.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IReceptor<CreateOrderCommand>");
    await Assert.That(result.TransformedCode).Contains("IReceptor<UpdateOrderCommand>");
    // Warning should be emitted for nested handler pattern
    await Assert.That(result.Warnings.Any(w => w.Contains("nested"))).IsTrue();
  }

  [Test]
  public async Task TransformAsync_H03_WolverineRpcHandler_TransformsToIReceptorWithResultAsync() {
    // Arrange - H03: Wolverine RPC handlers with request/response using IHandle<T, TResult>
    var transformer = new HandlerToReceptorTransformer();
    var sourceCode = """
      using Wolverine;

      public class GetOrderHandler : IHandle<GetOrderQuery, OrderResult> {
        private readonly IQuerySession _session;

        public GetOrderHandler(IQuerySession session) {
          _session = session;
        }

        public async Task<OrderResult> Handle(GetOrderQuery query) {
          var order = await _session.LoadAsync<Order>(query.OrderId);
          return new OrderResult(order.Id, order.Status);
        }
      }

      public record GetOrderQuery(Guid OrderId);
      public record OrderResult(Guid OrderId, string Status);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Handler.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IReceptor<GetOrderQuery, OrderResult>");
    await Assert.That(result.TransformedCode).DoesNotContain("IHandle<");
    await Assert.That(result.TransformedCode).Contains("ReceiveAsync");
  }

  [Test]
  public async Task TransformAsync_H04_LocalMessageWrapper_TransformsToLocalInvokeAsync() {
    // Arrange - H04: LocalMessage<T> wrapper for in-process calls
    var transformer = new HandlerToReceptorTransformer();
    var sourceCode = """
      using Wolverine;

      public class OrderService {
        private readonly IMessageBus _bus;

        public OrderService(IMessageBus bus) {
          _bus = bus;
        }

        public async Task ProcessAsync(ProcessOrderCommand command, CancellationToken ct) {
          // LocalMessage<T> indicates in-process invocation
          await _bus.InvokeAsync(new LocalMessage<ValidateOrderCommand>(
              new ValidateOrderCommand(command.OrderId)));
        }
      }

      public record ProcessOrderCommand(Guid OrderId);
      public record ValidateOrderCommand(Guid OrderId);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Service.cs");

    // Assert
    // Should convert LocalMessage pattern to LocalInvokeAsync
    await Assert.That(result.TransformedCode).Contains("IDispatcher");
    await Assert.That(result.TransformedCode).Contains("LocalInvokeAsync");
    await Assert.That(result.TransformedCode).DoesNotContain("LocalMessage<");
  }

  [Test]
  public async Task TransformAsync_H05_HandlerWithNotificationService_PreservesDependencyAsync() {
    // Arrange - H05: Handler with notification service dependency
    var transformer = new HandlerToReceptorTransformer();
    var sourceCode = """
      using Wolverine;

      public class OrderCompletedHandler : IHandle<OrderCompletedEvent> {
        private readonly INotificationService _notificationService;
        private readonly ILogger<OrderCompletedHandler> _logger;

        public OrderCompletedHandler(
            INotificationService notificationService,
            ILogger<OrderCompletedHandler> logger) {
          _notificationService = notificationService;
          _logger = logger;
        }

        public async Task Handle(OrderCompletedEvent @event) {
          _logger.LogInformation("Order {OrderId} completed", @event.OrderId);
          await _notificationService.SendAsync(
              @event.CustomerId,
              $"Your order {@event.OrderId} has been completed!");
        }
      }

      public record OrderCompletedEvent(Guid OrderId, Guid CustomerId);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Handler.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IReceptor<OrderCompletedEvent>");
    await Assert.That(result.TransformedCode).Contains("INotificationService _notificationService");
    await Assert.That(result.TransformedCode).Contains("ILogger<");
    await Assert.That(result.TransformedCode).Contains("_notificationService.SendAsync");
  }

  [Test]
  public async Task TransformAsync_H06_HandlerWithTokenEnrichment_TransformsToMessageEnvelopeAsync() {
    // Arrange - H06: Handler accessing correlation/token context
    var transformer = new HandlerToReceptorTransformer();
    var sourceCode = """
      using Wolverine;

      public class AuditedHandler : IHandle<AuditedCommand> {
        private readonly MessageContext _context;

        public AuditedHandler(MessageContext context) {
          _context = context;
        }

        public async Task Handle(AuditedCommand command) {
          var correlationId = _context.CorrelationId;
          var tenantId = _context.TenantId;
          // Use correlation for audit trail
          await AuditAsync(command, correlationId, tenantId);
        }

        private Task AuditAsync(AuditedCommand cmd, Guid? correlationId, string tenantId)
            => Task.CompletedTask;
      }

      public record AuditedCommand(string Data);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Handler.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IReceptor<AuditedCommand>");
    // Should transform MessageContext to MessageEnvelope pattern
    await Assert.That(result.TransformedCode).Contains("MessageEnvelope");
    await Assert.That(result.Warnings.Any(w =>
        w.Contains("MessageContext") || w.Contains("correlation"))).IsTrue();
  }

  [Test]
  public async Task TransformAsync_H07_HandlerWithTelemetryActivity_TransformsWithObservabilityAsync() {
    // Arrange - H07: Handler with Activity tracing
    var transformer = new HandlerToReceptorTransformer();
    var sourceCode = """
      using System.Diagnostics;
      using Wolverine;

      public class TracedHandler : IHandle<TracedCommand> {
        private static readonly ActivitySource ActivitySource = new("MyApp.Handlers");

        public async Task Handle(TracedCommand command) {
          using var activity = ActivitySource.StartActivity("ProcessTracedCommand");
          activity?.SetTag("command.id", command.Id);

          await ProcessAsync(command);

          activity?.SetStatus(ActivityStatusCode.Ok);
        }

        private Task ProcessAsync(TracedCommand cmd) => Task.CompletedTask;
      }

      public record TracedCommand(string Id);
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "Handler.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IReceptor<TracedCommand>");
    // Activity/observability code should be preserved
    await Assert.That(result.TransformedCode).Contains("ActivitySource");
    await Assert.That(result.TransformedCode).Contains("StartActivity");
    // Warning about observability migration
    await Assert.That(result.Warnings.Any(w =>
        w.Contains("Activity") || w.Contains("observability"))).IsTrue();
  }
}
