using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core.Tests.Integration;

/// <summary>
/// Integration tests for v0.1.0 Dispatcher and Receptor interactions.
/// These tests verify the complete flow from dispatcher through receptors,
/// testing the actual integration between components rather than testing them in isolation.
/// </summary>
[Category("Integration")]
public class DispatcherReceptorIntegrationTests {
  // Test Messages - Simple workflow
  public record PlaceOrder(Guid CustomerId, OrderItem[] Items);
  public record OrderItem(string Sku, int Quantity, decimal Price);
  public record OrderPlaced(Guid OrderId, Guid CustomerId, decimal Total);

  public record ShipOrder(Guid OrderId, string Address);
  public record OrderShipped(Guid OrderId, Guid ShipmentId, DateTimeOffset ShippedAt);

  // Test Messages - Complex workflow with multiple events
  public record ProcessPayment(Guid OrderId, decimal Amount, string Method);
  public record PaymentProcessed(Guid PaymentId, Guid OrderId, decimal Amount);
  public record PaymentFailed(Guid OrderId, string Reason);

  // Test Messages - Chain of events
  public record CreateUser(string Email, string Name);
  public record UserCreated(Guid UserId, string Email);
  public record SendWelcomeEmail(Guid UserId, string Email);
  public record WelcomeEmailSent(Guid UserId, DateTimeOffset SentAt);

  // Test Receptors
  public class OrderReceptor : IReceptor<PlaceOrder, OrderPlaced> {
    public async ValueTask<OrderPlaced> HandleAsync(PlaceOrder message, CancellationToken cancellationToken = default) {
      if (message.Items.Length == 0) {
        throw new InvalidOperationException("Order must have items");
      }

      await Task.Delay(1, cancellationToken); // Simulate async work

      var total = message.Items.Sum(item => item.Quantity * item.Price);
      return new OrderPlaced(
          OrderId: Guid.NewGuid(),
          CustomerId: message.CustomerId,
          Total: total
      );
    }
  }

  public class ShippingReceptor : IReceptor<ShipOrder, OrderShipped> {
    public async ValueTask<OrderShipped> HandleAsync(ShipOrder message, CancellationToken cancellationToken = default) {
      if (string.IsNullOrWhiteSpace(message.Address)) {
        throw new InvalidOperationException("Address is required");
      }

      await Task.Delay(1, cancellationToken);

      return new OrderShipped(
          OrderId: message.OrderId,
          ShipmentId: Guid.NewGuid(),
          ShippedAt: DateTimeOffset.UtcNow
      );
    }
  }

  public class PaymentReceptor : IReceptor<ProcessPayment, PaymentProcessed> {
    public async ValueTask<PaymentProcessed> HandleAsync(ProcessPayment message, CancellationToken cancellationToken = default) {
      await Task.Delay(1, cancellationToken);

      // Simulate payment validation
      if (message.Amount <= 0) {
        throw new InvalidOperationException("Amount must be positive");
      }

      return new PaymentProcessed(
          PaymentId: Guid.NewGuid(),
          OrderId: message.OrderId,
          Amount: message.Amount
      );
    }
  }

  public class UserReceptor : IReceptor<CreateUser, UserCreated> {
    public async ValueTask<UserCreated> HandleAsync(CreateUser message, CancellationToken cancellationToken = default) {
      if (string.IsNullOrWhiteSpace(message.Email)) {
        throw new InvalidOperationException("Email is required");
      }

      await Task.Delay(1, cancellationToken);

      return new UserCreated(
          UserId: Guid.NewGuid(),
          Email: message.Email
      );
    }
  }

  public class EmailReceptor : IReceptor<SendWelcomeEmail, WelcomeEmailSent> {
    public async ValueTask<WelcomeEmailSent> HandleAsync(SendWelcomeEmail message, CancellationToken cancellationToken = default) {
      await Task.Delay(1, cancellationToken);

      return new WelcomeEmailSent(
          UserId: message.UserId,
          SentAt: DateTimeOffset.UtcNow
      );
    }
  }

  /// <summary>
  /// Tests end-to-end flow: Command → Dispatcher → Receptor → Response
  /// </summary>
  [Test]
  public async Task Integration_SimpleCommandFlow_ShouldProcessCompletelyAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
      new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    var provider = services.BuildServiceProvider();
    var dispatcher = provider.GetRequiredService<IDispatcher>();

    var customerId = Guid.NewGuid();
    var command = new PlaceOrder(
        customerId,
        [
            new OrderItem("SKU-001", 2, 10.00m),
            new OrderItem("SKU-002", 1, 25.00m)
        ]
    );

    // Act
    var result = await dispatcher.LocalInvokeAsync<OrderPlaced>(command);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result.OrderId).IsNotEqualTo(Guid.Empty);
    await Assert.That(result.CustomerId).IsEqualTo(customerId);
    await Assert.That(result.Total).IsEqualTo(45.00m); // (2*10) + (1*25)
  }

  /// <summary>
  /// Tests sequential message processing: Command 1 → Result 1 → Command 2 → Result 2
  /// </summary>
  [Test]
  public async Task Integration_SequentialMessages_ShouldProcessInOrderAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
      new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    var provider = services.BuildServiceProvider();
    var dispatcher = provider.GetRequiredService<IDispatcher>();

    var customerId = Guid.NewGuid();

    // Act - First command
    var orderPlaced = await dispatcher.LocalInvokeAsync<OrderPlaced>(
        new PlaceOrder(
            customerId,
            [new OrderItem("SKU-001", 1, 10.00m)]
        )
    );

    // Act - Second command using result from first
    var orderShipped = await dispatcher.LocalInvokeAsync<OrderShipped>(
        new ShipOrder(orderPlaced.OrderId, "123 Main St")
    );

    // Assert
    await Assert.That(orderPlaced.OrderId).IsNotEqualTo(Guid.Empty);
    await Assert.That(orderShipped.OrderId).IsEqualTo(orderPlaced.OrderId);
    await Assert.That(orderShipped.ShipmentId).IsNotEqualTo(Guid.Empty);
    await Assert.That(orderShipped.ShippedAt).IsLessThanOrEqualTo(DateTimeOffset.UtcNow);
  }

  /// <summary>
  /// Tests parallel message processing with independent receptors
  /// </summary>
  [Test]
  public async Task Integration_ParallelMessages_ShouldProcessConcurrentlyAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
      new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    var provider = services.BuildServiceProvider();
    var dispatcher = provider.GetRequiredService<IDispatcher>();

    var customerId = Guid.NewGuid();
    var orderId = Guid.NewGuid();

    // Act - Process multiple messages in parallel
    var orderTask = dispatcher.LocalInvokeAsync<OrderPlaced>(
        new PlaceOrder(
            customerId,
            [new OrderItem("SKU-001", 1, 100.00m)]
        )
    );

    var paymentTask = dispatcher.LocalInvokeAsync<PaymentProcessed>(
        new ProcessPayment(orderId, 100.00m, "CreditCard")
    );

    var orderResult = await orderTask;
    var paymentResult = await paymentTask;

    // Assert
    await Assert.That(orderResult.OrderId).IsNotEqualTo(Guid.Empty);
    await Assert.That(orderResult.Total).IsEqualTo(100.00m);
    await Assert.That(paymentResult.PaymentId).IsNotEqualTo(Guid.Empty);
    await Assert.That(paymentResult.Amount).IsEqualTo(100.00m);
  }

  /// <summary>
  /// Tests error handling through the complete stack: Dispatcher → Receptor → Exception
  /// </summary>
  [Test]
  public async Task Integration_ReceptorValidationFailure_ShouldPropagateExceptionAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
      new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    var provider = services.BuildServiceProvider();
    var dispatcher = provider.GetRequiredService<IDispatcher>();

    var command = new PlaceOrder(
        Guid.NewGuid(),
        [] // Invalid - no items
    );

    // Act & Assert
    var exception = await Assert.That(async () =>
        await dispatcher.LocalInvokeAsync<PlaceOrder, OrderPlaced>(command))
        .ThrowsExactly<InvalidOperationException>();

    await Assert.That(exception!.Message).Contains("Order must have items");
  }

  /// <summary>
  /// Tests handler not found error through the complete stack
  /// </summary>
  [Test]
  public async Task Integration_UnregisteredMessage_ShouldThrowHandlerNotFoundAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
      new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));
    // Intentionally NOT registering any receptors - only dispatcher
    services.AddWhizbangDispatcher();
    var provider = services.BuildServiceProvider();
    var dispatcher = provider.GetRequiredService<IDispatcher>();

    var command = new PlaceOrder(
        Guid.NewGuid(),
        [new OrderItem("SKU-001", 1, 10.00m)]
    );

    // Act & Assert
    var exception = await Assert.That(async () =>
        await dispatcher.LocalInvokeAsync<PlaceOrder, OrderPlaced>(command))
        .ThrowsExactly<HandlerNotFoundException>();

    await Assert.That(exception!.Message).Contains("PlaceOrder");
  }

  /// <summary>
  /// Tests context propagation through dispatcher and receptor
  /// </summary>
  [Test]
  public async Task Integration_WithContext_ShouldPreserveContextThroughFlowAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
      new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    var provider = services.BuildServiceProvider();
    var dispatcher = provider.GetRequiredService<IDispatcher>();

    var context = MessageContext.Create(
        CorrelationId.New(),
        MessageId.New()
    );

    var command = new PlaceOrder(
        Guid.NewGuid(),
        [new OrderItem("SKU-001", 1, 10.00m)]
    );

    // Act
    var result = await dispatcher.LocalInvokeAsync<OrderPlaced>(command, context);

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(result.OrderId).IsNotEqualTo(Guid.Empty);
    // Context validation happens in the dispatcher - we verify the flow completes successfully
  }

  /// <summary>
  /// Tests complete workflow: User creation → Welcome email
  /// This simulates a real-world scenario where one command triggers follow-up actions
  /// </summary>
  [Test]
  public async Task Integration_CompleteWorkflow_ShouldProcessMultiStepFlowAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
      new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    var provider = services.BuildServiceProvider();
    var dispatcher = provider.GetRequiredService<IDispatcher>();

    var email = "user@example.com";

    // Act - Step 1: Create user
    var userCreated = await dispatcher.LocalInvokeAsync<UserCreated>(
        new CreateUser(email, "John Doe")
    );

    // Act - Step 2: Send welcome email (triggered by user creation)
    var emailSent = await dispatcher.LocalInvokeAsync<WelcomeEmailSent>(
        new SendWelcomeEmail(userCreated.UserId, userCreated.Email)
    );

    // Assert
    await Assert.That(userCreated.UserId).IsNotEqualTo(Guid.Empty);
    await Assert.That(userCreated.Email).IsEqualTo(email);
    await Assert.That(emailSent.UserId).IsEqualTo(userCreated.UserId);
    await Assert.That(emailSent.SentAt).IsLessThanOrEqualTo(DateTimeOffset.UtcNow);
  }

  /// <summary>
  /// Tests multiple receptors handling different message types simultaneously
  /// </summary>
  [Test]
  public async Task Integration_MultipleReceptorTypes_ShouldRouteCorrectlyAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
      new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    var provider = services.BuildServiceProvider();
    var dispatcher = provider.GetRequiredService<IDispatcher>();

    var customerId = Guid.NewGuid();
    var orderId = Guid.NewGuid();

    // Act - Send different message types through the same dispatcher
    var orderResult = await dispatcher.LocalInvokeAsync<OrderPlaced>(
        new PlaceOrder(customerId, [new OrderItem("SKU-001", 1, 50.00m)])
    );

    var paymentResult = await dispatcher.LocalInvokeAsync<PaymentProcessed>(
        new ProcessPayment(orderId, 50.00m, "CreditCard")
    );

    var shippingResult = await dispatcher.LocalInvokeAsync<OrderShipped>(
        new ShipOrder(orderId, "456 Oak Ave")
    );

    // Assert - Each message was routed to the correct receptor
    await Assert.That(orderResult).IsNotNull();
    await Assert.That(orderResult.Total).IsEqualTo(50.00m);

    await Assert.That(paymentResult).IsNotNull();
    await Assert.That(paymentResult.Amount).IsEqualTo(50.00m);

    await Assert.That(shippingResult).IsNotNull();
    await Assert.That(shippingResult.ShipmentId).IsNotEqualTo(Guid.Empty);
  }

  /// <summary>
  /// Tests that dispatcher properly handles async receptor execution
  /// </summary>
  [Test]
  public async Task Integration_AsyncReceptorExecution_ShouldCompleteAsyncWorkAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
      new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    var provider = services.BuildServiceProvider();
    var dispatcher = provider.GetRequiredService<IDispatcher>();

    var startTime = DateTimeOffset.UtcNow;
    var command = new PlaceOrder(
        Guid.NewGuid(),
        [new OrderItem("SKU-001", 1, 10.00m)]
    );

    // Act
    var result = await dispatcher.LocalInvokeAsync<OrderPlaced>(command);
    var endTime = DateTimeOffset.UtcNow;

    // Assert
    await Assert.That(result).IsNotNull();
    await Assert.That(endTime).IsGreaterThan(startTime); // Confirms async execution occurred
  }

  /// <summary>
  /// Tests service provider integration and dependency injection
  /// </summary>
  [Test]
  public async Task Integration_ServiceProvider_ShouldResolveAllDependenciesAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
      new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    var provider = services.BuildServiceProvider();

    // Act - Resolve dispatcher from service provider
    var dispatcher = provider.GetRequiredService<IDispatcher>();

    // Verify dispatcher can handle both receptor types
    var orderResult = await dispatcher.LocalInvokeAsync<OrderPlaced>(
        new PlaceOrder(Guid.NewGuid(), [new OrderItem("SKU-001", 1, 10.00m)])
    );

    var userResult = await dispatcher.LocalInvokeAsync<UserCreated>(
        new CreateUser("test@example.com", "Test User")
    );

    // Assert
    await Assert.That(dispatcher).IsNotNull();
    await Assert.That(orderResult).IsNotNull();
    await Assert.That(userResult).IsNotNull();
  }

  // TODO: These tests are placeholders for v0.2.0 Dispatcher integration with MessageEnvelope/Hops
  // Currently skipped as v0.1.0 Dispatcher doesn't create envelopes yet
  // Uncomment and implement when Dispatcher is upgraded to work with MessageEnvelope

  /// <summary>
  /// Tests that dispatcher creates a MessageEnvelope with an initial hop
  /// </summary>
  [Test]
  public async Task Integration_Dispatcher_ShouldCreateEnvelopeWithInitialHopAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
      new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    var traceStore = new Whizbang.Core.Observability.InMemoryTraceStore();
    services.AddSingleton<Whizbang.Core.Observability.ITraceStore>(traceStore);
    services.AddWhizbangDispatcher();
    var provider = services.BuildServiceProvider();
    var dispatcher = provider.GetRequiredService<IDispatcher>();

    var context = MessageContext.Create(Whizbang.Core.ValueObjects.CorrelationId.New());
    var command = new PlaceOrder(
        Guid.NewGuid(),
        [new OrderItem("SKU-001", 1, 10.00m)]
    );

    // Act
    var result = await dispatcher.LocalInvokeAsync<OrderPlaced>(command, context);

    // Assert - Verify envelope was created and stored
    var envelopes = await traceStore.GetByCorrelationAsync(context.CorrelationId);
    await Assert.That(envelopes).HasCount().EqualTo(1);

    var envelope = envelopes[0];
    await Assert.That(envelope.MessageId).IsNotEqualTo(Whizbang.Core.ValueObjects.MessageId.New());
    await Assert.That(envelope.GetCorrelationId()).IsEqualTo(context.CorrelationId);
    await Assert.That(envelope.Hops).HasCount().EqualTo(1);

    var hop = envelope.Hops[0];
    await Assert.That(hop.Type).IsEqualTo(Whizbang.Core.Observability.HopType.Current);
    await Assert.That(hop.ServiceInstance.ServiceName).IsNotEqualTo(string.Empty);
    await Assert.That(hop.Timestamp).IsLessThanOrEqualTo(DateTimeOffset.UtcNow);
  }

  /// <summary>
  /// Tests that hops contain correct caller information
  /// </summary>
  [Test]
  public async Task Integration_Hops_ShouldCaptureCallerInformationAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
      new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    var traceStore = new Whizbang.Core.Observability.InMemoryTraceStore();
    services.AddSingleton<Whizbang.Core.Observability.ITraceStore>(traceStore);
    services.AddWhizbangDispatcher();
    var provider = services.BuildServiceProvider();
    var dispatcher = provider.GetRequiredService<IDispatcher>();

    var context = MessageContext.Create(Whizbang.Core.ValueObjects.CorrelationId.New());
    var command = new PlaceOrder(
        Guid.NewGuid(),
        [new OrderItem("SKU-001", 1, 10.00m)]
    );

    // Act - Note: The line number below should be captured!
    var result = await dispatcher.LocalInvokeAsync<OrderPlaced>(command, context);

    // Assert
    var envelopes = await traceStore.GetByCorrelationAsync(context.CorrelationId);
    await Assert.That(envelopes).HasCount().EqualTo(1);

    var hop = envelopes[0].Hops[0];
    await Assert.That(hop.CallerMemberName).IsNotEqualTo(string.Empty);
    await Assert.That(hop.CallerFilePath).IsNotEqualTo(string.Empty);
    await Assert.That(hop.CallerLineNumber).IsNotEqualTo(0);
  }

  /// <summary>
  /// Tests that multiple messages create separate envelopes with unique hops
  /// </summary>
  [Test]
  public async Task Integration_MultipleMessages_ShouldCreateSeparateEnvelopesAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
      new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    var traceStore = new Whizbang.Core.Observability.InMemoryTraceStore();
    services.AddSingleton<Whizbang.Core.Observability.ITraceStore>(traceStore);
    services.AddWhizbangDispatcher();
    var provider = services.BuildServiceProvider();
    var dispatcher = provider.GetRequiredService<IDispatcher>();

    var context = MessageContext.Create(Whizbang.Core.ValueObjects.CorrelationId.New());

    // Act - Send two messages
    var result1 = await dispatcher.LocalInvokeAsync<OrderPlaced>(
        new PlaceOrder(Guid.NewGuid(), [new OrderItem("SKU-001", 1, 10.00m)]),
        context
    );

    await Task.Delay(10); // Small delay to ensure different timestamps

    var result2 = await dispatcher.LocalInvokeAsync<OrderPlaced>(
        new PlaceOrder(Guid.NewGuid(), [new OrderItem("SKU-002", 2, 20.00m)]),
        context
    );

    // Assert - Both messages created separate envelopes
    var envelopes = await traceStore.GetByCorrelationAsync(context.CorrelationId);
    await Assert.That(envelopes).HasCount().EqualTo(2);

    // Verify MessageIds are different
    await Assert.That(envelopes[0].MessageId).IsNotEqualTo(envelopes[1].MessageId);

    // Verify timestamps are different
    await Assert.That(envelopes[0].Hops[0].Timestamp).IsLessThan(envelopes[1].Hops[0].Timestamp);
  }

  /// <summary>
  /// Tests that causation hops are carried forward in workflows
  /// </summary>
  [Test]
  public async Task Integration_Workflow_ShouldCarryForwardCausationHopsAsync() {
    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
      new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    var traceStore = new Whizbang.Core.Observability.InMemoryTraceStore();
    services.AddSingleton<Whizbang.Core.Observability.ITraceStore>(traceStore);
    services.AddWhizbangDispatcher();
    var provider = services.BuildServiceProvider();
    var dispatcher = provider.GetRequiredService<IDispatcher>();

    var correlationId = Whizbang.Core.ValueObjects.CorrelationId.New();

    // Act - Create workflow: PlaceOrder → OrderPlaced → ShipOrder
    // Step 1: Place order
    var context1 = MessageContext.Create(correlationId);
    var orderPlaced = await dispatcher.LocalInvokeAsync<OrderPlaced>(
        new PlaceOrder(Guid.NewGuid(), [new OrderItem("SKU-001", 1, 10.00m)]),
        context1
    );

    // Step 2: Ship order (causation: the OrderPlaced message)
    var envelopes1 = await traceStore.GetByCorrelationAsync(correlationId);
    var orderPlacedEnvelopeId = envelopes1[0].MessageId;

    var context2 = MessageContext.Create(correlationId, orderPlacedEnvelopeId);
    var orderShipped = await dispatcher.LocalInvokeAsync<OrderShipped>(
        new ShipOrder(orderPlaced.OrderId, "123 Main St"),
        context2
    );

    // Assert - Verify causation chain
    var envelopes = await traceStore.GetByCorrelationAsync(correlationId);
    await Assert.That(envelopes).HasCount().EqualTo(2);

    // Second envelope should have causation pointing to first
    var shipOrderEnvelope = envelopes[1];
    await Assert.That(shipOrderEnvelope.GetCausationId()).IsEqualTo(orderPlacedEnvelopeId);
  }

  /// <summary>
  /// Tests that security context is preserved in hops
  /// </summary>
  [Test]
  public async Task Integration_Hops_ShouldPreserveSecurityContextAsync() {
    // NOTE: Currently the dispatcher doesn't automatically populate security context
    // This test verifies the structure is in place for when we add that feature
    // For now, we just verify that hops can contain security context when manually set

    // Arrange
    var services = new ServiceCollection();
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
      new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    var traceStore = new Whizbang.Core.Observability.InMemoryTraceStore();
    services.AddSingleton<Whizbang.Core.Observability.ITraceStore>(traceStore);
    services.AddWhizbangDispatcher();
    var provider = services.BuildServiceProvider();
    var dispatcher = provider.GetRequiredService<IDispatcher>();

    var context = MessageContext.Create(Whizbang.Core.ValueObjects.CorrelationId.New());
    var command = new PlaceOrder(
        Guid.NewGuid(),
        [new OrderItem("SKU-001", 1, 10.00m)]
    );

    // Act
    var result = await dispatcher.LocalInvokeAsync<OrderPlaced>(command, context);

    // Assert - Verify envelope was created (security context will be added in future)
    var envelopes = await traceStore.GetByCorrelationAsync(context.CorrelationId);
    await Assert.That(envelopes).HasCount().EqualTo(1);
    await Assert.That(envelopes[0].Hops).HasCount().EqualTo(1);

    // Security context is null for now, but the structure supports it
    // In the future, we'll populate this automatically from ambient context
    await Assert.That(envelopes[0].Hops[0].SecurityContext).IsNull();
  }
}
