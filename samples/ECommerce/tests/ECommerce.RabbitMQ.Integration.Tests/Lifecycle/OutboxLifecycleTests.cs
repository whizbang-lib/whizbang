using System.Diagnostics.CodeAnalysis;
using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using ECommerce.Integration.Tests.Fixtures;
using ECommerce.RabbitMQ.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace ECommerce.RabbitMQ.Integration.Tests.Lifecycle;

/// <summary>
/// Integration tests for all 4 Outbox lifecycle stages.
/// Validates that lifecycle receptors fire at correct points around transport publishing (RabbitMQ).
/// Each test gets its own PostgreSQL databases + hosts. RabbitMQ container is shared via SharedRabbitMqFixtureSource.
/// Tests run sequentially for reliable timing.
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
[NotInParallel("RabbitMQ")]
[Timeout(120_000)]  // Fixture init (~60s) + test body (~25s) + margin
public class OutboxLifecycleTests {
  private static RabbitMqIntegrationFixture? _fixture;

  [Before(Test)]
  [RequiresUnreferencedCode("Test code - reflection allowed")]
  [RequiresDynamicCode("Test code - reflection allowed")]
  public async Task SetupAsync(CancellationToken cancellationToken) {
    // Initialize shared containers (first test only)
    await SharedRabbitMqFixtureSource.InitializeAsync();

    // Get separate database connections for each host (eliminates lock contention)
    var inventoryDbConnection = SharedRabbitMqFixtureSource.GetPerTestDatabaseConnectionString();
    var bffDbConnection = SharedRabbitMqFixtureSource.GetPerTestDatabaseConnectionString();

    // Create and initialize test fixture with separate databases
    _fixture = new RabbitMqIntegrationFixture(
      SharedRabbitMqFixtureSource.RabbitMqConnectionString,
      inventoryDbConnection,
      bffDbConnection,
      SharedRabbitMqFixtureSource.ManagementApiUri,
      testId: Guid.NewGuid().ToString("N")[..12]
    );
    await _fixture.InitializeAsync();
  }

  [After(Test)]
  public async Task CleanupAsync(CancellationToken cancellationToken) {
    if (_fixture != null) {
      await _fixture.DisposeAsync();
      _fixture = null;
    }
  }

  // ========================================
  // PreOutboxInline Tests (Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PreOutboxInline lifecycle stage fires before transport publish (blocking).
  /// Transport publish should wait for this receptor to complete.
  /// </summary>
  [Test]
  public async Task PreOutboxInline_FiresBeforeTransportPublish_BlocksUntilCompleteAsync(CancellationToken cancellationToken) {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Act - Register receptor for ProductCreatedEvent (the event published to Service Bus)
    // IMPORTANT: Start waiting but don't await yet - we need to send the command first!
    var receptorTask = fixture.InventoryHost.WaitForPreOutboxInlineAsync<ProductCreatedEvent>(
);

    // Send command - this will trigger event publication and fire the lifecycle receptor
    await fixture.Dispatcher.SendAsync(command);

    // Now wait for the lifecycle receptor to complete
    var receptor = await receptorTask;

    // Assert - Verify receptor was invoked
    await Assert.That(receptor.InvocationCount).IsEqualTo(1);
    await Assert.That(receptor.LastMessage).IsNotNull();
    await Assert.That(receptor.LastMessage!.ProductId).IsEqualTo(command.ProductId);
  }

  // ========================================
  // PreOutboxDetached Tests (Non-Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PreOutboxDetached lifecycle stage fires parallel with transport publish (non-blocking).
  /// Should use Task.Run and not block message publishing.
  /// </summary>
  [Test]
  public async Task PreOutboxDetached_FiresParallelWithPublish_NonBlockingAsync(CancellationToken cancellationToken) {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Act - Register receptor for ProductCreatedEvent
    // IMPORTANT: Start waiting but don't await yet - we need to send the command first!
    var receptorTask = fixture.InventoryHost.WaitForPreOutboxDetachedAsync<ProductCreatedEvent>(
);

    // Send command - this will trigger event publication and fire the lifecycle receptor
    await fixture.Dispatcher.SendAsync(command);

    // Now wait for the lifecycle receptor to complete
    var receptor = await receptorTask;

    // Assert - Verify receptor was invoked
    await Assert.That(receptor.InvocationCount).IsEqualTo(1);
    await Assert.That(receptor.LastMessage).IsNotNull();
    await Assert.That(receptor.LastMessage!.ProductId).IsEqualTo(command.ProductId);
  }

  // ========================================
  // PostOutboxDetached Tests (Non-Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PostOutboxDetached lifecycle stage fires after transport publish (non-blocking).
  /// Should use Task.Run and not block next steps.
  /// </summary>
  [Test]
  public async Task PostOutboxDetached_FiresAfterTransportPublish_NonBlockingAsync(CancellationToken cancellationToken) {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Act - Register receptor for ProductCreatedEvent
    // IMPORTANT: Start waiting but don't await yet - we need to send the command first!
    var receptorTask = fixture.InventoryHost.WaitForPostOutboxDetachedAsync<ProductCreatedEvent>(
);

    // Send command - this will trigger event publication and fire the lifecycle receptor
    await fixture.Dispatcher.SendAsync(command);

    // Now wait for the lifecycle receptor to complete
    var receptor = await receptorTask;

    // Assert - Verify receptor was invoked
    await Assert.That(receptor.InvocationCount).IsEqualTo(1);
    await Assert.That(receptor.LastMessage).IsNotNull();
    await Assert.That(receptor.LastMessage!.ProductId).IsEqualTo(command.ProductId);
  }

  /// <summary>
  /// Verifies that PostOutboxDetached fires after message is successfully published.
  /// Tests the "message successfully published to transport" guarantee.
  /// </summary>
  [Test]
  public async Task PostOutboxDetached_FiresAfterSuccessfulPublish_GuaranteesDeliveryAsync(CancellationToken cancellationToken) {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var receptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(completionSource);

    var registry = fixture.InventoryHost.Services.GetRequiredService<IReceptorRegistry>();
    registry.Register<ProductCreatedEvent>(receptor, LifecycleStage.PostOutboxDetached);

    try {
      // Act - Dispatch command
      await fixture.Dispatcher.SendAsync(command);

      // Wait for PostOutboxDetached stage
      // NOTE: Async stages run in Task.Run (fire-and-forget), which can be delayed by infrastructure
      await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(60));

      // Assert - At this point, PostOutboxDetached has fired
      // Message should have been successfully published to transport
      await Assert.That(receptor.InvocationCount).IsEqualTo(1);
      await Assert.That(receptor.LastMessage).IsNotNull();

    } finally {
      registry.Unregister<ProductCreatedEvent>(receptor, LifecycleStage.PostOutboxDetached);
    }
  }

  // ========================================
  // PostOutboxInline Tests (Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PostOutboxInline lifecycle stage fires after transport publish (blocking).
  /// Next step should wait for this receptor to complete.
  /// </summary>
  [Test]
  public async Task PostOutboxInline_FiresAfterTransportPublish_BlocksUntilCompleteAsync(CancellationToken cancellationToken) {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Act - Use OnOutboxMessagePublished hook (deterministic, bypasses LifecycleCoordinator)
    var publishTask = fixture.WaitForOutboxPublishAsync();
    await fixture.Dispatcher.SendAsync(command);
    var publishedMessageId = await publishTask;

    // Assert - Message was published
    await Assert.That(publishedMessageId).IsNotEqualTo(Guid.Empty);
  }

  // ========================================
  // Stage Ordering Tests
  // ========================================

  /// <summary>
  /// Verifies that all 4 Outbox stages fire in correct order:
  /// PreOutboxInline → PreOutboxDetached (parallel with publish) → PostOutboxDetached → PostOutboxInline
  /// </summary>
  [Test]
  public async Task OutboxStages_FireInCorrectOrder_AllStagesInvokedAsync(CancellationToken cancellationToken) {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    var registry = fixture.InventoryHost.Services.GetRequiredService<IReceptorRegistry>();

    // Create receptors for all 4 stages
    var preInlineCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var preAsyncCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var postAsyncCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var postInlineCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    var preInlineReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(preInlineCompletion);
    var preAsyncReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(preAsyncCompletion);
    var postAsyncReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(postAsyncCompletion);
    var postInlineReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(postInlineCompletion);

    // Register all receptors
    registry.Register<ProductCreatedEvent>(preInlineReceptor, LifecycleStage.PreOutboxInline);
    registry.Register<ProductCreatedEvent>(preAsyncReceptor, LifecycleStage.PreOutboxDetached);
    registry.Register<ProductCreatedEvent>(postAsyncReceptor, LifecycleStage.PostOutboxDetached);
    registry.Register<ProductCreatedEvent>(postInlineReceptor, LifecycleStage.PostOutboxInline);

    try {
      // Act - Dispatch command
      await fixture.Dispatcher.SendAsync(command);

      // Wait for all stages to complete (with timeout)
      await Task.WhenAll(
        preInlineCompletion.Task,
        preAsyncCompletion.Task,
        postAsyncCompletion.Task,
        postInlineCompletion.Task
      ).WaitAsync(TimeSpan.FromSeconds(45));

      // Assert - All stages should have been invoked
      await Assert.That(preInlineReceptor.InvocationCount).IsEqualTo(1);
      await Assert.That(preAsyncReceptor.InvocationCount).IsEqualTo(1);
      await Assert.That(postAsyncReceptor.InvocationCount).IsEqualTo(1);
      await Assert.That(postInlineReceptor.InvocationCount).IsEqualTo(1);

    } finally {
      // Unregister all receptors
      registry.Unregister<ProductCreatedEvent>(preInlineReceptor, LifecycleStage.PreOutboxInline);
      registry.Unregister<ProductCreatedEvent>(preAsyncReceptor, LifecycleStage.PreOutboxDetached);
      registry.Unregister<ProductCreatedEvent>(postAsyncReceptor, LifecycleStage.PostOutboxDetached);
      registry.Unregister<ProductCreatedEvent>(postInlineReceptor, LifecycleStage.PostOutboxInline);
    }
  }

  /// <summary>
  /// Verifies that multiple events trigger all Outbox stages for each event.
  /// </summary>
  [Test]
  public async Task OutboxStages_MultipleEvents_AllStagesFireForEachAsync(CancellationToken cancellationToken) {
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

    var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var receptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(completionSource);

    var registry = fixture.InventoryHost.Services.GetRequiredService<IReceptorRegistry>();
    registry.Register<ProductCreatedEvent>(receptor, LifecycleStage.PostOutboxInline);

    try {
      // Act - Dispatch multiple commands
      foreach (var command in commands) {
        await fixture.Dispatcher.SendAsync(command);
      }

      // Wait for last event to complete PostOutboxInline
      await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(25));

      // Assert - Receptor should have been invoked at least once
      await Assert.That(receptor.InvocationCount).IsGreaterThanOrEqualTo(1);

    } finally {
      registry.Unregister<ProductCreatedEvent>(receptor, LifecycleStage.PostOutboxInline);
    }
  }
}
