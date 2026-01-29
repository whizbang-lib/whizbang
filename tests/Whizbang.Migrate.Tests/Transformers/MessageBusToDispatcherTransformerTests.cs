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
    var sourceCode = """
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
    var sourceCode = """
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
    var sourceCode = """
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
    var sourceCode = """
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
    var sourceCode = """
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
    var sourceCode = """
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
    var sourceCode = """
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
    var sourceCode = """
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
    var sourceCode = """
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
    var sourceCode = """
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
    var sourceCode = """
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
    var sourceCode = """
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
}
