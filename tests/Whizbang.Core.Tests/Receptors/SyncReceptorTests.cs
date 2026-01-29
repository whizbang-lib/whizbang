using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Tests.Common;

namespace Whizbang.Core.Tests.Receptors;

/// <summary>
/// Tests for synchronous receptor invocation via ISyncReceptor interface.
/// These tests define the required behavior for sync receptors.
/// </summary>
/// <docs>core-concepts/receptors#synchronous-receptors</docs>
[Category("Receptors")]
public class SyncReceptorTests : DiagnosticTestBase {
  protected override DiagnosticCategory DiagnosticCategories => DiagnosticCategory.ReceptorDiscovery;

  // Test Messages - unique names to avoid conflicts
  public record SyncCreateOrderCommand(Guid CustomerId, SyncOrderItem[] Items);
  public record SyncOrderItem(string Sku, int Quantity, decimal Price);
  public record SyncOrderResult(Guid OrderId);

  // Event with StreamKey for auto-cascade tests
  public record SyncOrderCreatedEvent([property: StreamKey] Guid OrderId, Guid CustomerId, decimal Total) : IEvent;

  /// <summary>
  /// Tests that a sync receptor returns a typed response directly (no ValueTask).
  /// </summary>
  [Test]
  public async Task SyncReceptor_Handle_ReturnsTypedResponseAsync() {
    // Arrange
    var receptor = new SyncOrderReceptor();
    var command = new SyncCreateOrderCommand(
        CustomerId: Guid.NewGuid(),
        Items: [new SyncOrderItem("SKU-001", 2, 29.99m)]
    );

    // Act
    var result = receptor.Handle(command);

    // Assert
    await Assert.That(result).IsTypeOf<SyncOrderResult>();
    await Assert.That(result.OrderId).IsNotEqualTo(Guid.Empty);
  }

  /// <summary>
  /// Tests that a sync receptor can return a tuple with multiple values.
  /// </summary>
  [Test]
  public async Task SyncReceptor_TupleReturn_ReturnsMultipleValuesAsync() {
    // Arrange
    var receptor = new SyncTupleReceptor();
    var command = new SyncCreateOrderCommand(
        CustomerId: Guid.NewGuid(),
        Items: [new SyncOrderItem("SKU-001", 2, 29.99m)]
    );

    // Act
    var (result, @event) = receptor.Handle(command);

    // Assert
    await Assert.That(result).IsTypeOf<SyncOrderResult>();
    await Assert.That(@event).IsTypeOf<SyncOrderCreatedEvent>();
    await Assert.That(result.OrderId).IsEqualTo(@event.OrderId);
  }

  /// <summary>
  /// Tests that sync receptors are stateless across invocations.
  /// </summary>
  [Test]
  public async Task SyncReceptor_Stateless_NoSharedStateAsync() {
    // Arrange
    var receptor = new SyncOrderReceptor();
    var command1 = new SyncCreateOrderCommand(
        CustomerId: Guid.NewGuid(),
        Items: [new SyncOrderItem("SKU-001", 1, 10.00m)]
    );
    var command2 = new SyncCreateOrderCommand(
        CustomerId: Guid.NewGuid(),
        Items: [new SyncOrderItem("SKU-002", 2, 20.00m)]
    );

    // Act
    var result1 = receptor.Handle(command1);
    var result2 = receptor.Handle(command2);

    // Assert - Each call should be independent
    await Assert.That(result1.OrderId).IsNotEqualTo(result2.OrderId);
  }

  /// <summary>
  /// Tests that sync receptors can throw exceptions for validation.
  /// </summary>
  [Test]
  public async Task SyncReceptor_Validation_ThrowsExceptionAsync() {
    // Arrange
    var receptor = new SyncOrderReceptor();
    var command = new SyncCreateOrderCommand(
        CustomerId: Guid.NewGuid(),
        Items: [] // Empty items should fail
    );

    // Act & Assert
    await Assert.That(() => receptor.Handle(command))
        .ThrowsExactly<InvalidOperationException>()
        .WithMessage("Order must have items");
  }

  /// <summary>
  /// Tests that void sync receptors execute synchronously without return value.
  /// </summary>
  [Test]
  public async Task VoidSyncReceptor_Handle_ExecutesSynchronouslyAsync() {
    // Arrange
    var receptor = new VoidSyncReceptor();
    var command = new SyncCreateOrderCommand(
        CustomerId: Guid.NewGuid(),
        Items: [new SyncOrderItem("SKU-001", 1, 10.00m)]
    );

    // Act
    receptor.Handle(command);

    // Assert - Side effect was executed
    await Assert.That(receptor.LastProcessedCustomerId).IsEqualTo(command.CustomerId);
  }

  // Test sync receptor implementations

  /// <summary>
  /// Simple sync receptor that returns a result.
  /// </summary>
  public class SyncOrderReceptor : ISyncReceptor<SyncCreateOrderCommand, SyncOrderResult> {
    public SyncOrderResult Handle(SyncCreateOrderCommand message) {
      // Validation
      if (message.Items.Length == 0) {
        throw new InvalidOperationException("Order must have items");
      }

      // Return result
      return new SyncOrderResult(Guid.NewGuid());
    }
  }

  /// <summary>
  /// Sync receptor that returns a tuple with result and event for auto-cascade.
  /// </summary>
  public class SyncTupleReceptor : ISyncReceptor<SyncCreateOrderCommand, (SyncOrderResult, SyncOrderCreatedEvent)> {
    public (SyncOrderResult, SyncOrderCreatedEvent) Handle(SyncCreateOrderCommand message) {
      var orderId = Guid.NewGuid();
      var total = message.Items.Sum(item => item.Quantity * item.Price);

      var result = new SyncOrderResult(orderId);
      var @event = new SyncOrderCreatedEvent(orderId, message.CustomerId, total);

      return (result, @event);
    }
  }

  /// <summary>
  /// Void sync receptor for side-effect-only operations.
  /// </summary>
  public class VoidSyncReceptor : ISyncReceptor<SyncCreateOrderCommand> {
    public Guid LastProcessedCustomerId { get; private set; }

    public void Handle(SyncCreateOrderCommand message) {
      // Side effect only
      LastProcessedCustomerId = message.CustomerId;
    }
  }
}
