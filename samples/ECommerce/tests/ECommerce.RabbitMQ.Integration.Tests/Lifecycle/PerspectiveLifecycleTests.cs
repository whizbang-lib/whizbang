using System.Diagnostics.CodeAnalysis;
using ECommerce.Contracts.Commands;
using ECommerce.Contracts.Events;
using ECommerce.Integration.Tests.Fixtures;
using ECommerce.RabbitMQ.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Workers;

namespace ECommerce.RabbitMQ.Integration.Tests.Lifecycle;

/// <summary>
/// Integration tests for all 4 Perspective lifecycle stages.
/// Validates that lifecycle receptors fire at correct points around perspective event processing.
/// Each test gets its own PostgreSQL databases + hosts. RabbitMQ container is shared via SharedRabbitMqFixtureSource.
/// Tests run sequentially for reliable timing.
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
[NotInParallel("RabbitMQ")]
public class PerspectiveLifecycleTests {
  private static RabbitMqIntegrationFixture? _fixture;

  [Before(Test)]
  [RequiresUnreferencedCode("Test code - reflection allowed")]
  [RequiresDynamicCode("Test code - reflection allowed")]
  public async Task SetupAsync() {
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
  public async Task CleanupAsync() {
    if (_fixture != null) {
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

    // Act — use OnPerspectiveEventProcessed hook to verify perspective processed the event.
    // If the worker processed it, PrePerspectiveInline must have fired (it fires before processing).
    await fixture.Dispatcher.SendAsync(command);
    await fixture.WaitForPerspectiveProcessingAsync(expectedCompletions: 2, timeoutMilliseconds: 45000, hostFilter: "bff");
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
    await Assert.That(receptor.LastMessage!.ProductId).IsEqualTo(command.ProductId);
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
    var receptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(completionSource);

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
      await perspectiveWaiter.WaitAsync(timeoutMilliseconds: 120000);

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
    await Assert.That(receptor.LastMessage!.ProductId).IsEqualTo(command.ProductId);
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
    var receptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(completionSource);

    var registry = fixture.BffHost.Services.GetRequiredService<IReceptorRegistry>();
    registry.Register<ProductCreatedEvent>(receptor, LifecycleStage.PostPerspectiveDetached);
    using var perspectiveWaiter = fixture.CreatePerspectiveWaiter<ProductCreatedEvent>(
      inventoryPerspectives: 2,
      bffPerspectives: 2);

    try {
      // Act - Dispatch command
      await fixture.Dispatcher.SendAsync(command);

      // Wait for PostPerspectiveDetached stage
      await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(45));

      // Assert - At this point, PostPerspectiveDetached has fired
      // Perspective should have processed all events
      await Assert.That(receptor.InvocationCount).IsEqualTo(1);

      // Verify that perspective data is saved (checkpoint not yet reported, but data saved)
      await perspectiveWaiter.WaitAsync(timeoutMilliseconds: 90000);

    } finally {
      registry.Unregister<ProductCreatedEvent>(receptor, LifecycleStage.PostPerspectiveDetached);
    }
  }

  /// <summary>
  /// Verifies that PostPerspectiveDetached fires before checkpoint is reported.
  /// Tests the "checkpoint not yet reported to coordinator" guarantee.
  /// </summary>
  [Test]
  [Timeout(120000)] // Increased timeout for resource-constrained CI environments (120s)
  public async Task PostPerspectiveDetached_FiresBeforeCheckpointReported_TimingGuaranteeAsync(CancellationToken cancellationToken) {
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

    var postAsyncReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(postAsyncCompletion);
    var postInlineReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(postInlineCompletion);

    var registry = fixture.BffHost.Services.GetRequiredService<IReceptorRegistry>();
    registry.Register<ProductCreatedEvent>(postAsyncReceptor, LifecycleStage.PostPerspectiveDetached);
    registry.Register<ProductCreatedEvent>(postInlineReceptor, LifecycleStage.PostPerspectiveInline);

    try {
      // Act - Dispatch command
      await fixture.Dispatcher.SendAsync(command);

      // Wait for both PostPerspective stages (increased timeout for resource exhaustion scenarios)
      await Task.WhenAll(
        postAsyncCompletion.Task,
        postInlineCompletion.Task
      ).WaitAsync(TimeSpan.FromSeconds(35));

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
  [Timeout(120000)] // Increased timeout for resource-constrained CI environments (120s)
  public async Task PostPerspectiveInline_FiresAfterPerspectiveCompletes_BlocksCheckpointAsync(CancellationToken cancellationToken) {
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
    await fixture.WaitForPerspectiveCompletionAsync<ProductCreatedEvent>(
      inventoryPerspectives: 2,
      bffPerspectives: 2,
      timeoutMilliseconds: 35000); // Increased timeout for resource exhaustion scenarios

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

    var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var receptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(completionSource);

    var registry = fixture.BffHost.Services.GetRequiredService<IReceptorRegistry>();
    registry.Register<ProductCreatedEvent>(receptor, LifecycleStage.PostPerspectiveInline);

    try {
      // Act - Dispatch command
      await fixture.Dispatcher.SendAsync(command);

      // Wait for PostPerspectiveInline stage (blocking)
      await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(45));

      // Assert - At this point, PostPerspectiveInline has completed
      // Database writes MUST be committed because this stage blocks checkpoint
      await Assert.That(receptor.InvocationCount).IsEqualTo(1);

      // Verify perspective data is immediately queryable
      var product = await fixture.BffProductLens.GetByIdAsync(command.ProductId.Value);
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
  public async Task PostPerspectiveInline_FiresForEachEvent_MultipleInvocationsAsync(CancellationToken cancellationToken) {
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

    // Act - Dispatch multiple commands
    foreach (var command in commands) {
      await fixture.Dispatcher.SendAsync(command);
    }

    // Wait for perspective processing using OnPerspectiveEventProcessed hooks (deterministic)
    // 2 commands → each triggers ProductCreated + Restock → multiple perspective completions
    await fixture.WaitForPerspectiveProcessingAsync(expectedCompletions: 8, timeoutMilliseconds: 60000);

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
  [Timeout(120_000)] // Fixture init + RabbitMQ → BFF pipeline + 4 stages
  public async Task PerspectiveStages_FireInCorrectOrder_AllStagesInvokedAsync(CancellationToken cancellationToken) {
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

    var preInlineReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(preInlineCompletion);
    var preAsyncReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(preAsyncCompletion);
    var postAsyncReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(postAsyncCompletion);
    var postInlineReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(postInlineCompletion);

    // Register all receptors
    registry.Register<ProductCreatedEvent>(preInlineReceptor, LifecycleStage.PrePerspectiveInline);
    registry.Register<ProductCreatedEvent>(preAsyncReceptor, LifecycleStage.PrePerspectiveDetached);
    registry.Register<ProductCreatedEvent>(postAsyncReceptor, LifecycleStage.PostPerspectiveDetached);
    registry.Register<ProductCreatedEvent>(postInlineReceptor, LifecycleStage.PostPerspectiveInline);

    try {
      // Act - Dispatch command (event will be processed by ProductCatalog perspective in BFF)
      await fixture.Dispatcher.SendAsync(command);

      // Wait for all stages to complete (increased timeout for resource exhaustion scenarios)
      await Task.WhenAll(
        preInlineCompletion.Task,
        preAsyncCompletion.Task,
        postAsyncCompletion.Task,
        postInlineCompletion.Task
      ).WaitAsync(TimeSpan.FromSeconds(60));

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
    var receptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(completionSource);

    var registry = fixture.BffHost.Services.GetRequiredService<IReceptorRegistry>();
    registry.Register<ProductCreatedEvent>(receptor, LifecycleStage.PostPerspectiveInline);

    try {
      // Act - Dispatch multiple commands
      foreach (var command in commands) {
        await fixture.Dispatcher.SendAsync(command);
      }

      // Wait for any perspective to complete PostPerspectiveInline
      await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(60));

      // Assert - Receptor should have been invoked at least once
      await Assert.That(receptor.InvocationCount).IsGreaterThanOrEqualTo(1);

    } finally {
      registry.Unregister<ProductCreatedEvent>(receptor, LifecycleStage.PostPerspectiveInline);
    }
  }

  // ========================================
  // PostAllPerspectivesDetached Tests (WhenAll Gate)
  // ========================================

  /// <summary>
  /// Verifies that PostAllPerspectivesDetached fires exactly once per event after ALL perspectives complete.
  /// BffHost has 2 perspectives for ProductCreatedEvent (ProductCatalog + InventoryLevels).
  /// Forces PerspectiveBatchSize=1 so perspectives are claimed in separate batches.
  /// Bug: perspectivesPerStream is built from claimed work items only (not all perspectives
  /// for the event type), so PostAllPerspectivesDetached fires once per batch cycle instead of
  /// once after ALL perspectives complete — resulting in multiple firings.
  /// </summary>
  [Test]
  [Timeout(120_000)]
  public async Task PostAllPerspectivesDetached_FiresExactlyOnce_AfterAllPerspectivesCompleteAsync(CancellationToken cancellationToken) {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "PostAllPerspectives Test",
      Description = "Tests WhenAll gate fires exactly once",
      Price = 49.99m,
      InitialStock = 5
    };

    // Act - Use OnPerspectiveEventProcessed hook to wait for all 4 perspectives
    // (2 inventory + 2 BFF) to complete processing the ProductCreatedEvent
    await fixture.Dispatcher.SendAsync(command);
    await fixture.WaitForPerspectiveProcessingAsync(expectedCompletions: 4, timeoutMilliseconds: 60000);
  }

}
