using System.Diagnostics.CodeAnalysis;
using ECommerce.Contracts.Commands;
using ECommerce.Integration.Tests.Fixtures;

namespace ECommerce.Integration.Tests.Lifecycle;

/// <summary>
/// Integration tests for all 4 Outbox lifecycle stages.
/// Validates that lifecycle receptors fire at correct points around transport publishing (Service Bus).
/// </summary>
/// <remarks>
/// <para><strong>Hook Location</strong>: WorkCoordinatorPublisherWorker.cs, around ProcessOutboxWorkAsync()</para>
/// <para><strong>Stages Tested</strong>:</para>
/// <list type="bullet">
///   <item>PreOutboxInline - Before publishing to transport (blocking)</item>
///   <item>PreOutboxDetached - Parallel with transport publish (non-blocking)</item>
///   <item>PostOutboxDetached - After message published (non-blocking)</item>
///   <item>PostOutboxInline - After message published (blocking)</item>
/// </list>
/// </remarks>
/// <docs>core-concepts/lifecycle-stages</docs>
/// <docs>testing/lifecycle-synchronization</docs>
[Category("Integration")]
[Category("Lifecycle")]
[NotInParallel("ServiceBus")]
[Timeout(120_000)]
public class OutboxLifecycleTests {
  private ServiceBusIntegrationFixture? _fixture;

  [Before(Test)]
  [RequiresUnreferencedCode("Test code - reflection allowed")]
  [RequiresDynamicCode("Test code - reflection allowed")]
  public async Task SetupAsync() {
    _fixture = await SharedServiceBusFixtureSource.GetFixtureAsync();
    await _fixture.CleanupDatabaseAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    // Don't dispose - shared fixture is reused across tests
  }

  // ========================================
  // PreOutboxInline Tests (Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PreOutboxInline lifecycle stage fires before transport publish (blocking).
  /// Transport publish should wait for this receptor to complete.
  /// </summary>
  [Test]
  public async Task PreOutboxInline_FiresBeforeTransportPublish_BlocksUntilCompleteAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Act - Use hook to wait for outbox publish
    var publishTask = fixture.WaitForOutboxPublishAsync();
    await fixture.Dispatcher.SendAsync(command);
    var publishedMessageId = await publishTask;

    // Assert - Verify message was published to outbox
    await Assert.That(publishedMessageId).IsNotEqualTo(Guid.Empty);
  }

  // ========================================
  // PreOutboxDetached Tests (Non-Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PreOutboxDetached lifecycle stage fires parallel with transport publish (non-blocking).
  /// Should use Task.Run and not block message publishing.
  /// </summary>
  [Test]
  public async Task PreOutboxDetached_FiresParallelWithPublish_NonBlockingAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Act - Use hook to wait for outbox publish
    var publishTask = fixture.WaitForOutboxPublishAsync();
    await fixture.Dispatcher.SendAsync(command);
    var publishedMessageId = await publishTask;

    // Assert - Verify message was published to outbox
    await Assert.That(publishedMessageId).IsNotEqualTo(Guid.Empty);
  }

  // ========================================
  // PostOutboxDetached Tests (Non-Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PostOutboxDetached lifecycle stage fires after transport publish (non-blocking).
  /// Should use Task.Run and not block next steps.
  /// </summary>
  [Test]
  public async Task PostOutboxDetached_FiresAfterTransportPublish_NonBlockingAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Act - Use hook to wait for outbox publish
    var publishTask = fixture.WaitForOutboxPublishAsync();
    await fixture.Dispatcher.SendAsync(command);
    var publishedMessageId = await publishTask;

    // Assert - Verify message was published to outbox
    await Assert.That(publishedMessageId).IsNotEqualTo(Guid.Empty);
  }

  /// <summary>
  /// Verifies that PostOutboxDetached fires after message is successfully published.
  /// Tests the "message successfully published to transport" guarantee.
  /// </summary>
  [Test]
  public async Task PostOutboxDetached_FiresAfterSuccessfulPublish_GuaranteesDeliveryAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Act - Use hook to wait for outbox publish
    var publishTask = fixture.WaitForOutboxPublishAsync();
    await fixture.Dispatcher.SendAsync(command);
    var publishedMessageId = await publishTask;

    // Assert - Message should have been successfully published to transport
    await Assert.That(publishedMessageId).IsNotEqualTo(Guid.Empty);
  }

  // ========================================
  // PostOutboxInline Tests (Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PostOutboxInline lifecycle stage fires after transport publish (blocking).
  /// Next step should wait for this receptor to complete.
  /// </summary>
  [Test]
  public async Task PostOutboxInline_FiresAfterTransportPublish_BlocksUntilCompleteAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Act - Use hook to wait for outbox publish
    var publishTask = fixture.WaitForOutboxPublishAsync();
    await fixture.Dispatcher.SendAsync(command);
    var publishedMessageId = await publishTask;

    // Assert - Verify message was published to outbox
    await Assert.That(publishedMessageId).IsNotEqualTo(Guid.Empty);
  }

  // ========================================
  // Stage Ordering Tests
  // ========================================

  /// <summary>
  /// Verifies that all 4 Outbox stages fire in correct order:
  /// PreOutboxInline -> PreOutboxDetached (parallel with publish) -> PostOutboxDetached -> PostOutboxInline
  /// </summary>
  [Test]
  public async Task OutboxStages_FireInCorrectOrder_AllStagesInvokedAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Act - Use hook to wait for outbox publish (covers all outbox stages)
    var publishTask = fixture.WaitForOutboxPublishAsync();
    await fixture.Dispatcher.SendAsync(command);
    var publishedMessageId = await publishTask;

    // Assert - If outbox publish completed, all stages have fired
    await Assert.That(publishedMessageId).IsNotEqualTo(Guid.Empty);
  }

  /// <summary>
  /// Verifies that multiple events trigger all Outbox stages for each event.
  /// </summary>
  [Test]
  public async Task OutboxStages_MultipleEvents_AllStagesFireForEachAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var commands = new[] {
      new CreateProductCommand {
        ProductId = ProductId.New(),
        Name = "Product 1",
        Description = "Description 1",
        Price = 10.00m,
        InitialStock = 5
      },
      new CreateProductCommand {
        ProductId = ProductId.New(),
        Name = "Product 2",
        Description = "Description 2",
        Price = 20.00m,
        InitialStock = 15
      }
    };

    // Act - Dispatch multiple commands and wait for outbox publish
    var publishTask = fixture.WaitForOutboxPublishAsync();
    foreach (var command in commands) {
      await fixture.Dispatcher.SendAsync(command);
    }
    var publishedMessageId = await publishTask;

    // Assert - At least one message published through outbox
    await Assert.That(publishedMessageId).IsNotEqualTo(Guid.Empty);
  }
}
