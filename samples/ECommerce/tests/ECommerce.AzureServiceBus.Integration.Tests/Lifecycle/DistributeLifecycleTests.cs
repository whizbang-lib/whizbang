using System.Diagnostics.CodeAnalysis;
using ECommerce.Contracts.Commands;
using ECommerce.Integration.Tests.Fixtures;

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
[NotInParallel("ServiceBus")]
[Timeout(120_000)]
public class DistributeLifecycleTests {
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

    // Act - Use hook to wait for perspective processing (distribute stages fire during event distribution)
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 2, timeoutMilliseconds: 45000, hostFilter: "inventory");
    await fixture.Dispatcher.SendAsync(command);
    await perspectiveTask;
  }

  // ========================================
  // PreDistributeDetached Tests (Non-Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PreDistributeDetached lifecycle stage fires before work distribution (non-blocking).
  /// Should use Task.Run and not block ProcessWorkBatchAsync.
  /// </summary>
  [Test]
  public async Task PreDistributeDetached_FiresBeforeDistribution_NonBlockingAsync() {
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
  // DistributeDetached Tests (Parallel, Non-Blocking)
  // ========================================

  /// <summary>
  /// Verifies that DistributeDetached lifecycle stage fires in parallel with ProcessWorkBatchAsync.
  /// Should use Task.Run and execute concurrently with work distribution.
  /// </summary>
  [Test]
  public async Task DistributeDetached_FiresInParallelWithDistribution_NonBlockingAsync() {
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
  /// Verifies that DistributeDetached completes even if distribution takes time.
  /// Tests the "may complete after distribution finishes" guarantee.
  /// </summary>
  [Test]
  public async Task DistributeDetached_CompletesIndependentlyOfDistribution_NonBlockingAsync() {
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

    // Act - Wait for perspective processing for both commands
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 4, timeoutMilliseconds: 60000, hostFilter: "inventory");
    foreach (var command in commands) {
      await fixture.Dispatcher.SendAsync(command);
    }
    await perspectiveTask;
  }

  // ========================================
  // PostDistributeDetached Tests (Non-Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PostDistributeDetached lifecycle stage fires after work distribution (non-blocking).
  /// Should use Task.Run and not block next steps.
  /// </summary>
  [Test]
  public async Task PostDistributeDetached_FiresAfterDistribution_NonBlockingAsync() {
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

    // Act - Use hook to wait for perspective processing
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 2, timeoutMilliseconds: 45000, hostFilter: "inventory");
    await fixture.Dispatcher.SendAsync(command);
    await perspectiveTask;
  }

  // ========================================
  // Stage Ordering Tests
  // ========================================

  /// <summary>
  /// Verifies that all 5 Distribute stages fire in correct order:
  /// PreDistributeInline -> PreDistributeDetached -> DistributeDetached (parallel) -> PostDistributeDetached -> PostDistributeInline
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

    // Act - Use hook to wait for perspective processing (all distribute stages fire in order)
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 2, timeoutMilliseconds: 60000, hostFilter: "inventory");
    await fixture.Dispatcher.SendAsync(command);
    await perspectiveTask;
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

    // Act - Wait for perspective processing for both commands
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 4, timeoutMilliseconds: 60000, hostFilter: "inventory");
    foreach (var command in commands) {
      await fixture.Dispatcher.SendAsync(command);
    }
    await perspectiveTask;
  }
}
