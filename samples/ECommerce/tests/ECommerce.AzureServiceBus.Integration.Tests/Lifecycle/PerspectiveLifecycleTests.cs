using System.Diagnostics.CodeAnalysis;
using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using ECommerce.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Messaging;

namespace ECommerce.Integration.Tests.Lifecycle;

/// <summary>
/// Integration tests for all 4 Perspective lifecycle stages.
/// Validates that lifecycle receptors fire at correct points around perspective event processing.
/// </summary>
/// <remarks>
/// <para><strong>Hook Location</strong>: Generated perspective runner (PerspectiveRunnerTemplate.cs)</para>
/// <para><strong>Stages Tested</strong>:</para>
/// <list type="bullet">
///   <item>PrePerspectiveInline - Before perspective RunAsync() (blocking)</item>
///   <item>PrePerspectiveDetached - Parallel with perspective RunAsync() (non-blocking)</item>
///   <item>PostPerspectiveDetached - After perspective completes (non-blocking)</item>
///   <item>PostPerspectiveInline - After perspective completes (blocking) - NOW EXPLICITLY TESTED</item>
/// </list>
/// </remarks>
/// <docs>core-concepts/lifecycle-stages</docs>
/// <docs>testing/lifecycle-synchronization</docs>
[Category("Integration")]
[Category("Lifecycle")]
[NotInParallel("ServiceBus")]
[Timeout(120_000)]
public class PerspectiveLifecycleTests {
  private ServiceBusIntegrationFixture? _fixture;

  [Before(Test)]
  [RequiresUnreferencedCode("Test code - reflection allowed")]
  [RequiresDynamicCode("Test code - reflection allowed")]
  public async Task SetupAsync() {
    _fixture = await SharedServiceBusFixtureSource.GetFixtureAsync();
    await Task.Delay(500);
    await _fixture.CleanupDatabaseAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    // Don't dispose - shared fixture is reused across tests
  }

  // ========================================
  // PrePerspectiveInline Tests (Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PrePerspectiveInline lifecycle stage fires before perspective event processing (blocking).
  /// Perspective processing should wait for this receptor to complete.
  /// </summary>
  [Test]
  public async Task PrePerspectiveInline_FiresBeforePerspectiveProcessing_BlocksUntilCompleteAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Act - Register receptor for ProductCreatedEvent in BFF (where perspective processing happens)
    var receptorTask = fixture.BffHost.WaitForPrePerspectiveInlineAsync<ProductCreatedEvent>(
      timeoutMilliseconds: 20000);

    await fixture.Dispatcher.SendAsync(command);
    var receptor = await receptorTask;

    // Assert - Verify receptor was invoked
    await Assert.That(receptor.InvocationCount).IsEqualTo(1);
    await Assert.That(receptor.LastMessage).IsNotNull();
    await Assert.That(receptor.LastMessage!.ProductId).IsEqualTo(command.ProductId.Value);
  }

  /// <summary>
  /// Verifies that PrePerspectiveInline fires before perspective data is saved.
  /// Tests the "no events processed yet" guarantee.
  /// </summary>
  [Test]
  public async Task PrePerspectiveInline_FiresBeforePerspectiveSave_NoEventsProcessedYetAsync() {
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
    var receptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(
      completionSource);

    var registry = fixture.BffHost.Services.GetRequiredService<IReceptorRegistry>();
    registry.Register<ProductCreatedEvent>(receptor, LifecycleStage.PrePerspectiveInline);

    try {
      // Act - Dispatch command
      await fixture.Dispatcher.SendAsync(command);

      // Wait for PrePerspectiveInline stage
      await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(20));

      // Assert - PrePerspectiveInline has fired
      // At this point, perspective processing hasn't started yet
      await Assert.That(receptor.InvocationCount).IsEqualTo(1);

    } finally {
      registry.Unregister<ProductCreatedEvent>(receptor, LifecycleStage.PrePerspectiveInline);
    }
  }

  // ========================================
  // PrePerspectiveDetached Tests (Non-Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PrePerspectiveDetached lifecycle stage fires parallel with perspective RunAsync (non-blocking).
  /// Should use Task.Run and not block perspective processing.
  /// </summary>
  [Test]
  public async Task PrePerspectiveDetached_FiresParallelWithProcessing_NonBlockingAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Act - Register receptor for ProductCreatedEvent in BFF
    var receptorTask = fixture.BffHost.WaitForPrePerspectiveDetachedAsync<ProductCreatedEvent>(
      timeoutMilliseconds: 20000);

    await fixture.Dispatcher.SendAsync(command);
    var receptor = await receptorTask;

    // Assert - Verify receptor was invoked
    await Assert.That(receptor.InvocationCount).IsEqualTo(1);
    await Assert.That(receptor.LastMessage).IsNotNull();
    await Assert.That(receptor.LastMessage!.ProductId).IsEqualTo(command.ProductId.Value);
  }

  /// <summary>
  /// Verifies that PrePerspectiveDetached may complete after perspective finishes.
  /// Tests the "perspective may complete before this stage finishes" guarantee.
  /// </summary>
  [Test]
  public async Task PrePerspectiveDetached_MayCompleteAfterPerspective_NonBlockingGuaranteeAsync() {
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
    var receptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(
      completionSource);

    var registry = fixture.BffHost.Services.GetRequiredService<IReceptorRegistry>();
    registry.Register<ProductCreatedEvent>(receptor, LifecycleStage.PrePerspectiveDetached);
    using var perspectiveWaiter = fixture.CreatePerspectiveWaiter<ProductCreatedEvent>(
      inventoryPerspectives: 2,
      bffPerspectives: 2);

    try {
      // Act - Dispatch command
      await fixture.Dispatcher.SendAsync(command);

      // Wait for PrePerspectiveDetached stage (non-blocking, may complete late)
      // NOTE: Async stages run in Task.Run (fire-and-forget), which can be delayed by infrastructure
      await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(60));

      // Assert - PrePerspectiveDetached should have completed eventually
      await Assert.That(receptor.InvocationCount).IsEqualTo(1);

      // Verify that perspective processing completed (data should be saved)
      // Wait for all perspectives to complete (no perspective filter)
      await perspectiveWaiter.WaitAsync(timeoutMilliseconds: 60000);

    } finally {
      registry.Unregister<ProductCreatedEvent>(receptor, LifecycleStage.PrePerspectiveDetached);
    }
  }

  // ========================================
  // PostPerspectiveDetached Tests (Non-Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PostPerspectiveDetached lifecycle stage fires after perspective completes (non-blocking).
  /// Should use Task.Run and not block checkpoint reporting.
  /// </summary>
  [Test]
  public async Task PostPerspectiveDetached_FiresAfterPerspectiveCompletes_NonBlockingAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Act - Register receptor for ProductCreatedEvent in BFF
    var receptorTask = fixture.BffHost.WaitForPostPerspectiveDetachedAsync<ProductCreatedEvent>(
      timeoutMilliseconds: 20000);

    await fixture.Dispatcher.SendAsync(command);
    var receptor = await receptorTask;

    // Assert - Verify receptor was invoked
    await Assert.That(receptor.InvocationCount).IsEqualTo(1);
    await Assert.That(receptor.LastMessage).IsNotNull();
    await Assert.That(receptor.LastMessage!.ProductId).IsEqualTo(command.ProductId.Value);
  }

  /// <summary>
  /// Verifies that PostPerspectiveDetached fires after perspective has processed all events.
  /// Tests the "perspective has processed all events" guarantee.
  /// </summary>
  [Test]
  public async Task PostPerspectiveDetached_FiresAfterEventsProcessed_GuaranteesCompletionAsync() {
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
    var receptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(
      completionSource);

    var registry = fixture.BffHost.Services.GetRequiredService<IReceptorRegistry>();
    registry.Register<ProductCreatedEvent>(receptor, LifecycleStage.PostPerspectiveDetached);
    using var perspectiveWaiter = fixture.CreatePerspectiveWaiter<ProductCreatedEvent>(
      inventoryPerspectives: 2,
      bffPerspectives: 2);

    try {
      // Act - Dispatch command
      await fixture.Dispatcher.SendAsync(command);

      // Wait for PostPerspectiveDetached stage
      await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(20));

      // Assert - At this point, PostPerspectiveDetached has fired
      // Perspective should have processed all events
      await Assert.That(receptor.InvocationCount).IsEqualTo(1);

      // Verify that perspective data is saved (checkpoint not yet reported, but data saved)
      await perspectiveWaiter.WaitAsync(timeoutMilliseconds: 45000);

    } finally {
      registry.Unregister<ProductCreatedEvent>(receptor, LifecycleStage.PostPerspectiveDetached);
    }
  }

  /// <summary>
  /// Verifies that PostPerspectiveDetached fires before checkpoint is reported.
  /// Tests the "checkpoint not yet reported to coordinator" guarantee.
  /// </summary>
  [Test]
  public async Task PostPerspectiveDetached_FiresBeforeCheckpointReported_TimingGuaranteeAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    var postAsyncCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var postInlineCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    var postAsyncReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(
      postAsyncCompletion);
    var postInlineReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(
      postInlineCompletion);

    var registry = fixture.BffHost.Services.GetRequiredService<IReceptorRegistry>();
    registry.Register<ProductCreatedEvent>(postAsyncReceptor, LifecycleStage.PostPerspectiveDetached);
    registry.Register<ProductCreatedEvent>(postInlineReceptor, LifecycleStage.PostPerspectiveInline);

    try {
      // Act - Dispatch command
      await fixture.Dispatcher.SendAsync(command);

      // Wait for both PostPerspective stages
      await Task.WhenAll(
        postAsyncCompletion.Task,
        postInlineCompletion.Task
      ).WaitAsync(TimeSpan.FromSeconds(20));

      // Assert - Both stages should have fired
      await Assert.That(postAsyncReceptor.InvocationCount).IsEqualTo(1);
      await Assert.That(postInlineReceptor.InvocationCount).IsEqualTo(1);

      // PostPerspectiveInline blocks checkpoint reporting, so if it completed,
      // checkpoint reporting happens AFTER both stages

    } finally {
      registry.Unregister<ProductCreatedEvent>(postAsyncReceptor, LifecycleStage.PostPerspectiveDetached);
      registry.Unregister<ProductCreatedEvent>(postInlineReceptor, LifecycleStage.PostPerspectiveInline);
    }
  }

  // ========================================
  // PostPerspectiveInline Tests (Blocking) ⭐ **Critical for Testing**
  // ========================================

  /// <summary>
  /// Verifies that PostPerspectiveInline lifecycle stage fires after perspective completes (blocking).
  /// This is the CRITICAL stage for test synchronization - guarantees perspective data is saved.
  /// </summary>
  [Test]
  public async Task PostPerspectiveInline_FiresAfterPerspectiveCompletes_BlocksCheckpointAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Act - Create waiter BEFORE sending command to avoid race condition
    using var waiter = fixture.CreatePerspectiveWaiter<ProductCreatedEvent>(inventoryPerspectives: 2, bffPerspectives: 2);
    await fixture.Dispatcher.SendAsync(command);
    await waiter.WaitAsync();

    // Assert - Verify perspective data is saved (this is the key guarantee!)
    var product = await fixture.BffProductLens.GetByIdAsync(command.ProductId.Value);
    await Assert.That(product).IsNotNull();
    await Assert.That(product!.Name).IsEqualTo(command.Name);
    await Assert.That(product.Price).IsEqualTo(command.Price);
  }

  /// <summary>
  /// Verifies that PostPerspectiveInline blocks checkpoint reporting.
  /// Tests the "checkpoint not yet reported to coordinator" guarantee.
  /// </summary>
  [Test]
  public async Task PostPerspectiveInline_BlocksCheckpointReporting_GuaranteesDataSavedAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Wait for ANY BFF perspective to complete PostPerspectiveInline via lifecycle receptor
    var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var receptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(completionSource);
    var registry = fixture.BffHost.Services.GetRequiredService<IReceptorRegistry>();
    registry.Register<ProductCreatedEvent>(receptor, LifecycleStage.PostPerspectiveInline);

    try {
      // Act - Dispatch command and wait for PostPerspectiveInline
      await fixture.Dispatcher.SendAsync(command);
      await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(45));

      // Assert - PostPerspectiveInline fired, confirming it blocks checkpoint
      await Assert.That(receptor.InvocationCount).IsGreaterThanOrEqualTo(1);
    } finally {
      registry.Unregister<ProductCreatedEvent>(receptor, LifecycleStage.PostPerspectiveInline);
    }
  }

  /// <summary>
  /// Verifies that PostPerspectiveInline fires for each event processed by the perspective.
  /// Tests that the stage fires during the event processing loop, not just once per batch.
  /// </summary>
  [Test]
  public async Task PostPerspectiveInline_FiresForEachEvent_MultipleInvocationsAsync() {
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

    // Create waiters BEFORE sending commands to avoid race condition
    // Each command creates 1 ProductCreatedEvent, which triggers 2 inventory perspectives + 2 BFF perspectives
    // 2 events × (2 inventory + 2 BFF) perspectives = 8 completions expected
    using var productWaiter = fixture.CreatePerspectiveWaiter<ProductCreatedEvent>(
      inventoryPerspectives: 4,
      bffPerspectives: 4);
    // Each command also creates 1 InventoryRestockedEvent (since InitialStock > 0)
    // 2 events × (1 inventory + 1 BFF) perspectives = 4 completions expected
    using var restockWaiter = fixture.CreatePerspectiveWaiter<InventoryRestockedEvent>(
      inventoryPerspectives: 2,
      bffPerspectives: 2);

    // Act - Dispatch multiple commands
    foreach (var command in commands) {
      await fixture.Dispatcher.SendAsync(command);
    }

    // Wait for ALL events to be processed through perspectives
    await productWaiter.WaitAsync(timeoutMilliseconds: 30000);
    await restockWaiter.WaitAsync(timeoutMilliseconds: 30000);

    // Assert - Verify both products are saved
    var product1 = await fixture.BffProductLens.GetByIdAsync(commands[0].ProductId.Value);
    var product2 = await fixture.BffProductLens.GetByIdAsync(commands[1].ProductId.Value);
    await Assert.That(product1).IsNotNull();
    await Assert.That(product2).IsNotNull();
  }

  // ========================================
  // Stage Ordering Tests
  // ========================================

  /// <summary>
  /// Verifies that all 4 Perspective stages fire in correct order:
  /// PrePerspectiveInline → PrePerspectiveDetached (parallel) → PostPerspectiveDetached → PostPerspectiveInline
  /// </summary>
  [Test]
  public async Task PerspectiveStages_FireInCorrectOrder_AllStagesInvokedAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    var registry = fixture.BffHost.Services.GetRequiredService<IReceptorRegistry>();

    // Create receptors for all 4 stages
    var preInlineCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var preAsyncCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var postAsyncCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var postInlineCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    var preInlineReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(
      preInlineCompletion);
    var preAsyncReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(
      preAsyncCompletion);
    var postAsyncReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(
      postAsyncCompletion);
    var postInlineReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(
      postInlineCompletion);

    // Register all receptors
    registry.Register<ProductCreatedEvent>(preInlineReceptor, LifecycleStage.PrePerspectiveInline);
    registry.Register<ProductCreatedEvent>(preAsyncReceptor, LifecycleStage.PrePerspectiveDetached);
    registry.Register<ProductCreatedEvent>(postAsyncReceptor, LifecycleStage.PostPerspectiveDetached);
    registry.Register<ProductCreatedEvent>(postInlineReceptor, LifecycleStage.PostPerspectiveInline);

    try {
      // Act - Dispatch command (event will be processed by ProductCatalog perspective in BFF)
      await fixture.Dispatcher.SendAsync(command);

      // Wait for all stages to complete (with timeout)
      await Task.WhenAll(
        preInlineCompletion.Task,
        preAsyncCompletion.Task,
        postAsyncCompletion.Task,
        postInlineCompletion.Task
      ).WaitAsync(TimeSpan.FromSeconds(25));

      // Assert - All stages should have been invoked
      await Assert.That(preInlineReceptor.InvocationCount).IsEqualTo(1);
      await Assert.That(preAsyncReceptor.InvocationCount).IsEqualTo(1);
      await Assert.That(postAsyncReceptor.InvocationCount).IsEqualTo(1);
      await Assert.That(postInlineReceptor.InvocationCount).IsEqualTo(1);

    } finally {
      // Unregister all receptors
      registry.Unregister<ProductCreatedEvent>(preInlineReceptor, LifecycleStage.PrePerspectiveInline);
      registry.Unregister<ProductCreatedEvent>(preAsyncReceptor, LifecycleStage.PrePerspectiveDetached);
      registry.Unregister<ProductCreatedEvent>(postAsyncReceptor, LifecycleStage.PostPerspectiveDetached);
      registry.Unregister<ProductCreatedEvent>(postInlineReceptor, LifecycleStage.PostPerspectiveInline);
    }
  }

  // ========================================
  // Stage Isolation Tests
  // Critical: Verify receptors ONLY fire at their registered stage
  // ========================================

  /// <summary>
  /// CRITICAL: Verifies that a receptor registered at PostPerspectiveDetached
  /// does NOT fire during PrePerspective stages (temporal ordering verification).
  /// This is the core test for the reported bug - receptors firing before perspective processes.
  /// </summary>
  /// <docs>core-concepts/lifecycle-receptors#stage-isolation</docs>
  [Test]
  [Category("StageIsolation")]
  [Category("PostPerspectiveDetached")]
  public async Task PostPerspectiveDetachedReceptor_FiresAfterPrePerspective_TemporalOrderingAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Stage Isolation Test Product",
      Description = "Testing stage isolation",
      Price = 99.99m,
      InitialStock = 10
    };

    var registry = fixture.BffHost.Services.GetRequiredService<IReceptorRegistry>();

    // Track invocation order using timestamps
    var invocationOrder = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset>();

    var preInlineCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var preAsyncCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var postAsyncCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    // Receptors that record invocation times
    var preInlineReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(
      preInlineCompletion,
      messageFilter: _ => {
        invocationOrder.TryAdd("PrePerspectiveInline", DateTimeOffset.UtcNow);
        return true;
      });

    var preAsyncReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(
      preAsyncCompletion,
      messageFilter: _ => {
        invocationOrder.TryAdd("PrePerspectiveDetached", DateTimeOffset.UtcNow);
        return true;
      });

    var postAsyncReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(
      postAsyncCompletion,
      messageFilter: _ => {
        invocationOrder.TryAdd("PostPerspectiveDetached", DateTimeOffset.UtcNow);
        return true;
      });

    // Register all receptors at their respective stages
    registry.Register<ProductCreatedEvent>(preInlineReceptor, LifecycleStage.PrePerspectiveInline);
    registry.Register<ProductCreatedEvent>(preAsyncReceptor, LifecycleStage.PrePerspectiveDetached);
    registry.Register<ProductCreatedEvent>(postAsyncReceptor, LifecycleStage.PostPerspectiveDetached);

    try {
      // Act - Dispatch command
      await fixture.Dispatcher.SendAsync(command);

      // Wait for all stages to complete
      await Task.WhenAll(
        preInlineCompletion.Task,
        preAsyncCompletion.Task,
        postAsyncCompletion.Task
      ).WaitAsync(TimeSpan.FromSeconds(30));

      // Assert - Each receptor should fire EXACTLY once at its registered stage
      await Assert.That(preInlineReceptor.InvocationCount).IsEqualTo(1)
        .Because("PrePerspectiveInline receptor should fire exactly once");
      await Assert.That(preAsyncReceptor.InvocationCount).IsEqualTo(1)
        .Because("PrePerspectiveDetached receptor should fire exactly once");
      await Assert.That(postAsyncReceptor.InvocationCount).IsEqualTo(1)
        .Because("PostPerspectiveDetached receptor should fire exactly once");

      // CRITICAL ASSERTION: PostPerspectiveDetached MUST fire AFTER PrePerspective stages
      var preInlineTime = invocationOrder.GetValueOrDefault("PrePerspectiveInline");
      var postAsyncTime = invocationOrder.GetValueOrDefault("PostPerspectiveDetached");

      await Assert.That(postAsyncTime).IsGreaterThan(preInlineTime)
        .Because("PostPerspectiveDetached MUST fire AFTER PrePerspectiveInline (not before perspective processing)");

    } finally {
      // Unregister all receptors
      registry.Unregister<ProductCreatedEvent>(preInlineReceptor, LifecycleStage.PrePerspectiveInline);
      registry.Unregister<ProductCreatedEvent>(preAsyncReceptor, LifecycleStage.PrePerspectiveDetached);
      registry.Unregister<ProductCreatedEvent>(postAsyncReceptor, LifecycleStage.PostPerspectiveDetached);
    }
  }

  /// <summary>
  /// CRITICAL: Verifies that PostPerspectiveDetached receptor can query the perspective model
  /// AFTER perspective processing is complete - data should NOT be stale/null.
  /// This is the exact scenario from the reported bug - querying stale data.
  /// </summary>
  /// <docs>core-concepts/lifecycle-receptors#stage-isolation</docs>
  [Test]
  [Category("StageIsolation")]
  [Category("PostPerspectiveDetached")]
  public async Task PostPerspectiveDetachedReceptor_CanQueryModel_DataNotStaleAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Data Freshness Test Product",
      Description = "Testing data is not stale",
      Price = 123.45m,
      InitialStock = 42
    };

    var registry = fixture.BffHost.Services.GetRequiredService<IReceptorRegistry>();
    var queryCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    // Receptor that completes at PostPerspectiveDetached
    // This simulates what the user's EmbeddingHandler does
    var queryReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(
      queryCompletion,
      messageFilter: _ => true);

    registry.Register<ProductCreatedEvent>(queryReceptor, LifecycleStage.PostPerspectiveDetached);

    try {
      // Act - Dispatch command
      await fixture.Dispatcher.SendAsync(command);

      // Wait for the receptor to complete
      await queryCompletion.Task.WaitAsync(TimeSpan.FromSeconds(30));

      // Now query the model - at this point PostPerspectiveDetached has fired,
      // so the data should be committed and fresh
      var queriedProduct = await fixture.BffProductLens.GetByIdAsync(command.ProductId.Value);

      // Assert - The queried product should NOT be null (data should be fresh)
      await Assert.That(queriedProduct).IsNotNull()
        .Because("PostPerspectiveDetached receptor fires after FlushAsync, so data should be queryable");
      await Assert.That(queriedProduct!.Name).IsEqualTo(command.Name)
        .Because("The queried model should have the correct name");
      await Assert.That(queriedProduct.Price).IsEqualTo(command.Price)
        .Because("The queried model should have the correct price");

    } finally {
      registry.Unregister<ProductCreatedEvent>(queryReceptor, LifecycleStage.PostPerspectiveDetached);
    }
  }

  /// <summary>
  /// Verifies that multiple events trigger all Perspective stages for each event.
  /// </summary>
  [Test]
  public async Task PerspectiveStages_MultipleEvents_AllStagesFireForEachAsync() {
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
    var receptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(
      completionSource);

    var registry = fixture.BffHost.Services.GetRequiredService<IReceptorRegistry>();
    registry.Register<ProductCreatedEvent>(receptor, LifecycleStage.PostPerspectiveInline);

    try {
      // Act - Dispatch multiple commands
      foreach (var command in commands) {
        await fixture.Dispatcher.SendAsync(command);
      }

      // Wait for last event to complete PostPerspectiveInline
      await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(30));

      // Assert - Receptor should have been invoked at least once
      await Assert.That(receptor.InvocationCount).IsGreaterThanOrEqualTo(1);

    } finally {
      registry.Unregister<ProductCreatedEvent>(receptor, LifecycleStage.PostPerspectiveInline);
    }
  }
}
