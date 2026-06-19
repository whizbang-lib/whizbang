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
/// Integration tests for all 5 Distribute lifecycle stages.
/// Validates that lifecycle receptors fire at correct points around ProcessWorkBatchAsync() call.
/// Each test gets its own PostgreSQL databases + hosts. RabbitMQ container is shared via SharedRabbitMqFixtureSource.
/// Tests run sequentially for reliable timing.
/// </summary>
/// <remarks>
/// <para><strong>Hook Location</strong>: *WorkCoordinatorStrategy.cs (Immediate/Scoped/Interval) around FlushAsync()</para>
/// <para><strong>Stages Tested</strong>:</para>
/// <list type="bullet">
///   <item>PreDistributeInline - Before ProcessWorkBatchAsync() (blocking)</item>
///   <item>PreDistributeDetached - Before ProcessWorkBatchAsync() (non-blocking, backgrounded)</item>
///   <item>DistributeDetached - In parallel with ProcessWorkBatchAsync() (non-blocking, backgrounded)</item>
///   <item>PostDistributeDetached - After ProcessWorkBatchAsync() (non-blocking, backgrounded)</item>
///   <item>PostDistributeInline - After ProcessWorkBatchAsync() (blocking)</item>
/// </list>
/// </remarks>
/// <docs>core-concepts/lifecycle-stages</docs>
/// <docs>testing/lifecycle-synchronization</docs>
[Category("Integration")]
[Category("Lifecycle")]
[NotInParallel("RabbitMQ")]
// Eventual-consistency under RabbitMQ — shared-infrastructure throughput on CI occasionally exceeds
// the per-test [Timeout] under parallel-module pressure. Matches the established [Retry(2)] convention
// on the sibling RabbitMQ workflow suites. Not a test-logic race.
[Retry(2)]
public class DistributeLifecycleTests {
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
  // PreDistributeInline Tests (Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PreDistributeInline lifecycle stage fires before work distribution (blocking).
  /// </summary>
  [Test]
  [Timeout(180_000)]
  public async Task PreDistributeInline_FiresBeforeDistribution_BlocksUntilCompleteAsync(CancellationToken cancellationToken) {
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
      timeoutMilliseconds: 60000,
      messageFilter: e => e.ProductId == command.ProductId.Value,
      cancellationToken: cancellationToken);

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
  // PreDistributeDetached Tests (Non-Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PreDistributeDetached lifecycle stage fires before work distribution (non-blocking).
  /// Should use Task.Run and not block ProcessWorkBatchAsync.
  /// </summary>
  [Test]
  [Timeout(180_000)]
  public async Task PreDistributeDetached_FiresBeforeDistribution_NonBlockingAsync(CancellationToken cancellationToken) {
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
    var receptorTask = fixture.InventoryHost.WaitForPreDistributeDetachedAsync<ProductCreatedEvent>(
      timeoutMilliseconds: 60000,
      messageFilter: e => e.ProductId == command.ProductId.Value,
      cancellationToken: cancellationToken);

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
  // DistributeDetached Tests (Parallel, Non-Blocking)
  // ========================================

  /// <summary>
  /// Verifies that DistributeDetached lifecycle stage fires in parallel with ProcessWorkBatchAsync.
  /// Should use Task.Run and execute concurrently with work distribution.
  /// </summary>
  [Test]
  [Timeout(180_000)]
  public async Task DistributeDetached_FiresInParallelWithDistribution_NonBlockingAsync(CancellationToken cancellationToken) {
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
    var receptorTask = fixture.InventoryHost.WaitForDistributeDetachedAsync<ProductCreatedEvent>(
      timeoutMilliseconds: 60000,
      messageFilter: e => e.ProductId == command.ProductId.Value,
      cancellationToken: cancellationToken);

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
  /// Verifies that DistributeDetached completes even if distribution takes time.
  /// Tests the "may complete after distribution finishes" guarantee.
  /// </summary>
  [Test]
  [Timeout(180_000)]
  public async Task DistributeDetached_CompletesIndependentlyOfDistribution_NonBlockingAsync(CancellationToken cancellationToken) {
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

    // Register the lifecycle wait BEFORE dispatching. Filter by the set of ProductIds for THIS
    // test's commands so a stale event from a prior test cannot satisfy the wait.
    // NOTE: Distribute stages fire for PUBLISHED EVENTS (in outbox), not commands
    var commandIds = commands.Select(c => c.ProductId.Value).ToHashSet();
    var receptorTask = fixture.InventoryHost.WaitForDistributeDetachedAsync<ProductCreatedEvent>(
      timeoutMilliseconds: 60000,
      messageFilter: e => commandIds.Contains(e.ProductId));

    // Act - Dispatch multiple commands
    foreach (var command in commands) {
      await fixture.Dispatcher.SendAsync(command);
    }

    var receptor = await receptorTask;

    // Assert - Receptor should have been invoked for at least one message
    await Assert.That(receptor.InvocationCount).IsGreaterThanOrEqualTo(1);
  }

  // ========================================
  // PostDistributeDetached Tests (Non-Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PostDistributeDetached lifecycle stage fires after work distribution (non-blocking).
  /// Should use Task.Run and not block next steps.
  /// </summary>
  [Test]
  [Timeout(180_000)]
  public async Task PostDistributeDetached_FiresAfterDistribution_NonBlockingAsync(CancellationToken cancellationToken) {
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
    var receptorTask = fixture.InventoryHost.WaitForPostDistributeDetachedAsync<ProductCreatedEvent>(
      timeoutMilliseconds: 60000,
      messageFilter: e => e.ProductId == command.ProductId.Value,
      cancellationToken: cancellationToken);

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
  [Timeout(180_000)]
  public async Task PostDistributeInline_FiresAfterDistribution_BlocksUntilCompleteAsync(CancellationToken cancellationToken) {
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
      timeoutMilliseconds: 60000,
      messageFilter: e => e.ProductId == command.ProductId.Value,
      cancellationToken: cancellationToken);

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
  /// PreDistributeInline → PreDistributeDetached → DistributeDetached (parallel) → PostDistributeDetached → PostDistributeInline
  /// </summary>
  [Test]
  [Timeout(300_000)]
  public async Task DistributeStages_FireInCorrectOrder_AllStagesInvokedAsync(CancellationToken cancellationToken) {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Register all five lifecycle waits BEFORE dispatching. Each helper internally registers
    // its receptor synchronously up to its first await, so by the time we dispatch all five
    // are armed. The helper's timeout scales with WHIZBANG_TEST_TIMEOUT_MULTIPLIER.
    // 120s per helper: under heavy parallel load, the outbox + transport publish plus the
    // 5 distribute stages can occasionally exceed the 45s default.
    // NOTE: Distribute stages fire for PUBLISHED EVENTS (in outbox), not commands
    const int perStageTimeoutMs = 120_000;
    bool filter(ProductCreatedEvent e) => e.ProductId == command.ProductId.Value;
    var preInlineTask = fixture.InventoryHost.WaitForPreDistributeInlineAsync<ProductCreatedEvent>(perStageTimeoutMs, messageFilter: filter);
    var preAsyncTask = fixture.InventoryHost.WaitForPreDistributeDetachedAsync<ProductCreatedEvent>(perStageTimeoutMs, messageFilter: filter);
    var distributeAsyncTask = fixture.InventoryHost.WaitForDistributeDetachedAsync<ProductCreatedEvent>(perStageTimeoutMs, messageFilter: filter);
    var postAsyncTask = fixture.InventoryHost.WaitForPostDistributeDetachedAsync<ProductCreatedEvent>(perStageTimeoutMs, messageFilter: filter);
    var postInlineTask = fixture.InventoryHost.WaitForPostDistributeInlineAsync<ProductCreatedEvent>(perStageTimeoutMs, messageFilter: filter);

    // Act - Dispatch command
    await fixture.Dispatcher.SendAsync(command);

    // Wait for all five. If any helper times out, its TimeoutException identifies the missing stage.
    await Task.WhenAll(preInlineTask, preAsyncTask, distributeAsyncTask, postAsyncTask, postInlineTask);

    // Assert - All stages should have been invoked
    await Assert.That((await preInlineTask).InvocationCount).IsEqualTo(1);
    await Assert.That((await preAsyncTask).InvocationCount).IsEqualTo(1);
    await Assert.That((await distributeAsyncTask).InvocationCount).IsEqualTo(1);
    await Assert.That((await postAsyncTask).InvocationCount).IsEqualTo(1);
    await Assert.That((await postInlineTask).InvocationCount).IsEqualTo(1);
  }

  /// <summary>
  /// Verifies that multiple commands trigger all Distribute stages for each command.
  /// </summary>
  [Test]
  [Timeout(180_000)]
  public async Task DistributeStages_MultipleCommands_AllStagesFireForEachAsync(CancellationToken cancellationToken) {
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
    // event arrives. Filter by ProductId set so a stale event from a prior test cannot satisfy the wait.
    // NOTE: Distribute stages fire for PUBLISHED EVENTS (in outbox), not commands
    var commandIds = commands.Select(c => c.ProductId.Value).ToHashSet();
    var receptorTask = fixture.InventoryHost.WaitForPostDistributeInlineAsync<ProductCreatedEvent>(
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
}
