using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Tests.Generated;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Tests for RPC-style LocalInvokeAsync where the caller requests a specific type
/// from a receptor that returns a tuple or complex result.
/// The requested type is extracted and returned to the caller,
/// while other values in the result cascade through normal routing.
/// </summary>
/// <tests>Whizbang.Core/Internal/ResponseExtractor.cs</tests>
/// <tests>Whizbang.Core/Dispatcher.cs:_localInvokeWithRpcExtractionAsync</tests>
public class DispatcherRpcExtractionTests {
  // ========================================
  // TEST MESSAGES AND TYPES
  // ========================================

  public record CreateOrder(Guid OrderId, decimal Amount);

  // RPC response - this is what the caller wants back
  public record OrderConfirmation {
    public required Guid OrderId { get; init; }
    public required string ConfirmationCode { get; init; }
  }

  // Side effect events that should cascade (not return to RPC caller)
  [DefaultRouting(DispatchMode.Local)] // Local for test verification
  public record InventoryReserved([property: StreamKey] Guid OrderId, int Quantity) : IEvent;

  [DefaultRouting(DispatchMode.Local)]
  public record PaymentInitiated([property: StreamKey] Guid OrderId, decimal Amount) : IEvent;


  // ========================================
  // EVENT TRACKING INFRASTRUCTURE
  // ========================================

  public static class CascadedEventTracker {
    private static readonly List<IEvent> _cascadedEvents = [];
    private static readonly object _lock = new();

    public static void Reset() {
      lock (_lock) {
        _cascadedEvents.Clear();
      }
    }

    public static void Track(IEvent evt) {
      lock (_lock) {
        _cascadedEvents.Add(evt);
      }
    }

    public static IReadOnlyList<IEvent> GetCascadedEvents() {
      lock (_lock) {
        return _cascadedEvents.ToList();
      }
    }

    public static int Count {
      get {
        lock (_lock) {
          return _cascadedEvents.Count;
        }
      }
    }
  }

  // ========================================
  // TEST RECEPTORS - RETURN TUPLES/COMPLEX RESULTS
  // ========================================

  /// <summary>
  /// Receptor that returns a tuple: (RpcResponse, SideEffectEvent).
  /// When caller does LocalInvokeAsync&lt;OrderConfirmation&gt;, they should get
  /// OrderConfirmation, and InventoryReserved should cascade.
  /// </summary>
  public class TupleReturningReceptor : IReceptor<CreateOrder, (OrderConfirmation, InventoryReserved)> {
    public ValueTask<(OrderConfirmation, InventoryReserved)> HandleAsync(
        CreateOrder message,
        CancellationToken cancellationToken = default) {
      var confirmation = new OrderConfirmation {
        OrderId = message.OrderId,
        ConfirmationCode = $"CONF-{message.OrderId:N}"
      };
      var inventory = new InventoryReserved(message.OrderId, 1);
      return ValueTask.FromResult((confirmation, inventory));
    }
  }

  /// <summary>
  /// Receptor that returns a 3-tuple: (RpcResponse, Event1, Event2).
  /// </summary>
  public record CreateOrderWithPayment(Guid OrderId, decimal Amount);

  public class MultiEventReceptor : IReceptor<CreateOrderWithPayment, (OrderConfirmation, InventoryReserved, PaymentInitiated)> {
    public ValueTask<(OrderConfirmation, InventoryReserved, PaymentInitiated)> HandleAsync(
        CreateOrderWithPayment message,
        CancellationToken cancellationToken = default) {
      var confirmation = new OrderConfirmation {
        OrderId = message.OrderId,
        ConfirmationCode = $"CONF-{message.OrderId:N}"
      };
      var inventory = new InventoryReserved(message.OrderId, 1);
      var payment = new PaymentInitiated(message.OrderId, message.Amount);
      return ValueTask.FromResult((confirmation, inventory, payment));
    }
  }

  // Note: Routed<T> in tuple return types currently has a generator limitation.
  // The ResponseExtractor unit tests cover Route.Local/Route.Outbox unwrapping.
  // These dispatcher tests focus on the RPC extraction flow with non-wrapped types.

  /// <summary>
  /// Receptor that returns just OrderConfirmation (no tuple).
  /// Should work with exact match fast path.
  /// </summary>
  public record SimpleCreateOrder(Guid OrderId);

  public class SimpleReceptor : IReceptor<SimpleCreateOrder, OrderConfirmation> {
    public ValueTask<OrderConfirmation> HandleAsync(
        SimpleCreateOrder message,
        CancellationToken cancellationToken = default) {
      var confirmation = new OrderConfirmation {
        OrderId = message.OrderId,
        ConfirmationCode = $"SIMPLE-{message.OrderId:N}"
      };
      return ValueTask.FromResult(confirmation);
    }
  }

  // ========================================
  // EVENT TRACKING RECEPTORS
  // ========================================

  public class InventoryReservedTracker : IReceptor<InventoryReserved> {
    public ValueTask HandleAsync(InventoryReserved message, CancellationToken cancellationToken = default) {
      CascadedEventTracker.Track(message);
      return ValueTask.CompletedTask;
    }
  }

  public class PaymentInitiatedTracker : IReceptor<PaymentInitiated> {
    public ValueTask HandleAsync(PaymentInitiated message, CancellationToken cancellationToken = default) {
      CascadedEventTracker.Track(message);
      return ValueTask.CompletedTask;
    }
  }


  // ========================================
  // TESTS - RPC EXTRACTION SCENARIOS
  // ========================================

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_TupleReturn_ExtractsRequestedTypeAsync() {
    // Arrange
    CascadedEventTracker.Reset();
    var dispatcher = _createDispatcher();
    var orderId = Guid.NewGuid();
    var command = new CreateOrder(orderId, 100m);

    // Act - Request only OrderConfirmation from receptor that returns (OrderConfirmation, InventoryReserved)
    var confirmation = await dispatcher.LocalInvokeAsync<OrderConfirmation>(command);

    // Assert - Should receive the OrderConfirmation extracted from the tuple
    await Assert.That(confirmation).IsNotNull();
    await Assert.That(confirmation.OrderId).IsEqualTo(orderId);
    await Assert.That(confirmation.ConfirmationCode).IsEqualTo($"CONF-{orderId:N}");
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_TupleReturn_CascadesRemainingEventsAsync() {
    // Arrange
    CascadedEventTracker.Reset();
    var dispatcher = _createDispatcher();
    var orderId = Guid.NewGuid();
    var command = new CreateOrder(orderId, 100m);

    // Act - Request OrderConfirmation; InventoryReserved should cascade
    _ = await dispatcher.LocalInvokeAsync<OrderConfirmation>(command);

    // Assert - InventoryReserved should have cascaded (not returned to caller)
    await Assert.That(CascadedEventTracker.Count).IsEqualTo(1)
      .Because("InventoryReserved should cascade since it wasn't the RPC response type");

    var cascadedEvents = CascadedEventTracker.GetCascadedEvents();
    var cascaded = cascadedEvents[0];
    await Assert.That(cascaded).IsTypeOf<InventoryReserved>();
    await Assert.That(((InventoryReserved)cascaded).OrderId).IsEqualTo(orderId);
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_MultiEventTuple_CascadesAllNonResponseEventsAsync() {
    // Arrange
    CascadedEventTracker.Reset();
    var dispatcher = _createDispatcher();
    var orderId = Guid.NewGuid();
    var command = new CreateOrderWithPayment(orderId, 250m);

    // Act - Request OrderConfirmation; both InventoryReserved and PaymentInitiated should cascade
    var confirmation = await dispatcher.LocalInvokeAsync<OrderConfirmation>(command);

    // Assert - Confirmation returned correctly
    await Assert.That(confirmation).IsNotNull();
    await Assert.That(confirmation.OrderId).IsEqualTo(orderId);

    // Assert - Both events cascaded
    await Assert.That(CascadedEventTracker.Count).IsEqualTo(2)
      .Because("Both InventoryReserved and PaymentInitiated should cascade");

    var cascadedEvents = CascadedEventTracker.GetCascadedEvents();
    await Assert.That(cascadedEvents.Any(e => e is InventoryReserved)).IsTrue();
    await Assert.That(cascadedEvents.Any(e => e is PaymentInitiated)).IsTrue();
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_ExactMatch_UsesOptimizedFastPathAsync() {
    // Arrange
    CascadedEventTracker.Reset();
    var dispatcher = _createDispatcher();
    var orderId = Guid.NewGuid();
    var command = new SimpleCreateOrder(orderId);

    // Act - Request OrderConfirmation from receptor that returns exactly OrderConfirmation
    var confirmation = await dispatcher.LocalInvokeAsync<OrderConfirmation>(command);

    // Assert - Should work correctly (uses fast path, no extraction needed)
    await Assert.That(confirmation).IsNotNull();
    await Assert.That(confirmation.OrderId).IsEqualTo(orderId);
    await Assert.That(confirmation.ConfirmationCode).IsEqualTo($"SIMPLE-{orderId:N}");

    // Assert - No cascading (single return value, no extra events)
    await Assert.That(CascadedEventTracker.Count).IsEqualTo(0);
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_TypeNotInTuple_ThrowsInvalidOperationAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var command = new CreateOrder(Guid.NewGuid(), 100m);

    // Act & Assert - Request PaymentInitiated from receptor that returns (OrderConfirmation, InventoryReserved)
    // This should throw because PaymentInitiated is not in the tuple
    await Assert.ThrowsAsync<InvalidOperationException>(async () => {
      await dispatcher.LocalInvokeAsync<PaymentInitiated>(command);
    });
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_ExtractViaInterface_WorksAsync() {
    // Arrange
    CascadedEventTracker.Reset();
    var dispatcher = _createDispatcher();
    var orderId = Guid.NewGuid();
    var command = new CreateOrder(orderId, 100m);

    // Act - Request IEvent (which InventoryReserved implements)
    var evt = await dispatcher.LocalInvokeAsync<IEvent>(command);

    // Assert - Should extract InventoryReserved (first IEvent in tuple)
    await Assert.That(evt).IsNotNull();
    await Assert.That(evt).IsTypeOf<InventoryReserved>();

    // Note: OrderConfirmation doesn't implement IEvent, so it cascades differently
    // The exact behavior depends on implementation details
  }

  [Test]
  [NotInParallel]
  public async Task LocalInvokeAsync_RpcResponseNotCascaded_OnlyOtherValuesAsync() {
    // Arrange
    CascadedEventTracker.Reset();
    var dispatcher = _createDispatcher();
    var orderId = Guid.NewGuid();
    var command = new CreateOrder(orderId, 100m);

    // Act
    var confirmation = await dispatcher.LocalInvokeAsync<OrderConfirmation>(command);

    // Assert - The extracted response (OrderConfirmation) should NOT be in cascaded events
    // Only InventoryReserved should cascade
    var cascadedEvents = CascadedEventTracker.GetCascadedEvents();

    // OrderConfirmation is not an IEvent so wouldn't cascade anyway,
    // but if it were, it should be excluded from cascade since it's the RPC response
    await Assert.That(cascadedEvents.Count).IsEqualTo(1);
    await Assert.That(cascadedEvents.All(e => e is not OrderConfirmation)).IsTrue();
  }

  // Note: Tests for RPC response ignoring routing wrappers (Route.Local/Route.Outbox)
  // are covered in ResponseExtractorTests.cs. The dispatcher integration tests
  // focus on the core RPC extraction flow with non-wrapped types due to
  // generator limitations with Routed<T> in tuple return types.

  // ========================================
  // HELPER METHODS
  // ========================================

  private static IDispatcher _createDispatcher() {
    var services = new ServiceCollection();

    // Register service instance provider (required dependency)
    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
      new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));

    // Register all receptors including our test receptors
    services.AddReceptors();

    // Register dispatcher
    services.AddWhizbangDispatcher();

    var serviceProvider = services.BuildServiceProvider();
    return serviceProvider.GetRequiredService<IDispatcher>();
  }
}
