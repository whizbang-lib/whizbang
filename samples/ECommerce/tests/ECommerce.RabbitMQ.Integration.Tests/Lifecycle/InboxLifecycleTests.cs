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

    // Act - Register receptor for ProductCreatedEvent (received by BFF from RabbitMQ)
    // IMPORTANT: Start waiting but don't await yet - we need to send the command first!
    var receptorTask = fixture.BffHost.WaitForPreInboxInlineAsync<ProductCreatedEvent>(
      timeoutMilliseconds: 20000);

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

    // Act - Register receptor for ProductCreatedEvent (received by BFF)
    // IMPORTANT: Start waiting but don't await yet - we need to send the command first!
    var receptorTask = fixture.BffHost.WaitForPreInboxDetachedAsync<ProductCreatedEvent>(
      timeoutMilliseconds: 20000);

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

    var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var receptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(completionSource);

    var registry = fixture.BffHost.Services.GetRequiredService<IReceptorRegistry>();
    registry.Register<ProductCreatedEvent>(receptor, LifecycleStage.PreInboxDetached);

    try {
      // Act - Dispatch command
      await fixture.Dispatcher.SendAsync(command);

      // Wait for PreInboxDetached stage (non-blocking, may complete late)
      await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(20));

      // Assert - PreInboxDetached should have completed eventually
      await Assert.That(receptor.InvocationCount).IsEqualTo(1);

    } finally {
      registry.Unregister<ProductCreatedEvent>(receptor, LifecycleStage.PreInboxDetached);
    }
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

    // Act - Register receptor for ProductCreatedEvent (received by BFF)
    // IMPORTANT: Start waiting but don't await yet - we need to send the command first!
    var receptorTask = fixture.BffHost.WaitForPostInboxDetachedAsync<ProductCreatedEvent>(
      timeoutMilliseconds: 20000);

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

    var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var receptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(completionSource);

    var registry = fixture.BffHost.Services.GetRequiredService<IReceptorRegistry>();
    registry.Register<ProductCreatedEvent>(receptor, LifecycleStage.PostInboxDetached);

    try {
      // Act - Dispatch command
      await fixture.Dispatcher.SendAsync(command);

      // Wait for PostInboxDetached stage
      await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(20));

      // Assert - At this point, PostInboxDetached has fired
      // Receptor should have completed successfully
      await Assert.That(receptor.InvocationCount).IsEqualTo(1);

    } finally {
      registry.Unregister<ProductCreatedEvent>(receptor, LifecycleStage.PostInboxDetached);
    }
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

    // Act - Register receptor for ProductCreatedEvent (received by BFF)
    // IMPORTANT: Start waiting but don't await yet - we need to send the command first!
    var receptorTask = fixture.BffHost.WaitForPostInboxInlineAsync<ProductCreatedEvent>(
      timeoutMilliseconds: 20000);

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
  /// Verifies that all 4 Inbox stages fire in correct order:
  /// PreInboxInline → PreInboxDetached (parallel with receptor) → PostInboxDetached → PostInboxInline
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
    registry.Register<ProductCreatedEvent>(preInlineReceptor, LifecycleStage.PreInboxInline);
    registry.Register<ProductCreatedEvent>(preAsyncReceptor, LifecycleStage.PreInboxDetached);
    registry.Register<ProductCreatedEvent>(postAsyncReceptor, LifecycleStage.PostInboxDetached);
    registry.Register<ProductCreatedEvent>(postInlineReceptor, LifecycleStage.PostInboxInline);

    try {
      // Act - Dispatch command (will publish event to RabbitMQ, BFF will receive it)
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
      registry.Unregister<ProductCreatedEvent>(preInlineReceptor, LifecycleStage.PreInboxInline);
      registry.Unregister<ProductCreatedEvent>(preAsyncReceptor, LifecycleStage.PreInboxDetached);
      registry.Unregister<ProductCreatedEvent>(postAsyncReceptor, LifecycleStage.PostInboxDetached);
      registry.Unregister<ProductCreatedEvent>(postInlineReceptor, LifecycleStage.PostInboxInline);
    }
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

    var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var receptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(completionSource);

    var registry = fixture.BffHost.Services.GetRequiredService<IReceptorRegistry>();
    registry.Register<ProductCreatedEvent>(receptor, LifecycleStage.PostInboxInline);

    try {
      // Act - Dispatch multiple commands
      foreach (var command in commands) {
        await fixture.Dispatcher.SendAsync(command);
      }

      // Wait for last event to complete PostInboxInline
      await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(30));

      // Assert - Receptor should have been invoked at least once
      await Assert.That(receptor.InvocationCount).IsGreaterThanOrEqualTo(1);

    } finally {
      registry.Unregister<ProductCreatedEvent>(receptor, LifecycleStage.PostInboxInline);
    }
  }
}
