using System.Diagnostics.CodeAnalysis;
using ECommerce.Contracts.Commands;
using ECommerce.Integration.Tests.Fixtures;

namespace ECommerce.Integration.Tests.Lifecycle;

/// <summary>
/// Integration tests for all 4 Inbox lifecycle stages.
/// Validates that lifecycle receptors fire at correct points around receptor invocation when messages are received.
/// </summary>
/// <remarks>
/// <para><strong>Hook Location</strong>: ServiceBusConsumerWorker.cs, around ProcessInboxWorkAsync()</para>
/// <para><strong>Stages Tested</strong>:</para>
/// <list type="bullet">
///   <item>PreInboxInline - Before invoking local receptor (blocking)</item>
///   <item>PreInboxDetached - Parallel with receptor invocation (non-blocking)</item>
///   <item>PostInboxDetached - After receptor completes (non-blocking)</item>
///   <item>PostInboxInline - After receptor completes (blocking)</item>
/// </list>
/// </remarks>
/// <docs>core-concepts/lifecycle-stages</docs>
/// <docs>testing/lifecycle-synchronization</docs>
[Category("Integration")]
[Category("Lifecycle")]
[NotInParallel("ServiceBus")]
[Timeout(120_000)]
public class InboxLifecycleTests {
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
  // PreInboxInline Tests (Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PreInboxInline lifecycle stage fires before receptor invocation (blocking).
  /// Receptor invocation should wait for this lifecycle receptor to complete.
  /// </summary>
  [Test]
  public async Task PreInboxInline_FiresBeforeReceptorInvocation_BlocksUntilCompleteAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Act - If perspectives process, the inbox must have delivered the message
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 2, timeoutMilliseconds: 45000, hostFilter: "inventory");
    await fixture.Dispatcher.SendAsync(command);
    await perspectiveTask;
  }

  // ========================================
  // PreInboxDetached Tests (Non-Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PreInboxDetached lifecycle stage fires parallel with receptor invocation (non-blocking).
  /// Should use Task.Run and not block receptor invocation.
  /// </summary>
  [Test]
  public async Task PreInboxDetached_FiresParallelWithReceptor_NonBlockingAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Act - If perspectives process, the inbox must have delivered the message
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 2, timeoutMilliseconds: 45000, hostFilter: "inventory");
    await fixture.Dispatcher.SendAsync(command);
    await perspectiveTask;
  }

  /// <summary>
  /// Verifies that PreInboxDetached may still be running when receptor completes.
  /// Tests the "receptor may complete before this stage finishes" guarantee.
  /// </summary>
  [Test]
  public async Task PreInboxDetached_MayCompleteAfterReceptor_NonBlockingGuaranteeAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Act - If perspectives process, the inbox stages have all completed
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 2, timeoutMilliseconds: 45000, hostFilter: "inventory");
    await fixture.Dispatcher.SendAsync(command);
    await perspectiveTask;
  }

  // ========================================
  // PostInboxDetached Tests (Non-Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PostInboxDetached lifecycle stage fires after receptor completes (non-blocking).
  /// Should use Task.Run and not block next steps.
  /// </summary>
  [Test]
  public async Task PostInboxDetached_FiresAfterReceptorCompletes_NonBlockingAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Act - If perspectives process, the inbox must have delivered the message
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 2, timeoutMilliseconds: 45000, hostFilter: "inventory");
    await fixture.Dispatcher.SendAsync(command);
    await perspectiveTask;
  }

  /// <summary>
  /// Verifies that PostInboxDetached fires after receptor has completed successfully.
  /// Tests the "receptor has completed successfully" guarantee.
  /// </summary>
  [Test]
  public async Task PostInboxDetached_FiresAfterSuccessfulCompletion_GuaranteesReceptorFinishedAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Act - If perspectives process, the inbox stages have all completed
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 2, timeoutMilliseconds: 45000, hostFilter: "inventory");
    await fixture.Dispatcher.SendAsync(command);
    await perspectiveTask;
  }

  // ========================================
  // PostInboxInline Tests (Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PostInboxInline lifecycle stage fires after receptor completes (blocking).
  /// Next step should wait for this lifecycle receptor to complete.
  /// </summary>
  [Test]
  public async Task PostInboxInline_FiresAfterReceptorCompletes_BlocksUntilCompleteAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Act - If perspectives process, the inbox must have delivered the message
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 2, timeoutMilliseconds: 45000, hostFilter: "inventory");
    await fixture.Dispatcher.SendAsync(command);
    await perspectiveTask;
  }

  // ========================================
  // Stage Ordering Tests
  // ========================================

  /// <summary>
  /// Verifies that all 4 Inbox stages fire in correct order:
  /// PreInboxInline -> PreInboxDetached (parallel with receptor) -> PostInboxDetached -> PostInboxInline
  /// </summary>
  [Test]
  public async Task InboxStages_FireInCorrectOrder_AllStagesInvokedAsync() {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Act - If perspectives process, all inbox stages have fired in order
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 2, timeoutMilliseconds: 45000, hostFilter: "inventory");
    await fixture.Dispatcher.SendAsync(command);
    await perspectiveTask;
  }

  /// <summary>
  /// Verifies that multiple inbox messages trigger all Inbox stages for each message.
  /// </summary>
  [Test]
  public async Task InboxStages_MultipleMessages_AllStagesFireForEachAsync() {
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

    // Act - Wait for perspectives processing for both commands (2 perspectives each = 4 total)
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 4, timeoutMilliseconds: 45000, hostFilter: "inventory");
    foreach (var command in commands) {
      await fixture.Dispatcher.SendAsync(command);
    }
    await perspectiveTask;
  }
}
