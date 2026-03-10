using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Tests.Generated;

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Tests verifying that perspective sync is NOT triggered for commands (ICommand).
/// Perspectives only process events (IEvent), so waiting for perspective sync on a command
/// would wait forever and timeout.
/// </summary>
/// <docs>core-concepts/dispatcher#perspective-sync</docs>
[Category("Dispatcher")]
[Category("Sync")]
[Category("Commands")]
[NotInParallel]
public sealed class DispatcherPerspectiveSyncCommandTests {
  // Test perspective type
  public sealed class TestSyncPerspective { }

  // Test command (NOT an event) - should NOT wait for perspective sync
  public sealed record TestSyncCommand([property: StreamId] Guid StreamId) : ICommand;

  // Test command with result - should NOT wait for perspective sync
  public sealed record TestSyncCommandWithResult([property: StreamId] Guid StreamId) : ICommand;

  // Result type for command with result (must implement IMessage for dispatcher)
  public sealed record TestSyncCommandResult(bool Success) : IMessage;

  // Test event - SHOULD wait for perspective sync (and timeout if not processed)
  public sealed record TestSyncEvent([property: StreamId] Guid StreamId) : IEvent;

  // Test plain message (not IEvent, not ICommand) - should NOT wait for perspective sync
  public sealed record TestSyncPlainMessage([property: StreamId] Guid StreamId) : IMessage;

  /// <summary>
  /// Command receptor WITH [AwaitPerspectiveSync] - should NOT wait because commands
  /// are not processed by perspectives.
  /// </summary>
  [AwaitPerspectiveSync(typeof(TestSyncPerspective), TimeoutMs = 100)]
  public sealed class TestSyncCommandReceptor : IReceptor<TestSyncCommand> {
    public ValueTask HandleAsync(TestSyncCommand message, CancellationToken cancellationToken = default)
      => ValueTask.CompletedTask;
  }

  /// <summary>
  /// Command receptor with result AND [AwaitPerspectiveSync] - should NOT wait.
  /// </summary>
  [AwaitPerspectiveSync(typeof(TestSyncPerspective), TimeoutMs = 100)]
  public sealed class TestSyncCommandWithResultReceptor : IReceptor<TestSyncCommandWithResult, TestSyncCommandResult> {
    public ValueTask<TestSyncCommandResult> HandleAsync(TestSyncCommandWithResult message, CancellationToken cancellationToken = default)
      => ValueTask.FromResult(new TestSyncCommandResult(true));
  }

  /// <summary>
  /// Event receptor WITH [AwaitPerspectiveSync] - SHOULD wait (and timeout if not processed).
  /// </summary>
  [AwaitPerspectiveSync(typeof(TestSyncPerspective), TimeoutMs = 100)]
  public sealed class TestSyncEventReceptor : IReceptor<TestSyncEvent> {
    public ValueTask HandleAsync(TestSyncEvent message, CancellationToken cancellationToken = default)
      => ValueTask.CompletedTask;
  }

  /// <summary>
  /// Plain message receptor WITH [AwaitPerspectiveSync] - should NOT wait.
  /// </summary>
  [AwaitPerspectiveSync(typeof(TestSyncPerspective), TimeoutMs = 100)]
  public sealed class TestSyncPlainMessageReceptor : IReceptor<TestSyncPlainMessage> {
    public ValueTask HandleAsync(TestSyncPlainMessage message, CancellationToken cancellationToken = default)
      => ValueTask.CompletedTask;
  }

  [Test]
  public async Task LocalInvokeAsync_WithCommand_DoesNotWaitForPerspectiveSyncAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var command = new TestSyncCommand(Guid.NewGuid());

    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

    // Act - should NOT wait for perspective sync, complete immediately
    await dispatcher.LocalInvokeAsync(command);

    stopwatch.Stop();

    // Assert - should complete almost immediately, NOT wait for the 100ms timeout
    await Assert.That(stopwatch.ElapsedMilliseconds).IsLessThan(50)
      .Because("Command should NOT wait for perspective sync (perspectives don't process commands)");
  }

  [Test]
  public async Task LocalInvokeAsync_WithEvent_WithSyncAttribute_WaitsAndTimesOutAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var eventMessage = new TestSyncEvent(Guid.NewGuid());

    // Act & Assert - SHOULD throw timeout because no perspective processes the event
    await Assert.ThrowsAsync<PerspectiveSyncTimeoutException>(async () => {
      await dispatcher.LocalInvokeAsync(eventMessage);
    }).Because("Event WITH [AwaitPerspectiveSync] should wait and timeout when not processed");
  }

  [Test]
  public async Task LocalInvokeAsync_WithCommandReturningResult_DoesNotWaitForPerspectiveSyncAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var command = new TestSyncCommandWithResult(Guid.NewGuid());

    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

    // Act - should NOT wait for perspective sync, complete immediately
    var result = await dispatcher.LocalInvokeAsync<TestSyncCommandWithResult, TestSyncCommandResult>(command);

    stopwatch.Stop();

    // Assert
    await Assert.That(result.Success).IsTrue();
    await Assert.That(stopwatch.ElapsedMilliseconds).IsLessThan(50)
      .Because("Command with result should NOT wait for perspective sync");
  }

  [Test]
  public async Task LocalInvokeAsync_WithPlainMessage_DoesNotWaitForPerspectiveSyncAsync() {
    // Arrange
    var dispatcher = _createDispatcher();
    var message = new TestSyncPlainMessage(Guid.NewGuid());

    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

    // Act - should NOT wait for perspective sync, complete immediately
    await dispatcher.LocalInvokeAsync(message);

    stopwatch.Stop();

    // Assert - should complete almost immediately
    await Assert.That(stopwatch.ElapsedMilliseconds).IsLessThan(50)
      .Because("Plain IMessage (not IEvent) should NOT wait for perspective sync");
  }

  private static IDispatcher _createDispatcher() {
    var services = new ServiceCollection();

    services.AddSingleton<Whizbang.Core.Observability.IServiceInstanceProvider>(
        new Whizbang.Core.Observability.ServiceInstanceProvider(configuration: null));

    services.AddReceptors();
    services.AddWhizbangDispatcher();

    // Add a mock IPerspectiveSyncAwaiter that will timeout if called
    services.AddScoped<IPerspectiveSyncAwaiter, TimeoutPerspectiveSyncAwaiter>();

    var serviceProvider = services.BuildServiceProvider();
    return serviceProvider.GetRequiredService<IDispatcher>();
  }

  /// <summary>
  /// Mock awaiter that always times out. This helps verify that the sync mechanism
  /// is actually being triggered (for events) vs skipped (for commands).
  /// </summary>
  private sealed class TimeoutPerspectiveSyncAwaiter : IPerspectiveSyncAwaiter {
    public Guid AwaiterId { get; } = Guid.NewGuid();

    public Task<SyncResult> WaitAsync(Type perspectiveType, PerspectiveSyncOptions options, CancellationToken ct = default) {
      throw new PerspectiveSyncTimeoutException(perspectiveType, options.Timeout, "Test timeout");
    }

    public Task<bool> IsCaughtUpAsync(Type perspectiveType, PerspectiveSyncOptions options, CancellationToken ct = default) {
      return Task.FromResult(false);
    }

    public Task<SyncResult> WaitForStreamAsync(
        Type perspectiveType,
        Guid streamId,
        Type[]? eventTypes,
        TimeSpan timeout,
        Guid? eventIdToAwait = null,
        CancellationToken ct = default) {
      // Always return timed out - if this is called, the sync mechanism was triggered
      return Task.FromResult(new SyncResult(SyncOutcome.TimedOut, 0, timeout));
    }
  }
}
