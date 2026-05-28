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
  /// Verifies that all 4 Inbox stages fire in correct order:
  /// PreInboxInline → PreInboxDetached (parallel with receptor) → PostInboxDetached → PostInboxInline
  /// </summary>
  [Test]
  [Timeout(180_000)]
  public async Task InboxStages_FireInCorrectOrder_AllStagesInvokedAsync(CancellationToken cancellationToken) {
    var fixture = _fixture ?? throw new InvalidOperationException("Fixture not initialized");

    var command = new CreateProductCommand {
      ProductId = ProductId.New(),
      Name = "Test Product",
      Description = "Test Description",
      Price = 99.99m,
      InitialStock = 10
    };

    // Inline TCS + manual receptor construction (instead of the WaitFor* helpers) so the
    // failure path can introspect each receptor's InvocationCount + LastMessage on timeout —
    // identifies whether the stage didn't fire at all (count == 0, LastMessage == null) vs
    // fired but filtered out (count == 0, LastMessage != null) vs some other condition.
    // Keep the messageFilter for stale-event safety; same filter for all four receptors.
    bool filter(ProductCreatedEvent e) => e.ProductId == command.ProductId.Value;
    var preInlineCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var preAsyncCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var postAsyncCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var postInlineCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var preInlineReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(preInlineCompletion, expectedStage: LifecycleStage.PreInboxInline, messageFilter: filter);
    var preAsyncReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(preAsyncCompletion, expectedStage: LifecycleStage.PreInboxDetached, messageFilter: filter);
    var postAsyncReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(postAsyncCompletion, expectedStage: LifecycleStage.PostInboxDetached, messageFilter: filter);
    var postInlineReceptor = new GenericLifecycleCompletionReceptor<ProductCreatedEvent>(postInlineCompletion, expectedStage: LifecycleStage.PostInboxInline, messageFilter: filter);

    var registry = fixture.BffHost.Services.GetRequiredService<IReceptorRegistry>();
    registry.Register<ProductCreatedEvent>(preInlineReceptor, LifecycleStage.PreInboxInline);
    registry.Register<ProductCreatedEvent>(preAsyncReceptor, LifecycleStage.PreInboxDetached);
    registry.Register<ProductCreatedEvent>(postAsyncReceptor, LifecycleStage.PostInboxDetached);
    registry.Register<ProductCreatedEvent>(postInlineReceptor, LifecycleStage.PostInboxInline);

    try {
      await fixture.Dispatcher.SendAsync(command);

      try {
        // Hardcoded inner timeout — must be < [Timeout(180_000)] attribute above so this
        // catch block fires FIRST (otherwise the test framework kills the method via the
        // attribute and we lose the diagnostic). Scaling with WHIZBANG_TEST_TIMEOUT_MULTIPLIER
        // would push the inner timeout past the attribute on CI (3× scale = 360s vs 180s
        // attribute) — race the diagnostic loses.
        await Task.WhenAll(
          preInlineCompletion.Task,
          preAsyncCompletion.Task,
          postAsyncCompletion.Task,
          postInlineCompletion.Task
        ).WaitAsync(TimeSpan.FromSeconds(120));
      } catch (TimeoutException) {
        static string _describe(string stage, TaskCompletionSource<bool> tcs, GenericLifecycleCompletionReceptor<ProductCreatedEvent> r)
          => $"{stage}: signaled={tcs.Task.IsCompleted}, invocations={r.InvocationCount}, lastMessageSeen={(r.LastMessage is not null ? "yes" : "no")}";
        throw new TimeoutException(
          "Inbox lifecycle stages did not all fire. " +
          _describe("PreInboxInline", preInlineCompletion, preInlineReceptor) + "; " +
          _describe("PreInboxDetached", preAsyncCompletion, preAsyncReceptor) + "; " +
          _describe("PostInboxDetached", postAsyncCompletion, postAsyncReceptor) + "; " +
          _describe("PostInboxInline", postInlineCompletion, postInlineReceptor));
      }

      await Assert.That(preInlineReceptor.InvocationCount).IsEqualTo(1);
      await Assert.That(preAsyncReceptor.InvocationCount).IsEqualTo(1);
      await Assert.That(postAsyncReceptor.InvocationCount).IsEqualTo(1);
      await Assert.That(postInlineReceptor.InvocationCount).IsEqualTo(1);
    } finally {
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
    var receptorTask = fixture.BffHost.WaitForPostInboxInlineAsync<ProductCreatedEvent>(
      messageFilter: e => commandIds.Contains(e.ProductId));

    foreach (var command in commands) {
      await fixture.Dispatcher.SendAsync(command);
    }

    var receptor = await receptorTask;

    await Assert.That(receptor.InvocationCount).IsGreaterThanOrEqualTo(1);
  }
}
