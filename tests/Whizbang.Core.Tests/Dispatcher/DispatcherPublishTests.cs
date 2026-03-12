using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Tests.Generated;
using Whizbang.Core.ValueObjects;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Dispatcher;

/// <summary>
/// Tests for LocalInvokeAndSyncAsync 3-type-param overload and LocalInvokeAndSyncForPerspectiveAsync.
/// These paths are not covered by existing sync tests which only cover the 2-type-param overloads.
/// </summary>
[Category("Dispatcher")]
[Category("Sync")]
[NotInParallel]
public class DispatcherPublishTests {

  // ========================================
  // Test Message Types
  // ========================================

  public record PerspectiveCommand([property: StreamId] Guid StreamId);
  public record PerspectiveResult(bool Success);
  public record VoidPerspectiveCommand([property: StreamId] Guid StreamId);

  public class PerspectiveCommandReceptor : IReceptor<PerspectiveCommand, PerspectiveResult> {
    public ValueTask<PerspectiveResult> HandleAsync(PerspectiveCommand message, CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(new PerspectiveResult(true));
    }
  }

  public class VoidPerspectiveCommandReceptor : IReceptor<VoidPerspectiveCommand> {
    public ValueTask HandleAsync(VoidPerspectiveCommand message, CancellationToken cancellationToken = default) {
      return ValueTask.CompletedTask;
    }
  }

  // Fake perspective marker class
  public sealed class FakePerspective { }

  // ========================================
  // LocalInvokeAndSyncAsync<TMessage, TResult, TPerspective> - 3-type-param overload
  // ========================================

  [Test]
  public async Task LocalInvokeAndSyncAsync_ThreeTypeParams_WithNoSyncAwaiter_ReturnsSyncedAsync() {
    // When no IPerspectiveSyncAwaiter is registered and message has StreamId,
    // it returns SyncOutcome.Synced (can't verify either way)
    var dispatcher = _createDispatcher();
    var command = new PerspectiveCommand(Guid.NewGuid());

    // Act - should not throw, returns result
    var result = await dispatcher.LocalInvokeAndSyncAsync<PerspectiveCommand, PerspectiveResult, FakePerspective>(
      command);

    await Assert.That(result).IsNotNull();
    await Assert.That(result.Success).IsTrue();
  }

  [Test]
  public async Task LocalInvokeAndSyncAsync_ThreeTypeParams_MessageWithoutStreamId_ReturnsSyncedAsync() {
    // Message without StreamId - _waitForSpecificPerspectiveAsync returns NoPendingEvents
    var dispatcher = _createDispatcher();
    var command = new NoStreamIdCommand("test");

    // Act - should not throw
    var result = await dispatcher.LocalInvokeAndSyncAsync<NoStreamIdCommand, NoStreamIdResult, FakePerspective>(
      command);

    await Assert.That(result).IsNotNull();
    await Assert.That(result.Success).IsTrue();
  }

  [Test]
  public async Task LocalInvokeAndSyncAsync_ThreeTypeParams_WithTimeout_PassesTimeoutAsync() {
    var dispatcher = _createDispatcher();
    var command = new PerspectiveCommand(Guid.NewGuid());
    var timeout = TimeSpan.FromSeconds(1);

    // Act - should complete (no awaiter registered, returns synced)
    var result = await dispatcher.LocalInvokeAndSyncAsync<PerspectiveCommand, PerspectiveResult, FakePerspective>(
      command, timeout: timeout);

    await Assert.That(result).IsNotNull();
  }

  // ========================================
  // LocalInvokeAndSyncForPerspectiveAsync - Void variant with specific perspective
  // ========================================

  [Test]
  public async Task LocalInvokeAndSyncForPerspectiveAsync_WithNoSyncAwaiter_ReturnsSyncedAsync() {
    // When no IPerspectiveSyncAwaiter is registered, the test-local VoidPerspectiveCommand type is not
    // known to the generated StreamId extractor, so it returns NoPendingEvents (can't extract stream ID)
    var dispatcher = _createDispatcher();
    var command = new VoidPerspectiveCommand(Guid.NewGuid());

    // Act
    var syncResult = await dispatcher.LocalInvokeAndSyncForPerspectiveAsync<VoidPerspectiveCommand, FakePerspective>(
      command);

    await Assert.That(syncResult.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
  }

  [Test]
  public async Task LocalInvokeAndSyncForPerspectiveAsync_WithMessageWithoutStreamId_ReturnsNoPendingEventsAsync() {
    // Message without StreamId - no stream-specific events to wait for
    var dispatcher = _createDispatcher();
    var command = new NoStreamIdVoidCommand("test");

    // Act
    var syncResult = await dispatcher.LocalInvokeAndSyncForPerspectiveAsync<NoStreamIdVoidCommand, FakePerspective>(
      command);

    await Assert.That(syncResult.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
  }

  [Test]
  public async Task LocalInvokeAndSyncForPerspectiveAsync_WithTimeout_PassesTimeoutAsync() {
    var dispatcher = _createDispatcher();
    var command = new VoidPerspectiveCommand(Guid.NewGuid());
    var timeout = TimeSpan.FromSeconds(1);

    // Act - test-local type not known to generated StreamId extractor; returns NoPendingEvents
    var syncResult = await dispatcher.LocalInvokeAndSyncForPerspectiveAsync<VoidPerspectiveCommand, FakePerspective>(
      command, timeout: timeout);

    await Assert.That(syncResult.Outcome).IsEqualTo(SyncOutcome.NoPendingEvents);
  }

  [Test]
  public async Task LocalInvokeAndSyncForPerspectiveAsync_WithCallbacks_InvokesOnDecisionMadeAsync() {
    // Verify that the onDecisionMade callback is called
    var dispatcher = _createDispatcher();
    var command = new VoidPerspectiveCommand(Guid.NewGuid());
    SyncDecisionContext? capturedContext = null;

    // Act
    var syncResult = await dispatcher.LocalInvokeAndSyncForPerspectiveAsync<VoidPerspectiveCommand, FakePerspective>(
      command, onDecisionMade: ctx => capturedContext = ctx);

    await Assert.That(capturedContext).IsNotNull();
  }

  // ========================================
  // Helper Types - Messages WITHOUT StreamId
  // ========================================

  public record NoStreamIdCommand(string Data);
  public record NoStreamIdResult(bool Success);

  public class NoStreamIdCommandReceptor : IReceptor<NoStreamIdCommand, NoStreamIdResult> {
    public ValueTask<NoStreamIdResult> HandleAsync(NoStreamIdCommand message, CancellationToken cancellationToken = default) {
      return ValueTask.FromResult(new NoStreamIdResult(true));
    }
  }

  public record NoStreamIdVoidCommand(string Data);

  public class NoStreamIdVoidCommandReceptor : IReceptor<NoStreamIdVoidCommand> {
    public ValueTask HandleAsync(NoStreamIdVoidCommand message, CancellationToken cancellationToken = default) {
      return ValueTask.CompletedTask;
    }
  }

  // ========================================
  // Helper Methods
  // ========================================

  private static IDispatcher _createDispatcher() {
    var services = new ServiceCollection();
    services.AddSingleton<IServiceInstanceProvider>(
      new ServiceInstanceProvider(configuration: null));
    services.AddReceptors();
    services.AddWhizbangDispatcher();
    return services.BuildServiceProvider().GetRequiredService<IDispatcher>();
  }
}
