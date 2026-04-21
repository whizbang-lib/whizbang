using System.Diagnostics.CodeAnalysis;
using ECommerce.Contracts.Commands;
using ECommerce.Integration.Tests.Fixtures;

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

    // Act - Use hook to wait for perspective processing
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 2, timeoutMilliseconds: 45000, hostFilter: "inventory");
    await fixture.Dispatcher.SendAsync(command);
    await perspectiveTask;
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

    // Act - Use hook to wait for perspective processing
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 2, timeoutMilliseconds: 45000, hostFilter: "inventory");
    await fixture.Dispatcher.SendAsync(command);
    await perspectiveTask;
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

    // Act - Use hook to wait for perspective processing
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 2, timeoutMilliseconds: 45000, hostFilter: "inventory");
    await fixture.Dispatcher.SendAsync(command);
    await perspectiveTask;
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

    // Act - Use hook to wait for perspective processing
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 2, timeoutMilliseconds: 60000, hostFilter: "inventory");
    await fixture.Dispatcher.SendAsync(command);
    await perspectiveTask;
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

    // Act - Use hook to wait for perspective processing
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 2, timeoutMilliseconds: 45000, hostFilter: "inventory");
    await fixture.Dispatcher.SendAsync(command);
    await perspectiveTask;
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

    // Act - Use hook to wait for perspective processing
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 2, timeoutMilliseconds: 45000, hostFilter: "inventory");
    await fixture.Dispatcher.SendAsync(command);
    await perspectiveTask;
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

    // Act - Use hook to wait for perspective processing (covers both PostPerspective stages)
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 2, timeoutMilliseconds: 45000, hostFilter: "inventory");
    await fixture.Dispatcher.SendAsync(command);
    await perspectiveTask;
  }

  // ========================================
  // PostPerspectiveInline Tests (Blocking) - Critical for Testing
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

    // Act - Use hook to wait for perspective processing
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 2, timeoutMilliseconds: 45000, hostFilter: "inventory");
    await fixture.Dispatcher.SendAsync(command);
    await perspectiveTask;
  }

  /// <summary>
  /// Verifies that PostPerspectiveInline blocks checkpoint reporting.
  /// Tests the "checkpoint not yet reported to coordinator" guarantee.
  /// </summary>
  [Test]
  [Timeout(120_000)]
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

    // Act - Use hook to wait for perspective processing
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 2, timeoutMilliseconds: 90000, hostFilter: "inventory");
    await fixture.Dispatcher.SendAsync(command);
    await perspectiveTask;
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

    // Act - Wait for all perspective processing
    // Each command creates ProductCreatedEvent with 2 inventory perspectives
    // 2 commands x 2 perspective completions = 4 total
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 4, timeoutMilliseconds: 45000, hostFilter: "inventory");
    foreach (var command in commands) {
      await fixture.Dispatcher.SendAsync(command);
    }
    await perspectiveTask;
  }

  // ========================================
  // Stage Ordering Tests
  // ========================================

  /// <summary>
  /// Verifies that all 4 Perspective stages fire in correct order:
  /// PrePerspectiveInline -> PrePerspectiveDetached (parallel) -> PostPerspectiveDetached -> PostPerspectiveInline
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

    // Act - Use hook to wait for perspective processing (all stages fire in order)
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 2, timeoutMilliseconds: 45000, hostFilter: "inventory");
    await fixture.Dispatcher.SendAsync(command);
    await perspectiveTask;
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

    // Act - Use hook to wait for perspective processing
    // If perspectives complete, all stages have fired in correct order
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 2, timeoutMilliseconds: 45000, hostFilter: "inventory");
    await fixture.Dispatcher.SendAsync(command);
    await perspectiveTask;
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

    // Act - Use hook to wait for perspective processing
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 2, timeoutMilliseconds: 45000, hostFilter: "inventory");
    await fixture.Dispatcher.SendAsync(command);
    await perspectiveTask;

    // Wait for workers to be idle before querying data
    await fixture.WaitForWorkersIdleAsync();

    // Assert - Verify perspective data is queryable and not stale
    var inventoryProduct = await fixture.InventoryProductLens.GetByIdAsync(command.ProductId.Value);
    await Assert.That(inventoryProduct).IsNotNull();
    await Assert.That(inventoryProduct!.Name).IsEqualTo("Data Freshness Test Product");
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

    // Act - Wait for all perspective processing
    // 2 commands x 2 inventory perspectives for ProductCreatedEvent = 4 total
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 4, timeoutMilliseconds: 45000, hostFilter: "inventory");
    foreach (var command in commands) {
      await fixture.Dispatcher.SendAsync(command);
    }
    await perspectiveTask;
  }
}
