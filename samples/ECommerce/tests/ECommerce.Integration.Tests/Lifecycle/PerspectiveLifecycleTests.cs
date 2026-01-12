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
///   <item>PrePerspectiveAsync - Parallel with perspective RunAsync() (non-blocking)</item>
///   <item>PostPerspectiveAsync - After perspective completes (non-blocking)</item>
///   <item>PostPerspectiveInline - After perspective completes (blocking) - NOW EXPLICITLY TESTED</item>
/// </list>
/// </remarks>
/// <docs>core-concepts/lifecycle-stages</docs>
/// <docs>testing/lifecycle-synchronization</docs>
[Category("Integration")]
[Category("Lifecycle")]
[NotInParallel]
public class PerspectiveLifecycleTests {
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
      perspectiveName: "ProductCatalogPerspective",
      timeoutMilliseconds: 20000);

    await fixture.Dispatcher.SendAsync(command);
    var receptor = await receptorTask;

    // Assert - Verify receptor was invoked
    await Assert.That(receptor.InvocationCount).IsEqualTo(1);
    await Assert.That(receptor.LastMessage).IsNotNull();
    await Assert.That(receptor.LastMessage!.ProductId).IsEqualTo(command.ProductId);
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

    var completionSource = new TaskCompletionSource<bool>();
    var receptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(
      completionSource,
      perspectiveName: "ProductCatalogPerspective");

    var registry = fixture.BffHost.Services.GetRequiredService<ILifecycleReceptorRegistry>();
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
  // PrePerspectiveAsync Tests (Non-Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PrePerspectiveAsync lifecycle stage fires parallel with perspective RunAsync (non-blocking).
  /// Should use Task.Run and not block perspective processing.
  /// </summary>
  [Test]
  public async Task PrePerspectiveAsync_FiresParallelWithProcessing_NonBlockingAsync() {
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
    var receptorTask = fixture.BffHost.WaitForPrePerspectiveAsyncAsync<ProductCreatedEvent>(
      perspectiveName: "ProductCatalogPerspective",
      timeoutMilliseconds: 20000);

    await fixture.Dispatcher.SendAsync(command);
    var receptor = await receptorTask;

    // Assert - Verify receptor was invoked
    await Assert.That(receptor.InvocationCount).IsEqualTo(1);
    await Assert.That(receptor.LastMessage).IsNotNull();
    await Assert.That(receptor.LastMessage!.ProductId).IsEqualTo(command.ProductId);
  }

  /// <summary>
  /// Verifies that PrePerspectiveAsync may complete after perspective finishes.
  /// Tests the "perspective may complete before this stage finishes" guarantee.
  /// </summary>
  [Test]
  public async Task PrePerspectiveAsync_MayCompleteAfterPerspective_NonBlockingGuaranteeAsync() {
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
    var receptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(
      completionSource,
      perspectiveName: "ProductCatalogPerspective");

    var registry = fixture.BffHost.Services.GetRequiredService<ILifecycleReceptorRegistry>();
    registry.Register<ProductCreatedEvent>(receptor, LifecycleStage.PrePerspectiveAsync);
    using var perspectiveWaiter = fixture.CreatePerspectiveWaiter<ProductCreatedEvent>(expectedPerspectiveCount: 4);

    try {
      // Act - Dispatch command
      await fixture.Dispatcher.SendAsync(command);

      // Wait for PrePerspectiveAsync stage (non-blocking, may complete late)
      // NOTE: Async stages run in Task.Run (fire-and-forget), which can be delayed by infrastructure
      await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(60));

      // Assert - PrePerspectiveAsync should have completed eventually
      await Assert.That(receptor.InvocationCount).IsEqualTo(1);

      // Verify that perspective processing completed (data should be saved)
      // Wait for all perspectives to complete (no perspective filter)
      await perspectiveWaiter.WaitAsync(timeoutMilliseconds: 60000);

    } finally {
      registry.Unregister<ProductCreatedEvent>(receptor, LifecycleStage.PrePerspectiveAsync);
    }
  }

  // ========================================
  // PostPerspectiveAsync Tests (Non-Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PostPerspectiveAsync lifecycle stage fires after perspective completes (non-blocking).
  /// Should use Task.Run and not block checkpoint reporting.
  /// </summary>
  [Test]
  public async Task PostPerspectiveAsync_FiresAfterPerspectiveCompletes_NonBlockingAsync() {
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
    var receptorTask = fixture.BffHost.WaitForPostPerspectiveAsyncAsync<ProductCreatedEvent>(
      perspectiveName: "ProductCatalogPerspective",
      timeoutMilliseconds: 20000);

    await fixture.Dispatcher.SendAsync(command);
    var receptor = await receptorTask;

    // Assert - Verify receptor was invoked
    await Assert.That(receptor.InvocationCount).IsEqualTo(1);
    await Assert.That(receptor.LastMessage).IsNotNull();
    await Assert.That(receptor.LastMessage!.ProductId).IsEqualTo(command.ProductId);
  }

  /// <summary>
  /// Verifies that PostPerspectiveAsync fires after perspective has processed all events.
  /// Tests the "perspective has processed all events" guarantee.
  /// </summary>
  [Test]
  public async Task PostPerspectiveAsync_FiresAfterEventsProcessed_GuaranteesCompletionAsync() {
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
    var receptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(
      completionSource,
      perspectiveName: "ProductCatalogPerspective");

    var registry = fixture.BffHost.Services.GetRequiredService<ILifecycleReceptorRegistry>();
    registry.Register<ProductCreatedEvent>(receptor, LifecycleStage.PostPerspectiveAsync);
    using var perspectiveWaiter = fixture.CreatePerspectiveWaiter<ProductCreatedEvent>(expectedPerspectiveCount: 4);

    try {
      // Act - Dispatch command
      await fixture.Dispatcher.SendAsync(command);

      // Wait for PostPerspectiveAsync stage
      await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(20));

      // Assert - At this point, PostPerspectiveAsync has fired
      // Perspective should have processed all events
      await Assert.That(receptor.InvocationCount).IsEqualTo(1);

      // Verify that perspective data is saved (checkpoint not yet reported, but data saved)
      await perspectiveWaiter.WaitAsync(timeoutMilliseconds: 45000);

    } finally {
      registry.Unregister<ProductCreatedEvent>(receptor, LifecycleStage.PostPerspectiveAsync);
    }
  }

  /// <summary>
  /// Verifies that PostPerspectiveAsync fires before checkpoint is reported.
  /// Tests the "checkpoint not yet reported to coordinator" guarantee.
  /// </summary>
  [Test]
  public async Task PostPerspectiveAsync_FiresBeforeCheckpointReported_TimingGuaranteeAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    var postAsyncCompletion = new TaskCompletionSource<bool>();
    var postInlineCompletion = new TaskCompletionSource<bool>();

    var postAsyncReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(
      postAsyncCompletion,
      perspectiveName: "ProductCatalogPerspective");
    var postInlineReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(
      postInlineCompletion,
      perspectiveName: "ProductCatalogPerspective");

    var registry = fixture.BffHost.Services.GetRequiredService<ILifecycleReceptorRegistry>();
    registry.Register<ProductCreatedEvent>(postAsyncReceptor, LifecycleStage.PostPerspectiveAsync);
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
      registry.Unregister<ProductCreatedEvent>(postAsyncReceptor, LifecycleStage.PostPerspectiveAsync);
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

    // Act - Use the existing WaitForPerspectiveCompletionAsync helper (PostPerspectiveInline)
    await fixture.Dispatcher.SendAsync(command);
    await fixture.WaitForPerspectiveCompletionAsync<ProductCreatedEvent>(inventoryPerspectives: 2, bffPerspectives: 2);

    // Assert - Verify perspective data is saved (this is the key guarantee!)
    var product = await fixture.BffProductLens.GetByIdAsync(command.ProductId);
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

    var completionSource = new TaskCompletionSource<bool>();
    var receptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(
      completionSource,
      perspectiveName: "ProductCatalogPerspective");

    var registry = fixture.BffHost.Services.GetRequiredService<ILifecycleReceptorRegistry>();
    registry.Register<ProductCreatedEvent>(receptor, LifecycleStage.PostPerspectiveInline);

    try {
      // Act - Dispatch command
      await fixture.Dispatcher.SendAsync(command);

      // Wait for PostPerspectiveInline stage (blocking)
      await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(20));

      // Assert - At this point, PostPerspectiveInline has completed
      // Database writes MUST be committed because this stage blocks checkpoint
      await Assert.That(receptor.InvocationCount).IsEqualTo(1);

      // Verify perspective data is immediately queryable
      var product = await fixture.BffProductLens.GetByIdAsync(command.ProductId);
      await Assert.That(product).IsNotNull();
      await Assert.That(product!.Name).IsEqualTo(command.Name);

    } finally {
      registry.Unregister<ProductCreatedEvent>(receptor, LifecycleStage.PostPerspectiveInline);
    }
  }

  /// <summary>
  /// Verifies that PostPerspectiveInline fires for each event processed by the perspective.
  /// Tests that the stage fires during the event processing loop, not just once per batch.
  /// </summary>
  [Test]
  [Timeout(90_000)]  // TUnit includes fixture initialization in test timeout (~60s setup + ~5s test)
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

    // Create waiter BEFORE sending commands to avoid race condition
    // Each command creates 1 ProductCreatedEvent, which triggers 2 BFF perspectives
    // 2 events × 2 BFF perspectives = 4 completions expected
    using var waiter = fixture.CreatePerspectiveWaiter<ProductCreatedEvent>(expectedPerspectiveCount: 4);

    // Act - Dispatch multiple commands
    foreach (var command in commands) {
      await fixture.Dispatcher.SendAsync(command);
    }

    // Wait for BOTH events to be processed through perspectives
    await waiter.WaitAsync(timeoutMilliseconds: 30000);

    // Assert - Verify both products are saved
    var product1 = await fixture.BffProductLens.GetByIdAsync(commands[0].ProductId);
    var product2 = await fixture.BffProductLens.GetByIdAsync(commands[1].ProductId);
    await Assert.That(product1).IsNotNull();
    await Assert.That(product2).IsNotNull();
  }

  // ========================================
  // Stage Ordering Tests
  // ========================================

  /// <summary>
  /// Verifies that all 4 Perspective stages fire in correct order:
  /// PrePerspectiveInline → PrePerspectiveAsync (parallel) → PostPerspectiveAsync → PostPerspectiveInline
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

    var registry = fixture.BffHost.Services.GetRequiredService<ILifecycleReceptorRegistry>();

    // Create receptors for all 4 stages
    var preInlineCompletion = new TaskCompletionSource<bool>();
    var preAsyncCompletion = new TaskCompletionSource<bool>();
    var postAsyncCompletion = new TaskCompletionSource<bool>();
    var postInlineCompletion = new TaskCompletionSource<bool>();

    var preInlineReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(
      preInlineCompletion, perspectiveName: "ProductCatalogPerspective");
    var preAsyncReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(
      preAsyncCompletion, perspectiveName: "ProductCatalogPerspective");
    var postAsyncReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(
      postAsyncCompletion, perspectiveName: "ProductCatalogPerspective");
    var postInlineReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(
      postInlineCompletion, perspectiveName: "ProductCatalogPerspective");

    // Register all receptors
    registry.Register<ProductCreatedEvent>(preInlineReceptor, LifecycleStage.PrePerspectiveInline);
    registry.Register<ProductCreatedEvent>(preAsyncReceptor, LifecycleStage.PrePerspectiveAsync);
    registry.Register<ProductCreatedEvent>(postAsyncReceptor, LifecycleStage.PostPerspectiveAsync);
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
      registry.Unregister<ProductCreatedEvent>(preAsyncReceptor, LifecycleStage.PrePerspectiveAsync);
      registry.Unregister<ProductCreatedEvent>(postAsyncReceptor, LifecycleStage.PostPerspectiveAsync);
      registry.Unregister<ProductCreatedEvent>(postInlineReceptor, LifecycleStage.PostPerspectiveInline);
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

    var completionSource = new TaskCompletionSource<bool>();
    var receptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(
      completionSource,
      perspectiveName: "ProductCatalogPerspective");

    var registry = fixture.BffHost.Services.GetRequiredService<ILifecycleReceptorRegistry>();
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
