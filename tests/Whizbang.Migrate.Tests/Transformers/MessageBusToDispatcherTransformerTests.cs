using Whizbang.Migrate.Transformers;

namespace Whizbang.Migrate.Tests.Transformers;

/// <summary>
/// Tests for the MessageBus transformer that converts Wolverine IMessageBus patterns to Whizbang IDispatcher.
/// </summary>
/// <tests>Whizbang.Migrate/Transformers/MessageBusToDispatcherTransformer.cs:*</tests>
public class MessageBusToDispatcherTransformerTests {
  [Test]
  public async Task TransformAsync_IMessageBusField_TransformsToIDispatcherAsync() {
    // Arrange
    var transformer = new MessageBusToDispatcherTransformer();
    const string sourceCode = """
      using Wolverine;

      public class OrderService {
        private readonly IMessageBus _messageBus;

        public OrderService(IMessageBus messageBus) {
          _messageBus = messageBus;
        }
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IDispatcher");
    await Assert.That(result.TransformedCode).DoesNotContain("IMessageBus");
    await Assert.That(result.Changes.Any(c => c.ChangeType == ChangeType.InterfaceReplacement)).IsTrue();
  }

  [Test]
  public async Task TransformAsync_IMessageBusParameter_TransformsToIDispatcherAsync() {
    // Arrange
    var transformer = new MessageBusToDispatcherTransformer();
    const string sourceCode = """
      using Wolverine;

      public class OrderService {
        public async Task ProcessAsync(IMessageBus bus) {
          await bus.PublishAsync(new OrderCreated());
        }
      }

      public record OrderCreated();
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("IDispatcher");
    await Assert.That(result.TransformedCode).DoesNotContain("IMessageBus");
  }

  [Test]
  public async Task TransformAsync_SendAsyncCall_TransformsCorrectlyAsync() {
    // Arrange
    var transformer = new MessageBusToDispatcherTransformer();
    const string sourceCode = """
      using Wolverine;

      public class OrderService {
        private readonly IMessageBus _messageBus;

        public async Task SendCommandAsync() {
          await _messageBus.SendAsync(new ProcessPayment());
        }
      }

      public record ProcessPayment();
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("_dispatcher.SendAsync");
    await Assert.That(result.TransformedCode).DoesNotContain("_messageBus.SendAsync");
  }

  [Test]
  public async Task TransformAsync_PublishAsyncCall_TransformsCorrectlyAsync() {
    // Arrange
    var transformer = new MessageBusToDispatcherTransformer();
    const string sourceCode = """
      using Wolverine;

      public class OrderService {
        private readonly IMessageBus _messageBus;

        public async Task PublishEventAsync() {
          await _messageBus.PublishAsync(new OrderCreated());
        }
      }

      public record OrderCreated();
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("_dispatcher.PublishAsync");
    await Assert.That(result.TransformedCode).DoesNotContain("_messageBus.PublishAsync");
  }

  [Test]
  public async Task TransformAsync_InvokeAsync_TransformsToSendAsyncAsync() {
    // Arrange
    var transformer = new MessageBusToDispatcherTransformer();
    const string sourceCode = """
      using Wolverine;

      public class OrderService {
        private readonly IMessageBus _messageBus;

        public async Task<OrderCreated> InvokeCommandAsync() {
          return await _messageBus.InvokeAsync<OrderCreated>(new CreateOrder());
        }
      }

      public record CreateOrder();
      public record OrderCreated();
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    // InvokeAsync becomes LocalInvokeAsync for in-process RPC
    await Assert.That(result.TransformedCode).Contains("LocalInvokeAsync");
    await Assert.That(result.TransformedCode).DoesNotContain("_messageBus.InvokeAsync");
    await Assert.That(result.Warnings.Any(w => w.Contains("InvokeAsync"))).IsTrue();
  }

  [Test]
  public async Task TransformAsync_UsingWolverine_TransformsToWhizbangCoreAsync() {
    // Arrange
    var transformer = new MessageBusToDispatcherTransformer();
    const string sourceCode = """
      using Wolverine;

      public class OrderService {
        private readonly IMessageBus _messageBus;
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("using Whizbang.Core;");
    await Assert.That(result.TransformedCode).DoesNotContain("using Wolverine;");
    await Assert.That(result.Changes.Any(c => c.ChangeType == ChangeType.UsingRemoved)).IsTrue();
  }

  [Test]
  public async Task TransformAsync_NoMessageBus_ReturnsUnchangedAsync() {
    // Arrange
    var transformer = new MessageBusToDispatcherTransformer();
    const string sourceCode = """
      public class OrderService {
        public void Process() { }
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
  public async Task TransformAsync_PublishAsyncEvent_EmitsWarningAboutPatternAsync() {
    // Arrange
    var transformer = new MessageBusToDispatcherTransformer();
    const string sourceCode = """
      using Wolverine;

      public class CreateOrderReceptor {
        private readonly IMessageBus _messageBus;

        public async Task HandleAsync() {
          var @event = new OrderCreated();
          await _messageBus.PublishAsync(@event);
        }
      }

      public record OrderCreated();
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "CreateOrderReceptor.cs");

    // Assert
    // Should warn about considering whether to use PublishAsync vs returning the event
    await Assert.That(result.Warnings.Any(w =>
        w.Contains("PublishAsync") &&
        (w.Contains("receptor") || w.Contains("consider") || w.Contains("event")))).IsTrue();
  }

  [Test]
  public async Task TransformAsync_RenamesFieldToDispatcherAsync() {
    // Arrange
    var transformer = new MessageBusToDispatcherTransformer();
    const string sourceCode = """
      using Wolverine;

      public class OrderService {
        private readonly IMessageBus _messageBus;

        public OrderService(IMessageBus messageBus) {
          _messageBus = messageBus;
        }

        public async Task SendAsync() {
          await _messageBus.SendAsync(new ProcessPayment());
        }
      }

      public record ProcessPayment();
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("_dispatcher");
    await Assert.That(result.TransformedCode).DoesNotContain("_messageBus");
    await Assert.That(result.TransformedCode).Contains("IDispatcher dispatcher");
  }

  [Test]
  public async Task TransformAsync_PreservesOtherCodeAsync() {
    // Arrange
    var transformer = new MessageBusToDispatcherTransformer();
    const string sourceCode = """
      using Wolverine;
      using Microsoft.Extensions.Logging;

      public class OrderService {
        private readonly IMessageBus _messageBus;
        private readonly ILogger<OrderService> _logger;

        public OrderService(IMessageBus messageBus, ILogger<OrderService> logger) {
          _messageBus = messageBus;
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
    await Assert.That(result.TransformedCode).Contains("_logger");
  }

  [Test]
  public async Task TransformAsync_PreservesNamespaceAsync() {
    // Arrange
    var transformer = new MessageBusToDispatcherTransformer();
    const string sourceCode = """
      using Wolverine;

      namespace MyApp.Services;

      public class OrderService {
        private readonly IMessageBus _messageBus;
      }
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.TransformedCode).Contains("namespace MyApp.Services;");
  }

  [Test]
  public async Task TransformAsync_TracksAllChangesAsync() {
    // Arrange
    var transformer = new MessageBusToDispatcherTransformer();
    const string sourceCode = """
      using Wolverine;

      public class OrderService {
        private readonly IMessageBus _messageBus;

        public OrderService(IMessageBus messageBus) {
          _messageBus = messageBus;
        }

        public async Task SendAsync() {
          await _messageBus.SendAsync(new ProcessPayment());
        }
      }

      public record ProcessPayment();
      """;

    // Act
    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    // Assert
    await Assert.That(result.Changes.Count).IsGreaterThan(0);
    // Should have using change, interface replacement, and field/parameter rename
    await Assert.That(result.Changes.Any(c => c.ChangeType == ChangeType.UsingRemoved)).IsTrue();
    await Assert.That(result.Changes.Any(c => c.ChangeType == ChangeType.InterfaceReplacement)).IsTrue();
  }

  [Test]
  public async Task TransformAsync_WhizbangCoreAlreadyImported_DropsTheWolverineUsingAsync() {
    // Several transformers rewrite a Wolverine or Marten using into Whizbang.Core, and they run
    // in sequence over the same file. Whichever runs second finds Whizbang.Core already present;
    // emitting it again is CS0105, which fails any migrated project built warnings-as-errors.
    var transformer = new MessageBusToDispatcherTransformer();
    const string sourceCode = """
      using Whizbang.Core;
      using Wolverine;

      public class OrderService {
        private readonly IMessageBus _messageBus;

        public OrderService(IMessageBus messageBus) {
          _messageBus = messageBus;
        }
      }
      """;

    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    var occurrences = result.TransformedCode.Split("using Whizbang.Core;").Length - 1;
    await Assert.That(occurrences).IsEqualTo(1)
      .Because("a second identical using is CS0105, not a harmless duplicate");
    await Assert.That(result.TransformedCode).DoesNotContain("using Wolverine;");
    await Assert.That(result.TransformedCode).Contains("IDispatcher")
      .Because("deduping the using must not stop the transform itself");
  }


  [Test]
  public async Task TransformAsync_MessageBusWithoutAFileLevelWolverineUsing_AddsNoImportAsync() {
    // IMessageBus can reach a file without `using Wolverine;` on it — through a global using, or
    // an ImplicitUsings entry. The type still has to be rewritten, but the using rewrite keys off
    // a directive that is not there, and inventing one would put an import into a file whose
    // imports were already correct. Reported as a change, it would also overstate what the
    // migration touched.
    //
    // Written with IMessageBus present on purpose: a file without it returns from TransformAsync
    // before the using logic runs at all, so a version of this test using an unrelated class
    // passes without ever reaching the branch it claims to cover.
    var transformer = new MessageBusToDispatcherTransformer();
    const string sourceCode = """
      public class OrderService {
        private readonly IMessageBus _messageBus;
      }
      """;

    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    await Assert.That(result.TransformedCode).Contains("IDispatcher")
      .Because("the type is what the migration is for, and where the name came from does not "
             + "change that it has to be replaced");
    await Assert.That(result.TransformedCode).DoesNotContain("using Whizbang.Core;")
      .Because("this file never imported Wolverine, so there is no import to swap — adding one "
             + "edits imports that were already right");
    await Assert.That(result.Changes.Any(c => c.ChangeType == ChangeType.UsingAdded)).IsFalse()
      .Because("reporting a using change that was not made overstates the migration's footprint");
  }

  [Test]
  public async Task TransformAsync_FieldWithoutUnderscore_KeepsTheCodebasesConventionAsync() {
    // Field naming is a house style, and the migration is not the place to change it. Renaming a
    // plain `messageBus` to `_dispatcher` would leave one underscore-prefixed field in a class
    // where nothing else is, which is the kind of edit a reviewer has to stop and think about.
    var transformer = new MessageBusToDispatcherTransformer();
    const string sourceCode = """
      using Wolverine;

      public class OrderService {
        private readonly IMessageBus messageBus;

        public OrderService(IMessageBus bus) {
          messageBus = bus;
        }
      }
      """;

    var result = await transformer.TransformAsync(sourceCode, "OrderService.cs");

    await Assert.That(result.TransformedCode).Contains("dispatcher")
      .Because("the field is renamed along with its type, or the class reads as though it still "
             + "holds a message bus");
    await Assert.That(result.TransformedCode).DoesNotContain("_dispatcher")
      .Because("the original field carried no underscore, and the migration should not introduce "
             + "a naming convention the file did not already follow");
  }

  [Test]
  public async Task TransformAsync_IMessageBusInsideAGenericArgument_IsStillReplacedAsync() {
    // The interface is being removed, so every mention has to go — including the ones nested in
    // type arguments. Missing one leaves a file referring to a type that no longer exists, and
    // the migration reports success while the build breaks.
    var transformer = new MessageBusToDispatcherTransformer();
    const string sourceCode = """
      using Wolverine;
      using System.Collections.Generic;

      public class FanOut {
        private readonly List<IMessageBus> _buses = new();
      }
      """;

    var result = await transformer.TransformAsync(sourceCode, "FanOut.cs");

    await Assert.That(result.TransformedCode).Contains("List<IDispatcher>")
      .Because("a type argument is a use of the type like any other; leaving it behind produces "
             + "a file that does not compile against the framework it was migrated to");
    await Assert.That(result.TransformedCode).DoesNotContain("IMessageBus")
      .Because("the whole point of the pass is that the old interface is gone afterwards");
  }

  [Test]
  public async Task TransformAsync_PublishAsyncInsideAReceptor_WarnsAboutTheReturnTupleAsync() {
    // In a receptor the framework publishes what the handler returns, so an explicit PublishAsync
    // still compiles and still works — it just bypasses the mechanism the rest of the pipeline is
    // built on. That is exactly the kind of thing a migration must point at, because nothing else
    // will: the code runs, and the author has no reason to look again.
    var transformer = new MessageBusToDispatcherTransformer();
    const string sourceCode = """
      using Wolverine;

      public class OrderReceptor {
        private readonly IMessageBus _messageBus;

        public async Task HandleAsync(OrderPlaced message) {
          await _messageBus.PublishAsync(new OrderConfirmed());
        }
      }
      """;

    var result = await transformer.TransformAsync(sourceCode, "OrderReceptor.cs");

    await Assert.That(result.Warnings.Any(w => w.Contains("PublishAsync"))).IsTrue()
      .Because("the call survives the migration unchanged and keeps working, so a warning is the "
             + "only thing that tells the author the framework would have published it for them");
  }
}
