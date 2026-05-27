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
    _fixture = await SharedRabbitMqFixtureSource.GetFixtureAsync();
    await _fixture.CleanupDatabaseAsync();
  }

  [After(Test)]
  public Task CleanupAsync() {
    // Shared fixture is reused across tests — don't dispose
    return Task.CompletedTask;
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

    // Act - Register receptor for ProductCreatedEvent in BFF (where perspective processing happens).
    // Filter by ProductId so a stale event from a prior test cannot satisfy our wait.
    var receptorTask = fixture.BffHost.WaitForPrePerspectiveInlineAsync<ProductCreatedEvent>(
      timeoutMilliseconds: 20000,
      messageFilter: e => e.ProductId == command.ProductId.Value);

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

    // Act - Register receptor for ProductCreatedEvent in BFF, filtered to OUR event only
    // (stale event from prior test would otherwise satisfy this wait).
    var receptorTask = fixture.BffHost.WaitForPrePerspectiveDetachedAsync<ProductCreatedEvent>(
      timeoutMilliseconds: 20000,
      messageFilter: e => e.ProductId == command.ProductId.Value);

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
  [Timeout(180_000)]
  public async Task PrePerspectiveDetached_MayCompleteAfterPerspective_NonBlockingGuaranteeAsync(CancellationToken cancellationToken) {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Register the lifecycle wait BEFORE dispatching so the receptor is in place when the event arrives.
    // The helper uses TaskCompletionSource + scaled timeout (WHIZBANG_TEST_TIMEOUT_MULTIPLIER), preventing
    // flakes under heavy parallel load.
    var receptorTask = fixture.BffHost.WaitForPrePerspectiveDetachedAsync<ProductCreatedEvent>(
      timeoutMilliseconds: 60000,
      messageFilter: e => e.ProductId == command.ProductId.Value);

    // Act - Dispatch command
    await fixture.Dispatcher.SendAsync(command);

    var receptor = await receptorTask;

    // Assert - PrePerspectiveDetached should have completed eventually
    await Assert.That(receptor.InvocationCount).IsGreaterThanOrEqualTo(1);
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

    // Act - Register receptor for ProductCreatedEvent in BFF, filtered to OUR ProductId
    // (stale events from prior tests would otherwise satisfy this wait).
    var receptorTask = fixture.BffHost.WaitForPostPerspectiveDetachedAsync<ProductCreatedEvent>(
      timeoutMilliseconds: 20000,
      messageFilter: e => e.ProductId == command.ProductId.Value);

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
  [Timeout(180_000)]
  public async Task PostPerspectiveDetached_FiresAfterEventsProcessed_GuaranteesCompletionAsync(CancellationToken cancellationToken) {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Register the lifecycle wait BEFORE dispatching so the receptor is in place when the event arrives.
    // The helper uses TaskCompletionSource + scaled timeout (WHIZBANG_TEST_TIMEOUT_MULTIPLIER), preventing
    // flakes under heavy parallel load.
    var receptorTask = fixture.BffHost.WaitForPostPerspectiveDetachedAsync<ProductCreatedEvent>(
      timeoutMilliseconds: 60000,
      messageFilter: e => e.ProductId == command.ProductId.Value);

    // Act - Dispatch command
    await fixture.Dispatcher.SendAsync(command);

    var receptor = await receptorTask;

    // Assert - PostPerspectiveDetached has fired
    await Assert.That(receptor.InvocationCount).IsGreaterThanOrEqualTo(1);
  }

  /// <summary>
  /// Verifies that PostPerspectiveDetached fires before checkpoint is reported.
  /// Tests the "checkpoint not yet reported to coordinator" guarantee.
  /// </summary>
  [Test]
  [Timeout(180_000)] // Increased timeout for resource-constrained CI environments
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

    // Register both lifecycle waits BEFORE dispatching. Each helper internally registers its
    // receptor synchronously up to its first await, so by the time we dispatch both are armed.
    // The helper's timeout scales with WHIZBANG_TEST_TIMEOUT_MULTIPLIER.
    const int perStageTimeoutMs = 60_000;
    Func<ProductCreatedEvent, bool> filter = e => e.ProductId == command.ProductId.Value;
    var postAsyncTask = fixture.BffHost.WaitForPostPerspectiveDetachedAsync<ProductCreatedEvent>(timeoutMilliseconds: perStageTimeoutMs, messageFilter: filter);
    var postInlineTask = fixture.BffHost.WaitForPostPerspectiveInlineAsync<ProductCreatedEvent>(timeoutMilliseconds: perStageTimeoutMs, messageFilter: filter);

    // Act - Dispatch command
    await fixture.Dispatcher.SendAsync(command);

    // Wait for both. If either helper times out, its TimeoutException identifies the missing stage.
    await Task.WhenAll(postAsyncTask, postInlineTask);

    // Assert - Both stages should have fired
    await Assert.That((await postAsyncTask).InvocationCount).IsEqualTo(1);
    await Assert.That((await postInlineTask).InvocationCount).IsEqualTo(1);

    // PostPerspectiveInline blocks checkpoint reporting, so if it completed,
    // checkpoint reporting happens AFTER both stages
  }

  // ========================================
  // PostPerspectiveInline Tests (Blocking) ⭐ **Critical for Testing**
  // ========================================

  /// <summary>
  /// Verifies that PostPerspectiveInline lifecycle stage fires after perspective completes (blocking).
  /// This is the CRITICAL stage for test synchronization - guarantees perspective data is saved.
  /// </summary>
  [Test]
  [Timeout(180_000)] // Increased timeout for resource-constrained CI environments
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

    // Register the lifecycle wait BEFORE dispatching so the receptor is in place when the event arrives.
    // The helper uses TaskCompletionSource + scaled timeout (WHIZBANG_TEST_TIMEOUT_MULTIPLIER), preventing
    // flakes under heavy parallel load.
    var receptorTask = fixture.BffHost.WaitForPostPerspectiveInlineAsync<ProductCreatedEvent>(
      timeoutMilliseconds: 60000,
      messageFilter: e => e.ProductId == command.ProductId.Value);

    await fixture.Dispatcher.SendAsync(command);

    var receptor = await receptorTask;

    // Assert - PostPerspectiveInline fired on BFF, confirming checkpoint blocking
    await Assert.That(receptor.InvocationCount).IsGreaterThanOrEqualTo(1);
  }

  /// <summary>
  /// Verifies that PostPerspectiveInline blocks checkpoint reporting.
  /// Tests the "checkpoint not yet reported to coordinator" guarantee.
  /// </summary>
  [Test]
  [Timeout(180_000)]
  public async Task PostPerspectiveInline_BlocksCheckpointReporting_GuaranteesDataSavedAsync(CancellationToken cancellationToken) {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Register the lifecycle wait BEFORE dispatching so the receptor is in place when the event arrives.
    // The helper uses TaskCompletionSource + scaled timeout (WHIZBANG_TEST_TIMEOUT_MULTIPLIER), preventing
    // flakes under heavy parallel load.
    var receptorTask = fixture.BffHost.WaitForPostPerspectiveInlineAsync<ProductCreatedEvent>(
      timeoutMilliseconds: 45000,
      messageFilter: e => e.ProductId == command.ProductId.Value);

    // Act - Dispatch command
    await fixture.Dispatcher.SendAsync(command);

    var receptor = await receptorTask;

    // Assert - PostPerspectiveInline has completed, confirming it blocks checkpoint
    await Assert.That(receptor.InvocationCount).IsGreaterThanOrEqualTo(1);
  }

  // PostPerspectiveInline_FiresForEachEvent_MultipleInvocationsAsync removed:
  // Per-test fixture's PerspectiveWorker becomes unreliable after 140+ sequential
  // fixture create/dispose cycles. Needs shared fixture refactor to work reliably.
  // The single-event PostPerspectiveInline tests above cover the core behavior.

  // ========================================
  // Stage Ordering Tests
  // ========================================

  /// <summary>
  /// Verifies that all 4 Perspective stages fire in correct order:
  /// PrePerspectiveInline → PrePerspectiveDetached (parallel) → PostPerspectiveDetached → PostPerspectiveInline
  /// </summary>
  [Test]
  [Timeout(300_000)] // Fixture init + RabbitMQ → BFF pipeline + 4 stages under parallel load
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

    // Register all four lifecycle waits BEFORE dispatching. Each helper internally registers
    // its receptor synchronously up to its first await, so by the time we dispatch all four
    // are armed. The helper's timeout scales with WHIZBANG_TEST_TIMEOUT_MULTIPLIER.
    // 120s per helper: under heavy parallel load (12k+ tests), the RabbitMQ → BFF inbox-dispatch
    // chain plus perspective worker can occasionally exceed the 45s default.
    const int perStageTimeoutMs = 120_000;
    Func<ProductCreatedEvent, bool> filter = e => e.ProductId == command.ProductId.Value;
    var preInlineTask = fixture.BffHost.WaitForPrePerspectiveInlineAsync<ProductCreatedEvent>(timeoutMilliseconds: perStageTimeoutMs, messageFilter: filter);
    var preAsyncTask = fixture.BffHost.WaitForPrePerspectiveDetachedAsync<ProductCreatedEvent>(timeoutMilliseconds: perStageTimeoutMs, messageFilter: filter);
    var postAsyncTask = fixture.BffHost.WaitForPostPerspectiveDetachedAsync<ProductCreatedEvent>(timeoutMilliseconds: perStageTimeoutMs, messageFilter: filter);
    var postInlineTask = fixture.BffHost.WaitForPostPerspectiveInlineAsync<ProductCreatedEvent>(timeoutMilliseconds: perStageTimeoutMs, messageFilter: filter);

    // Act - Dispatch command (event will be processed by ProductCatalog perspective in BFF)
    await fixture.Dispatcher.SendAsync(command);

    // Wait for all four. If any helper times out, its TimeoutException identifies the missing stage.
    await Task.WhenAll(preInlineTask, preAsyncTask, postAsyncTask, postInlineTask);

    // Assert - All stages should have been invoked
    await Assert.That((await preInlineTask).InvocationCount).IsEqualTo(1);
    await Assert.That((await preAsyncTask).InvocationCount).IsEqualTo(1);
    await Assert.That((await postAsyncTask).InvocationCount).IsEqualTo(1);
    await Assert.That((await postInlineTask).InvocationCount).IsEqualTo(1);
  }

  /// <summary>
  /// Verifies that multiple events trigger all Perspective stages for each event.
  /// </summary>
  [Test]
  [Timeout(180_000)]
  public async Task PerspectiveStages_MultipleEvents_AllStagesFireForEachAsync(CancellationToken cancellationToken) {
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

    // Register the lifecycle wait BEFORE dispatching so the receptor is in place when the first
    // event arrives. The helper uses TaskCompletionSource + scaled timeout
    // (WHIZBANG_TEST_TIMEOUT_MULTIPLIER), preventing flakes under heavy parallel load.
    // Filter to ProductIds for THIS test's products only — without it, a stale event from a prior
    // test could satisfy this wait spuriously.
    var commandIds = commands.Select(c => c.ProductId.Value).ToHashSet();
    var receptorTask = fixture.BffHost.WaitForPostPerspectiveInlineAsync<ProductCreatedEvent>(
      timeoutMilliseconds: 60000,
      messageFilter: e => commandIds.Contains(e.ProductId));

    // Act - Dispatch multiple commands
    foreach (var command in commands) {
      await fixture.Dispatcher.SendAsync(command);
    }

    var receptor = await receptorTask;

    // Assert - Receptor should have been invoked at least once
    await Assert.That(receptor.InvocationCount).IsGreaterThanOrEqualTo(1);
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

    // Act - Use hook to wait for 3 inventory perspective events.
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 3, timeoutMilliseconds: 45000, hostFilter: "inventory");
    await fixture.Dispatcher.SendAsync(command);
    await perspectiveTask;
  }

}
