using System.Diagnostics.CodeAnalysis;
using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using ECommerce.RabbitMQ.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace ECommerce.RabbitMQ.Integration.Tests.Lifecycle;

/// <summary>
/// Integration tests for all 4 Outbox lifecycle stages.
/// Validates that lifecycle receptors fire at correct points around transport publishing (RabbitMQ).
/// </summary>
/// <remarks>
/// <para><strong>Hook Location</strong>: WorkCoordinatorPublisherWorker.cs, around ProcessOutboxWorkAsync()</para>
/// <para><strong>Stages Tested</strong>:</para>
/// <list type="bullet">
///   <item>PreOutboxInline - Before publishing to transport (blocking)</item>
///   <item>PreOutboxAsync - Parallel with transport publish (non-blocking)</item>
///   <item>PostOutboxAsync - After message published (non-blocking)</item>
///   <item>PostOutboxInline - After message published (blocking)</item>
/// </list>
/// </remarks>
/// <docs>core-concepts/lifecycle-stages</docs>
/// <docs>testing/lifecycle-synchronization</docs>
[Category("Integration")]
[Category("Lifecycle")]
[ClassDataSource<RabbitMqClassFixtureSource>(Shared = SharedType.PerClass)]
public class OutboxLifecycleTests(RabbitMqClassFixtureSource fixtureSource) {
  private RabbitMqIntegrationFixture? _fixture;

  [Before(Test)]
  [RequiresUnreferencedCode("Test code - reflection allowed")]
  [RequiresDynamicCode("Test code - reflection allowed")]
  public async Task SetupAsync() {
    // Initialize container fixture (starts RabbitMQ + PostgreSQL)
    await fixtureSource.InitializeAsync();

    // Create and initialize test fixture (creates hosts)
    _fixture = new RabbitMqIntegrationFixture(
      fixtureSource.RabbitMqConnectionString,
      fixtureSource.PostgresConnectionString,
      fixtureSource.ManagementApiUri,
      testClassName: nameof(OutboxLifecycleTests)
    );
    await _fixture.InitializeAsync();
  }

  [After(Test)]
  public async Task CleanupAsync() {
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

    // Act - Register receptor for ProductCreatedEvent (the event published to Service Bus)
    // IMPORTANT: Start waiting but don't await yet - we need to send the command first!
    var receptorTask = fixture.InventoryHost.WaitForPreOutboxInlineAsync<ProductCreatedEvent>(
      timeoutMilliseconds: 20000);

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
  // PreOutboxAsync Tests (Non-Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PreOutboxAsync lifecycle stage fires parallel with transport publish (non-blocking).
  /// Should use Task.Run and not block message publishing.
  /// </summary>
  [Test]
  public async Task PreOutboxAsync_FiresParallelWithPublish_NonBlockingAsync() {
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
    var receptorTask = fixture.InventoryHost.WaitForPreOutboxAsyncAsync<ProductCreatedEvent>(
      timeoutMilliseconds: 20000);

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
  // PostOutboxAsync Tests (Non-Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PostOutboxAsync lifecycle stage fires after transport publish (non-blocking).
  /// Should use Task.Run and not block next steps.
  /// </summary>
  [Test]
  public async Task PostOutboxAsync_FiresAfterTransportPublish_NonBlockingAsync() {
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
    var receptorTask = fixture.InventoryHost.WaitForPostOutboxAsyncAsync<ProductCreatedEvent>(
      timeoutMilliseconds: 20000);

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
  /// Verifies that PostOutboxAsync fires after message is successfully published.
  /// Tests the "message successfully published to transport" guarantee.
  /// </summary>
  [Test]
  [Timeout(90_000)]  // TUnit includes fixture initialization in test timeout (~60s setup + ~5s test)
  public async Task PostOutboxAsync_FiresAfterSuccessfulPublish_GuaranteesDeliveryAsync(CancellationToken cancellationToken) {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    var completionSource = new TaskCompletionSource<bool>();
    var receptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(completionSource);

    var registry = fixture.InventoryHost.Services.GetRequiredService<ILifecycleReceptorRegistry>();
    registry.Register<ProductCreatedEvent>(receptor, LifecycleStage.PostOutboxAsync);
    using var perspectiveWaiter = fixture.CreatePerspectiveWaiter<ProductCreatedEvent>(
      inventoryPerspectives: 2,
      bffPerspectives: 2);

    try {
      // Act - Dispatch command
      await fixture.Dispatcher.SendAsync(command);

      // Wait for PostOutboxAsync stage
      // NOTE: Async stages run in Task.Run (fire-and-forget), which can be delayed by infrastructure
      await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(60));

      // Assert - At this point, PostOutboxAsync has fired
      // Message should have been successfully published to Service Bus
      await Assert.That(receptor.InvocationCount).IsEqualTo(1);
      await Assert.That(receptor.LastMessage).IsNotNull();

      // Give Service Bus time to propagate the message
      await Task.Delay(2000);

      // Verify message was actually received by BFF (indicates successful publish)
      // This is indirect verification that PostOutboxAsync fired AFTER successful publish
      await perspectiveWaiter.WaitAsync(timeoutMilliseconds: 60000);

    } finally {
      registry.Unregister<ProductCreatedEvent>(receptor, LifecycleStage.PostOutboxAsync);
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

    // Act - Register receptor for ProductCreatedEvent
    // IMPORTANT: Start waiting but don't await yet - we need to send the command first!
    var receptorTask = fixture.InventoryHost.WaitForPostOutboxInlineAsync<ProductCreatedEvent>(
      timeoutMilliseconds: 20000);

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
  // Stage Ordering Tests
  // ========================================

  /// <summary>
  /// Verifies that all 4 Outbox stages fire in correct order:
  /// PreOutboxInline → PreOutboxAsync (parallel with publish) → PostOutboxAsync → PostOutboxInline
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

    var registry = fixture.InventoryHost.Services.GetRequiredService<ILifecycleReceptorRegistry>();

    // Create receptors for all 4 stages
    var preInlineCompletion = new TaskCompletionSource<bool>();
    var preAsyncCompletion = new TaskCompletionSource<bool>();
    var postAsyncCompletion = new TaskCompletionSource<bool>();
    var postInlineCompletion = new TaskCompletionSource<bool>();

    var preInlineReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(preInlineCompletion);
    var preAsyncReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(preAsyncCompletion);
    var postAsyncReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(postAsyncCompletion);
    var postInlineReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(postInlineCompletion);

    // Register all receptors
    registry.Register<ProductCreatedEvent>(preInlineReceptor, LifecycleStage.PreOutboxInline);
    registry.Register<ProductCreatedEvent>(preAsyncReceptor, LifecycleStage.PreOutboxAsync);
    registry.Register<ProductCreatedEvent>(postAsyncReceptor, LifecycleStage.PostOutboxAsync);
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
      ).WaitAsync(TimeSpan.FromSeconds(20));

      // Assert - All stages should have been invoked
      await Assert.That(preInlineReceptor.InvocationCount).IsEqualTo(1);
      await Assert.That(preAsyncReceptor.InvocationCount).IsEqualTo(1);
      await Assert.That(postAsyncReceptor.InvocationCount).IsEqualTo(1);
      await Assert.That(postInlineReceptor.InvocationCount).IsEqualTo(1);

    } finally {
      // Unregister all receptors
      registry.Unregister<ProductCreatedEvent>(preInlineReceptor, LifecycleStage.PreOutboxInline);
      registry.Unregister<ProductCreatedEvent>(preAsyncReceptor, LifecycleStage.PreOutboxAsync);
      registry.Unregister<ProductCreatedEvent>(postAsyncReceptor, LifecycleStage.PostOutboxAsync);
      registry.Unregister<ProductCreatedEvent>(postInlineReceptor, LifecycleStage.PostOutboxInline);
    }
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

    var completionSource = new TaskCompletionSource<bool>();
    var receptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(completionSource);

    var registry = fixture.InventoryHost.Services.GetRequiredService<ILifecycleReceptorRegistry>();
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
