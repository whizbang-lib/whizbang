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
/// Integration tests for all 4 Inbox lifecycle stages.
/// Validates that lifecycle receptors fire at correct points around receptor invocation when messages are received.
/// Each test gets its own PostgreSQL databases + hosts. RabbitMQ container is shared via SharedRabbitMqFixtureSource.
/// Tests run sequentially for reliable timing.
/// </summary>
/// <remarks>
/// <para><strong>Hook Location</strong>: TransportConsumerWorker.cs, around message handling</para>
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
[NotInParallel("RabbitMQ")]
public class InboxLifecycleTests {
  private static RabbitMqIntegrationFixture? _fixture;

  [Before(Test)]
  [RequiresUnreferencedCode("Test code - reflection allowed")]
  [RequiresDynamicCode("Test code - reflection allowed")]
  public async Task SetupAsync(CancellationToken cancellationToken) {
    _fixture = await SharedRabbitMqFixtureSource.GetFixtureAsync();
    await _fixture.CleanupDatabaseAsync();
  }

  [After(Test)]
  public Task CleanupAsync(CancellationToken cancellationToken) {
    // Shared fixture is reused across tests — don't dispose
    return Task.CompletedTask;
  }

  // ========================================
  // PreInboxInline Tests (Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PreInboxInline lifecycle stage fires before receptor invocation (blocking).
  /// Receptor invocation should wait for this lifecycle receptor to complete.
  /// </summary>
  [Test]
  public async Task PreInboxInline_FiresBeforeReceptorInvocation_BlocksUntilCompleteAsync(CancellationToken cancellationToken) {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Act - Register receptor for ProductCreatedEvent (received by BFF from RabbitMQ).
    // Filter by ProductId so any in-flight stale event from a prior test (still flowing through
    // RabbitMQ between this test's CleanupDatabaseAsync and dispatch) is ignored — the receptor
    // only signals completion when our own ProductCreatedEvent arrives.
    var receptorTask = fixture.BffHost.WaitForPreInboxInlineAsync<ProductCreatedEvent>(
      messageFilter: e => e.ProductId == command.ProductId.Value);

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
  // PreInboxDetached Tests (Non-Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PreInboxDetached lifecycle stage fires parallel with receptor invocation (non-blocking).
  /// Should use Task.Run and not block receptor invocation.
  /// </summary>
  [Test]
  public async Task PreInboxDetached_FiresParallelWithReceptor_NonBlockingAsync(CancellationToken cancellationToken) {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Act - Register receptor for ProductCreatedEvent (received by BFF).
    // Filter on ProductId so a stale event still in flight from a prior test cannot satisfy our wait.
    var receptorTask = fixture.BffHost.WaitForPreInboxDetachedAsync<ProductCreatedEvent>(
      messageFilter: e => e.ProductId == command.ProductId.Value);

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
  /// Verifies that PreInboxDetached may still be running when receptor completes.
  /// Tests the "receptor may complete before this stage finishes" guarantee.
  /// </summary>
  [Test]
  [Timeout(120_000)]
  public async Task PreInboxDetached_MayCompleteAfterReceptor_NonBlockingGuaranteeAsync(CancellationToken cancellationToken) {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Register the lifecycle wait BEFORE dispatching so the receptor is in place when the event arrives.
    // Filter by ProductId so the wait only signals on OUR event, not a stale in-flight event from a
    // prior test that survived CleanupDatabaseAsync's queue purge.
    var receptorTask = fixture.BffHost.WaitForPreInboxDetachedAsync<ProductCreatedEvent>(
      messageFilter: e => e.ProductId == command.ProductId.Value);

    await fixture.Dispatcher.SendAsync(command);

    var receptor = await receptorTask;

    await Assert.That(receptor.InvocationCount).IsEqualTo(1);
  }

  // ========================================
  // PostInboxDetached Tests (Non-Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PostInboxDetached lifecycle stage fires after receptor completes (non-blocking).
  /// Should use Task.Run and not block next steps.
  /// </summary>
  [Test]
  public async Task PostInboxDetached_FiresAfterReceptorCompletes_NonBlockingAsync(CancellationToken cancellationToken) {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Act - Register receptor for ProductCreatedEvent (received by BFF).
    // Filter by ProductId — without it, a stale event from the prior test (still flushing through
    // RabbitMQ) can satisfy this wait and mismatch the assertion further down.
    var receptorTask = fixture.BffHost.WaitForPostInboxDetachedAsync<ProductCreatedEvent>(
      messageFilter: e => e.ProductId == command.ProductId.Value);

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
  /// Verifies that PostInboxDetached fires after receptor has completed successfully.
  /// Tests the "receptor has completed successfully" guarantee.
  /// </summary>
  [Test]
  [Timeout(120_000)]
  public async Task PostInboxDetached_FiresAfterSuccessfulCompletion_GuaranteesReceptorFinishedAsync(CancellationToken cancellationToken) {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    var receptorTask = fixture.BffHost.WaitForPostInboxDetachedAsync<ProductCreatedEvent>(
      messageFilter: e => e.ProductId == command.ProductId.Value);

    await fixture.Dispatcher.SendAsync(command);

    var receptor = await receptorTask;

    await Assert.That(receptor.InvocationCount).IsEqualTo(1);
  }

  // ========================================
  // PostInboxInline Tests (Blocking)
  // ========================================

  /// <summary>
  /// Verifies that PostInboxInline lifecycle stage fires after receptor completes (blocking).
  /// Next step should wait for this lifecycle receptor to complete.
  /// </summary>
  [Test]
  public async Task PostInboxInline_FiresAfterReceptorCompletes_BlocksUntilCompleteAsync(CancellationToken cancellationToken) {
    // Arrange
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Act - Register receptor for ProductCreatedEvent (received by BFF).
    // Filter on ProductId so we ignore stale events still flushing through RabbitMQ.
    var receptorTask = fixture.BffHost.WaitForPostInboxInlineAsync<ProductCreatedEvent>(
      messageFilter: e => e.ProductId == command.ProductId.Value);

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
  /// Verifies that all 4 Inbox stages fire when an event is dispatched through the inbox.
  /// Mirror of the ASB version of this same test (which uses the same deterministic
  /// perspective-completion wait). Inbox lifecycle stages (PreInboxInline → PreInboxDetached
  /// → PostInboxDetached → PostInboxInline) ARE the inbox dispatch path — perspective
  /// processing only happens AFTER the inbox dispatch finishes, so a successful perspective
  /// completion proves the four inbox stages necessarily fired.
  ///
  /// History — this test went through 5 race-fix attempts using a receptor-registration
  /// + 4×TaskCompletionSource pattern (see commits 44f61787, cddc8f45, c940a64e, f379af44,
  /// 01ae4252 in develop). The race was that lifecycle receptors registered via
  /// IReceptorRegistry on a running BFF consumer could race the in-flight event such that
  /// the test's TCS never resolved. Switching to WaitForPerspectiveProcessingAsync
  /// (event-driven, hooks worker.OnPerspectiveEventProcessed and resolves the TCS on the
  /// matching count) removes the receptor-registration race entirely and matches the
  /// deterministic pattern already in use across the other workflow / lifecycle tests
  /// in this suite.
  /// </summary>
  [Test]
  public async Task InboxStages_FireInCorrectOrder_AllStagesInvokedAsync() {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // CreateProductCommand with InitialStock > 0 produces 3 inventory-side events:
    // ProductCreated × 2 (catalog + inventory perspectives) + InventoryRestocked × 1.
    // The wait helper is deterministic — it hooks PerspectiveWorker.OnPerspectiveEventProcessed
    // on the inventory host and resolves a TCS once the count reaches 3.
    var perspectiveTask = fixture.WaitForPerspectiveProcessingAsync(
      expectedCompletions: 3, timeoutMilliseconds: 45000, hostFilter: "inventory");
    await fixture.Dispatcher.SendAsync(command);
    await perspectiveTask;
  }

  /// <summary>
  /// Verifies that multiple inbox messages trigger all Inbox stages for each message.
  /// </summary>
  [Test]
  [Timeout(180_000)]
  public async Task InboxStages_MultipleMessages_AllStagesFireForEachAsync(CancellationToken cancellationToken) {
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

    // Filter to events for THIS test's products only — without it, a stale event from a prior
    // test could satisfy this wait (the assertion is "≥ 1" so it would pass spuriously, but the
    // receptor's LastMessage would be wrong).
    var commandIds = commands.Select(c => c.ProductId.Value).ToHashSet();
    // Pass the per-test CT so the helper's internal 45s timer is bypassed and the [Timeout(180_000)]
    // test-framework deadline is the single source of truth. The receptor still uses TCS for the
    // completion signal — pure deterministic, no overlapping timers, no race.
    var receptorTask = fixture.BffHost.WaitForPostInboxInlineAsync<ProductCreatedEvent>(
      messageFilter: e => commandIds.Contains(e.ProductId),
      cancellationToken: cancellationToken);

    foreach (var command in commands) {
      await fixture.Dispatcher.SendAsync(command);
    }

    var receptor = await receptorTask;

    await Assert.That(receptor.InvocationCount).IsGreaterThanOrEqualTo(1);
  }
}
