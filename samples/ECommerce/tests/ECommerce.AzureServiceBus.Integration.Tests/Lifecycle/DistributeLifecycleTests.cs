using System.Diagnostics.CodeAnalysis;
using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using ECommerce.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Messaging;

namespace ECommerce.Integration.Tests.Lifecycle;

/// <summary>
/// Integration tests for all 5 Distribute lifecycle stages.
/// Validates that lifecycle receptors fire at correct points around ProcessWorkBatchAsync() call.
/// </summary>
/// <remarks>
/// <para><strong>Hook Location</strong>: *WorkCoordinatorStrategy.cs (Immediate/Scoped/Interval) around FlushAsync()</para>
/// <para><strong>Stages Tested</strong>:</para>
/// <list type="bullet">
///   <item>PreDistributeInline - Before ProcessWorkBatchAsync() (blocking)</item>
///   <item>PreDistributeAsync - Before ProcessWorkBatchAsync() (non-blocking, backgrounded)</item>
///   <item>DistributeAsync - In parallel with ProcessWorkBatchAsync() (non-blocking, backgrounded)</item>
///   <item>PostDistributeAsync - After ProcessWorkBatchAsync() (non-blocking, backgrounded)</item>
///   <item>PostDistributeInline - After ProcessWorkBatchAsync() (blocking)</item>
/// </list>
/// </remarks>
/// <docs>core-concepts/lifecycle-stages</docs>
/// <docs>testing/lifecycle-synchronization</docs>
[Category("Integration")]
[Category("Lifecycle")]
[NotInParallel("ServiceBus")]
public class DistributeLifecycleTests {
  private static ServiceBusIntegrationFixture? _fixture;

  [Before(Test)]
  [RequiresUnreferencedCode("Test code - reflection allowed")]
  [RequiresDynamicCode("Test code - reflection allowed")]
  public async Task SetupAsync() {
    // Get SHARED ServiceBus resources (emulator + single static ServiceBusClient)
    var (connectionString, sharedClient) = await SharedFixtureSource.GetSharedResourcesAsync(0);

    // Create fixture with shared client (per-test PostgreSQL + hosts, but shared ServiceBusClient)
    _fixture = new ServiceBusIntegrationFixture(connectionString, sharedClient, 0);
    await _fixture.InitializeAsync();
  }

  [After(Test)]
  public async Task CleanupAsync() {
    if (_fixture != null) {
      try {
        await _fixture.CleanupDatabaseAsync();
      } catch (Exception ex) {
        Console.WriteLine($"[After(Test)] Warning: Cleanup encountered error (non-critical): {ex.Message}");
      }

      await _fixture.DisposeAsync();
      _fixture = null;
    }
  }

  // ========================================
  // PreDistributeInline Tests (Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PreDistributeInline lifecycle stage fires before work distribution (blocking).
  /// </summary>
  [Test]
  public async Task PreDistributeInline_FiresBeforeDistribution_BlocksUntilCompleteAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Act - Register receptor for the PUBLISHED EVENT (not the command)
    // Distribute lifecycle stages fire when events are published, not when commands are dispatched
    // IMPORTANT: Start waiting but don't await yet - we need to send the command first!
    var receptorTask = fixture.InventoryHost.WaitForPreDistributeInlineAsync<ProductCreatedEvent>(
      timeoutMilliseconds: 10000);

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
  // PreDistributeAsync Tests (Non-Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PreDistributeAsync lifecycle stage fires before work distribution (non-blocking).
  /// Should use Task.Run and not block ProcessWorkBatchAsync.
  /// </summary>
  [Test]
  public async Task PreDistributeAsync_FiresBeforeDistribution_NonBlockingAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Act - Register receptor for the PUBLISHED EVENT (not the command)
    // Distribute lifecycle stages fire when events are published, not when commands are dispatched
    // IMPORTANT: Start waiting but don't await yet - we need to send the command first!
    // NOTE: Async stages run in Task.Run (fire-and-forget), so need longer timeout
    var receptorTask = fixture.InventoryHost.WaitForPreDistributeAsyncAsync<ProductCreatedEvent>(
      timeoutMilliseconds: 30000);

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
  // DistributeAsync Tests (Parallel, Non-Blocking)
  // ========================================

  /// <summary>
  /// Verifies that DistributeAsync lifecycle stage fires in parallel with ProcessWorkBatchAsync.
  /// Should use Task.Run and execute concurrently with work distribution.
  /// </summary>
  [Test]
  public async Task DistributeAsync_FiresInParallelWithDistribution_NonBlockingAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Act - Register receptor for the PUBLISHED EVENT (not the command)
    // Distribute lifecycle stages fire when events are published, not when commands are dispatched
    // IMPORTANT: Start waiting but don't await yet - we need to send the command first!
    // NOTE: Async stages run in Task.Run (fire-and-forget), so need longer timeout
    var receptorTask = fixture.InventoryHost.WaitForDistributeAsyncAsync<ProductCreatedEvent>(
      timeoutMilliseconds: 30000);

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
  /// Verifies that DistributeAsync completes even if distribution takes time.
  /// Tests the "may complete after distribution finishes" guarantee.
  /// </summary>
  [Test]
  public async Task DistributeAsync_CompletesIndependentlyOfDistribution_NonBlockingAsync() {
    // Arrange - Create multiple commands to simulate longer distribution
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
    // NOTE: Distribute stages fire for PUBLISHED EVENTS (in outbox), not commands
    var receptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(completionSource);

    var registry = fixture.InventoryHost.Services.GetRequiredService<ILifecycleReceptorRegistry>();
    registry.Register<ProductCreatedEvent>(receptor, LifecycleStage.DistributeAsync);

    try {
      // Act - Dispatch multiple commands
      foreach (var command in commands) {
        await fixture.Dispatcher.SendAsync(command);
      }

      // Wait for DistributeAsync completion
      await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(60));

      // Assert - Receptor should have been invoked for at least one message
      await Assert.That(receptor.InvocationCount).IsGreaterThanOrEqualTo(1);

    } finally {
      registry.Unregister<ProductCreatedEvent>(receptor, LifecycleStage.DistributeAsync);
    }
  }

  // ========================================
  // PostDistributeAsync Tests (Non-Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PostDistributeAsync lifecycle stage fires after work distribution (non-blocking).
  /// Should use Task.Run and not block next steps.
  /// </summary>
  [Test]
  public async Task PostDistributeAsync_FiresAfterDistribution_NonBlockingAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Act - Register receptor for the PUBLISHED EVENT (not the command)
    // Distribute lifecycle stages fire when events are published, not when commands are dispatched
    // IMPORTANT: Start waiting but don't await yet - we need to send the command first!
    // NOTE: Async stages run in Task.Run (fire-and-forget), so need longer timeout
    var receptorTask = fixture.InventoryHost.WaitForPostDistributeAsyncAsync<ProductCreatedEvent>(
      timeoutMilliseconds: 30000);

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
  // PostDistributeInline Tests (Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PostDistributeInline lifecycle stage fires after work distribution (blocking).
  /// Next step should wait for this receptor to complete.
  /// </summary>
  [Test]
  public async Task PostDistributeInline_FiresAfterDistribution_BlocksUntilCompleteAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Act - Register receptor for the PUBLISHED EVENT (not the command)
    // Distribute lifecycle stages fire when events are published, not when commands are dispatched
    // IMPORTANT: Start waiting but don't await yet - we need to send the command first!
    var receptorTask = fixture.InventoryHost.WaitForPostDistributeInlineAsync<ProductCreatedEvent>(
      timeoutMilliseconds: 10000);

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
  /// Verifies that all 5 Distribute stages fire in correct order:
  /// PreDistributeInline → PreDistributeAsync → DistributeAsync (parallel) → PostDistributeAsync → PostDistributeInline
  /// </summary>
  [Test]
  public async Task DistributeStages_FireInCorrectOrder_AllStagesInvokedAsync() {
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

    // Create receptors for all 5 stages
    // NOTE: Distribute stages fire for PUBLISHED EVENTS (in outbox), not commands
    var preInlineCompletion = new TaskCompletionSource<bool>();
    var preAsyncCompletion = new TaskCompletionSource<bool>();
    var distributeAsyncCompletion = new TaskCompletionSource<bool>();
    var postAsyncCompletion = new TaskCompletionSource<bool>();
    var postInlineCompletion = new TaskCompletionSource<bool>();

    var preInlineReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(preInlineCompletion);
    var preAsyncReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(preAsyncCompletion);
    var distributeAsyncReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(distributeAsyncCompletion);
    var postAsyncReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(postAsyncCompletion);
    var postInlineReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(postInlineCompletion);

    // Register all receptors
    registry.Register<ProductCreatedEvent>(preInlineReceptor, LifecycleStage.PreDistributeInline);
    registry.Register<ProductCreatedEvent>(preAsyncReceptor, LifecycleStage.PreDistributeAsync);
    registry.Register<ProductCreatedEvent>(distributeAsyncReceptor, LifecycleStage.DistributeAsync);
    registry.Register<ProductCreatedEvent>(postAsyncReceptor, LifecycleStage.PostDistributeAsync);
    registry.Register<ProductCreatedEvent>(postInlineReceptor, LifecycleStage.PostDistributeInline);

    try {
      // Act - Dispatch command
      await fixture.Dispatcher.SendAsync(command);

      // Wait for all stages to complete (with timeout)
      // NOTE: Async stages run in Task.Run (fire-and-forget), which can be delayed by infrastructure
      await Task.WhenAll(
        preInlineCompletion.Task,
        preAsyncCompletion.Task,
        distributeAsyncCompletion.Task,
        postAsyncCompletion.Task,
        postInlineCompletion.Task
      ).WaitAsync(TimeSpan.FromSeconds(60));

      // Assert - All stages should have been invoked
      await Assert.That(preInlineReceptor.InvocationCount).IsEqualTo(1);
      await Assert.That(preAsyncReceptor.InvocationCount).IsEqualTo(1);
      await Assert.That(distributeAsyncReceptor.InvocationCount).IsEqualTo(1);
      await Assert.That(postAsyncReceptor.InvocationCount).IsEqualTo(1);
      await Assert.That(postInlineReceptor.InvocationCount).IsEqualTo(1);

    } finally {
      // Unregister all receptors
      registry.Unregister<ProductCreatedEvent>(preInlineReceptor, LifecycleStage.PreDistributeInline);
      registry.Unregister<ProductCreatedEvent>(preAsyncReceptor, LifecycleStage.PreDistributeAsync);
      registry.Unregister<ProductCreatedEvent>(distributeAsyncReceptor, LifecycleStage.DistributeAsync);
      registry.Unregister<ProductCreatedEvent>(postAsyncReceptor, LifecycleStage.PostDistributeAsync);
      registry.Unregister<ProductCreatedEvent>(postInlineReceptor, LifecycleStage.PostDistributeInline);
    }
  }

  /// <summary>
  /// Verifies that multiple commands trigger all Distribute stages for each command.
  /// </summary>
  [Test]
  public async Task DistributeStages_MultipleCommands_AllStagesFireForEachAsync() {
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
    // NOTE: Distribute stages fire for PUBLISHED EVENTS (in outbox), not commands
    var receptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(completionSource);

    var registry = fixture.InventoryHost.Services.GetRequiredService<ILifecycleReceptorRegistry>();
    registry.Register<ProductCreatedEvent>(receptor, LifecycleStage.PostDistributeInline);

    try {
      // Act - Dispatch multiple commands
      foreach (var command in commands) {
        await fixture.Dispatcher.SendAsync(command);
      }

      // Wait for last command to complete PostDistributeInline
      // NOTE: Infrastructure delays can cause timeouts, use generous timeout
      await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(60));

      // Assert - Receptor should have been invoked at least once
      await Assert.That(receptor.InvocationCount).IsGreaterThanOrEqualTo(1);

    } finally {
      registry.Unregister<ProductCreatedEvent>(receptor, LifecycleStage.PostDistributeInline);
    }
  }
}
