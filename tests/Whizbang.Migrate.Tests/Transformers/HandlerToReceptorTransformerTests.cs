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
    const string sourceCode = """
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
    const string sourceCode = """
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
    const string sourceCode = """
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
    const string sourceCode = """
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
    const string sourceCode = """
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
    const string sourceCode = """
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
    const string sourceCode = """
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
    const string sourceCode = """
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
    const string sourceCode = """
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
    var transformer = new HandlerToReceptorTransformer();
    const string sourceCode = """
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
    const string sourceCode = """
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
    const string sourceCode = """
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
    const string sourceCode = """
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
    const string sourceCode = """
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
    const string sourceCode = """
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
    const string sourceCode = """
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
    const string sourceCode = """
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


  [Test]
  public async Task TransformAsync_WhizbangCoreAlreadyImported_DropsTheWolverineUsingAsync() {
    // The transformers run in sequence over one file, and several of them rewrite a Wolverine or
    // Marten using into Whizbang.Core. Whichever runs second finds Whizbang.Core already there.
    // Emitting it twice is legal C# but raises CS0105, which fails any migrated project building
    // with warnings-as-errors -- the tool handing back source that will not compile under the
    // settings most projects ship with.
    var transformer = new HandlerToReceptorTransformer();
    const string sourceCode = """
      using Whizbang.Core;
      using Wolverine;

      public class CreateOrderHandler : IHandle<CreateOrderCommand> {
        public Task Handle(CreateOrderCommand command) {
          return Task.CompletedTask;
        }
      }

      public record CreateOrderCommand(string OrderId);
      """;

    var result = await transformer.TransformAsync(sourceCode, "Handler.cs");

    var occurrences = result.TransformedCode.Split("using Whizbang.Core;").Length - 1;
    await Assert.That(occurrences).IsEqualTo(1)
      .Because("a second identical using is CS0105, not a harmless duplicate");
    await Assert.That(result.TransformedCode).DoesNotContain("using Wolverine;")
      .Because("the Wolverine using is still removed -- it is dropped rather than rewritten");
    await Assert.That(result.TransformedCode).Contains("IReceptor<CreateOrderCommand>")
      .Because("the transform itself still has to happen; deduping the using is not a bail-out");
  }


  [Test]
  public async Task TransformAsync_LocalMessageWithoutMessageBus_IsRecognizedAndReportedAsync() {
    // Detection checks IMessageBus first and returns early when it finds one, so every file that
    // mentions both takes the first branch. A file that uses LocalMessage<T> and never names
    // IMessageBus reaches the second, and that is the one nothing exercised. If detection missed
    // it the file would be left alone entirely -- LocalMessage<T> does not exist in Whizbang, so
    // the migrated project would not compile, and the report would say nothing was needed.
    var transformer = new HandlerToReceptorTransformer();
    const string sourceCode = """
      using Wolverine;

      public class OrderNotifier {
        public Task Notify(LocalMessage<OrderPlaced> message) {
          return Task.CompletedTask;
        }
      }

      public record OrderPlaced(string OrderId);
      """;

    var result = await transformer.TransformAsync(sourceCode, "OrderNotifier.cs");

    // LocalMessage<T> here is a parameter TYPE, and there is no type to rename it to --
    // LocalInvokeAsync is a method on the dispatcher. Renaming it in place would produce a
    // signature that does not compile while looking migrated, so the transform leaves it and
    // says so instead.
    await Assert.That(result.TransformedCode).Contains("LocalMessage<OrderPlaced>")
      .Because("there is no type-level equivalent, so the signature is left as the developer wrote it");
    await Assert.That(result.TransformedCode).DoesNotContain("LocalInvokeAsync<OrderPlaced> message")
      .Because("LocalInvokeAsync is a method; putting it in a parameter position is not a migration, it is a break");
    await Assert.That(result.Warnings.Any(w => w.Contains("LocalMessage<T>", StringComparison.Ordinal))).IsTrue()
      .Because("silence here is the real failure -- the project will not build against Whizbang, and "
             + "a report with no warning says nothing was needed");
  }

  [Test]
  public async Task TransformAsync_FileWithoutTheWolverineUsing_IsLeftUntouchedAsync() {
    // The transformer walks every .cs file in the tree. Most have nothing to do with Wolverine,
    // and rewriting usings on one that never imported it would edit code the migration has no
    // business touching.
    var transformer = new HandlerToReceptorTransformer();
    const string sourceCode = """
      using System;

      public class OrderService {
        public string Describe() => "orders";
      }
      """;

    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    await Assert.That(result.TransformedCode).IsEqualTo(sourceCode)
      .Because("a file that never imported Wolverine is not this transformer's business");
  }

  [Test]
  public async Task TransformAsync_AttributedClassWithNoHandleMethod_IsSkippedNotCrashedAsync() {
    // [WolverineHandler] on a class whose handler is named something else, or not written yet.
    // The transformer has nothing to convert and must move on: throwing here would abort the
    // whole file, losing the conversions it had already made for the classes around it.
    var transformer = new HandlerToReceptorTransformer();
    const string sourceCode = """
      using Wolverine;
      using Wolverine.Attributes;

      [WolverineHandler]
      public class OrderHandler {
        public Task Process(OrderPlaced message) {
          return Task.CompletedTask;
        }
      }

      public record OrderPlaced(string OrderId);
      """;

    var result = await transformer.TransformAsync(sourceCode, "OrderHandler.cs");

    await Assert.That(result.TransformedCode).Contains("OrderHandler")
      .Because("the class survives; there was simply no Handle method to convert");
    await Assert.That(async () => await transformer.TransformAsync(sourceCode, "OrderHandler.cs"))
      .ThrowsNothing()
      .Because("a class the transformer cannot convert must not abort the file");
  }

  [Test]
  public async Task TransformAsync_ClassOnAnUnrelatedBase_IsNotTreatedAsAHandlerAsync() {
    // Base-class detection looks for IHandle<T>. A class with some other base sits in the same
    // file and must come through unchanged -- converting it would produce a receptor for a type
    // that never handled anything.
    var transformer = new HandlerToReceptorTransformer();
    const string sourceCode = """
      using Wolverine;

      public class OrderReportBuilder : ReportBuilderBase {
        public void Build() { }
      }

      public class ReportBuilderBase { }
      """;

    var result = await transformer.TransformAsync(sourceCode, "OrderReportBuilder.cs");

    await Assert.That(result.TransformedCode).DoesNotContain("IReceptor<")
      .Because("only an IHandle<T> base makes a class a handler");
    await Assert.That(result.TransformedCode).Contains("ReportBuilderBase")
      .Because("the unrelated base class survives untouched");
  }

  [Test]
  public async Task TransformAsync_HandlerMethodNamedDifferently_SkipsSyncReceptorWarningWithoutFlaggingItAsync() {
    // If a class implements IHandle<T> but its handling method isn't literally named Handle or
    // HandleAsync, the base-list rewrite still converts it to IReceptor<T> -- but there is no
    // method named ReceiveAsync to show for it, and (the point of this test) nothing warns about
    // it either. A migrated class can silently stop implementing the interface it just gained.
    var transformer = new HandlerToReceptorTransformer();
    const string sourceCode = """
      using Wolverine;

      public class ArchiveOrderHandler : IHandle<ArchiveOrderCommand> {
        public Task Archive(ArchiveOrderCommand command) {
          return Task.CompletedTask;
        }
      }

      public record ArchiveOrderCommand(string OrderId);
      """;

    var result = await transformer.TransformAsync(sourceCode, "Handler.cs");

    await Assert.That(result.TransformedCode).Contains("IReceptor<ArchiveOrderCommand>")
      .Because("the base-type rewrite runs independently of the method name");
    await Assert.That(result.TransformedCode).Contains("public Task Archive(ArchiveOrderCommand command)")
      .Because("only methods literally named Handle or HandleAsync are renamed");
    await Assert.That(result.Warnings).IsEmpty()
      .Because("no warning exists for a handler whose method the renamer can't find -- the gap is silent");
  }

  [Test]
  public async Task TransformAsync_WolverineHandlerAttributeWithoutWolverineUsing_LeavesUsingsUntouchedAsync() {
    // Detection can trigger purely off the [WolverineHandler] attribute with no "using Wolverine;"
    // anywhere in the file (brought in globally, say). The using-rewrite step then has nothing to
    // replace and must leave the (here, empty) using list alone rather than inserting an import the
    // file never needed.
    var transformer = new HandlerToReceptorTransformer();
    const string sourceCode = """
      [WolverineHandler]
      public class NotifyOrderHandler {
        public Task Handle(NotifyOrderCommand command) {
          return Task.CompletedTask;
        }
      }

      public record NotifyOrderCommand(string OrderId);
      """;

    var result = await transformer.TransformAsync(sourceCode, "Handler.cs");

    await Assert.That(result.TransformedCode).DoesNotContain("using Whizbang.Core;")
      .Because("nothing imported Wolverine, so nothing should be replaced with a Whizbang import");
    await Assert.That(result.TransformedCode).DoesNotContain("[WolverineHandler]")
      .Because("attribute removal is independent of the using-directive step and still runs");
    await Assert.That(result.Changes.Any(c =>
        c.ChangeType is ChangeType.UsingAdded or ChangeType.UsingRemoved or ChangeType.UsingReplaced))
      .IsFalse()
      .Because("no using-directive change should be recorded when there was no Wolverine using to touch");
  }

  [Test]
  public async Task TransformAsync_HandleNamedMethodOnUnrelatedBaseClass_IsNotTreatedAsAHandlerAsync() {
    // Both the base-type rewriter and the method renamer key off IHandle<T>/IReceptor<T> in the
    // base list, never off the method name alone. A class with a method literally called Handle but
    // some other base class must come through untouched -- renaming it would fabricate a receptor
    // method on a class that never implemented the interface.
    var transformer = new HandlerToReceptorTransformer();
    const string sourceCode = """
      using Wolverine;

      public class CreateOrderHandler : IHandle<CreateOrderCommand> {
        public Task Handle(CreateOrderCommand command) => Task.CompletedTask;
      }

      public class OrderReportBuilder : ReportBuilderBase {
        public void Handle(string data) { }
      }

      public class ReportBuilderBase { }

      public record CreateOrderCommand(string OrderId);
      """;

    var result = await transformer.TransformAsync(sourceCode, "Handlers.cs");

    await Assert.That(result.TransformedCode).Contains("IReceptor<CreateOrderCommand>")
      .Because("the real handler in the same file still converts normally");
    await Assert.That(result.TransformedCode).Contains("class OrderReportBuilder : ReportBuilderBase")
      .Because("an unrelated base type must fall through the base-type rewriter unchanged");
    await Assert.That(result.TransformedCode).Contains("public void Handle(string data)")
      .Because("a Handle-named method outside an IHandle/IReceptor class must not be renamed");
  }

  [Test]
  public async Task TransformAsync_AttributeListWithoutWolverineHandler_IsLeftCompletelyUntouchedAsync() {
    // The attribute remover only special-cases [WolverineHandler]/[WolverineHandlerAttribute]. Any
    // other attribute list in the same file -- here an [Obsolete] on the handler itself -- must
    // come through byte-for-byte, and no AttributeRemoved change should be logged for it.
    var transformer = new HandlerToReceptorTransformer();
    const string sourceCode = """
      using Wolverine;

      [Obsolete("use CreateOrderHandler instead")]
      public class LegacyOrderHandler : IHandle<LegacyOrderCommand> {
        public Task Handle(LegacyOrderCommand command) => Task.CompletedTask;
      }

      public record LegacyOrderCommand(string OrderId);
      """;

    var result = await transformer.TransformAsync(sourceCode, "Handler.cs");

    await Assert.That(result.TransformedCode).Contains("[Obsolete(\"use CreateOrderHandler instead\")]")
      .Because("an unrelated attribute must survive verbatim");
    await Assert.That(result.TransformedCode).Contains("IReceptor<LegacyOrderCommand>")
      .Because("the interface conversion still happens for the class underneath");
    await Assert.That(result.Changes.Any(c => c.ChangeType == ChangeType.AttributeRemoved)).IsFalse()
      .Because("nothing Wolverine-specific was in that attribute list, so nothing should be logged as removed");
  }

  [Test]
  public async Task TransformAsync_WolverineHandlerAttributeSharesListWithAnotherAttribute_RemovesOnlyItAsync() {
    // [WolverineHandler, Obsolete(...)] packs two attributes into one bracketed list. Removing the
    // Wolverine one must rebuild the list around the survivor instead of dropping the whole
    // bracket -- losing an unrelated attribute like [Obsolete] would silence a real deprecation
    // warning for anyone still calling into the migrated type.
    var transformer = new HandlerToReceptorTransformer();
    const string sourceCode = """
      using Wolverine;

      [WolverineHandler, Obsolete("legacy handler shim")]
      public class LegacyNotificationHandler {
        public Task Handle(SendLegacyNotificationCommand command) => Task.CompletedTask;
      }

      public record SendLegacyNotificationCommand(string Message);
      """;

    var result = await transformer.TransformAsync(sourceCode, "Handler.cs");

    await Assert.That(result.TransformedCode).DoesNotContain("WolverineHandler")
      .Because("the Wolverine attribute is still removed");
    await Assert.That(result.TransformedCode).Contains("[Obsolete(\"legacy handler shim\")]")
      .Because("the sibling attribute must survive in its own rebuilt bracket");
    await Assert.That(result.Changes.Any(c => c.ChangeType == ChangeType.AttributeRemoved)).IsTrue()
      .Because("the removal of WolverineHandler from the shared list is still tracked");
  }

  [Test]
  public async Task TransformAsync_NullConditionalInvokeAsyncOnLocalMessage_IsLeftIntactRatherThanManglingItAsync() {
    // The AST rewrite for _bus.InvokeAsync(new LocalMessage<T>(...)) only fires when the call's
    // expression is a plain member access. Written with the null-conditional operator
    // (_bus?.InvokeAsync(...)), the expression is a member-binding node instead, so the rewrite
    // declines and the call comes back unchanged. That is the right call -- but it used to be
    // undone downstream: a post-process step meant for comments text-replaced every
    // "LocalMessage<" in the whole file, so the declined call was emitted as
    // "new LocalInvokeAsync<T>(...)" -- a method name used as a constructed type. Source that
    // does not compile, produced silently, with no warning and no recorded change.
    //
    // Leaving the original intact is what a partial migration should do: the developer's code
    // still builds, and the un-migrated call is visible as itself.
    var transformer = new HandlerToReceptorTransformer();
    const string sourceCode = """
      using Wolverine;

      public class OrderRelay {
        private readonly IMessageBus _bus;

        public OrderRelay(IMessageBus bus) {
          _bus = bus;
        }

        public void Relay(RelayOrderCommand command) {
          _bus?.InvokeAsync(new LocalMessage<ValidateOrderCommand>(
              new ValidateOrderCommand(command.OrderId)));
        }
      }

      public record RelayOrderCommand(Guid OrderId);
      public record ValidateOrderCommand(Guid OrderId);
      """;

    var result = await transformer.TransformAsync(sourceCode, "OrderRelay.cs");

    await Assert.That(result.TransformedCode).Contains("new LocalMessage<ValidateOrderCommand>")
      .Because("a call the rewrite declined must come back exactly as written, not half-renamed into source that will not compile");
    await Assert.That(result.TransformedCode).DoesNotContain("new LocalInvokeAsync<")
      .Because("LocalInvokeAsync is a method; emitting it as a constructed type is the corruption this guards against");
    await Assert.That(result.TransformedCode).Contains("_bus?.InvokeAsync(")
      .Because("the call site was not restructured, so it must be left whole rather than partly rewritten");
    await Assert.That(result.Changes.Any(c => c.ChangeType == ChangeType.MethodCallReplacement)).IsFalse()
      .Because("nothing was replaced here, and a recorded change would misreport the file as migrated");
  }

}
