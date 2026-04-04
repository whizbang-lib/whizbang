using ECommerce.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Whizbang.Core;
using Whizbang.Core.Messaging;

namespace ECommerce.RabbitMQ.Integration.Tests.Fixtures;

/// <summary>
/// Extension methods for testing all 18 lifecycle stages in integration tests.
/// Provides deterministic synchronization by waiting for lifecycle stage completion instead of polling.
/// </summary>
/// <docs>testing/lifecycle-synchronization</docs>
public static class LifecycleStageTestExtensions {

  /// <summary>
  /// Waits for ImmediateDetached lifecycle stage to complete.
  /// Fires immediately after receptor HandleAsync() returns, before database operations.
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForImmediateDetachedAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 20000)
    where TMessage : IMessage {

    return await _waitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.ImmediateDetached,
      timeoutMilliseconds);
  }

  /// <summary>
  /// Waits for PreDistributeInline lifecycle stage to complete.
  /// Fires before ProcessWorkBatchAsync() call (blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPreDistributeInlineAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 20000)
    where TMessage : IMessage {

    return await _waitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PreDistributeInline,
      timeoutMilliseconds);
  }

  /// <summary>
  /// Waits for PreDistributeDetached lifecycle stage to complete.
  /// Fires before ProcessWorkBatchAsync() call (non-blocking, backgrounded).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPreDistributeDetachedAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 20000)
    where TMessage : IMessage {

    return await _waitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PreDistributeDetached,
      timeoutMilliseconds);
  }

  /// <summary>
  /// Waits for DistributeDetached lifecycle stage to complete.
  /// Fires in parallel with ProcessWorkBatchAsync() (non-blocking, backgrounded).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForDistributeDetachedAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 20000)
    where TMessage : IMessage {

    return await _waitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.DistributeDetached,
      timeoutMilliseconds);
  }

  /// <summary>
  /// Waits for PostDistributeInline lifecycle stage to complete.
  /// Fires after ProcessWorkBatchAsync() completes (blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPostDistributeInlineAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 20000)
    where TMessage : IMessage {

    return await _waitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PostDistributeInline,
      timeoutMilliseconds);
  }

  /// <summary>
  /// Waits for PostDistributeDetached lifecycle stage to complete.
  /// Fires after ProcessWorkBatchAsync() completes (non-blocking, backgrounded).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPostDistributeDetachedAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 20000)
    where TMessage : IMessage {

    return await _waitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PostDistributeDetached,
      timeoutMilliseconds);
  }

  /// <summary>
  /// Waits for PreOutboxInline lifecycle stage to complete.
  /// Fires before publishing message to transport (blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPreOutboxInlineAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 30000)
    where TMessage : IMessage {

    return await _waitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PreOutboxInline,
      timeoutMilliseconds);
  }

  /// <summary>
  /// Waits for PreOutboxDetached lifecycle stage to complete.
  /// Fires parallel with transport publish (non-blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPreOutboxDetachedAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 30000)
    where TMessage : IMessage {

    return await _waitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PreOutboxDetached,
      timeoutMilliseconds);
  }

  /// <summary>
  /// Waits for PostOutboxInline lifecycle stage to complete.
  /// Fires after message published to transport (blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPostOutboxInlineAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 30000)
    where TMessage : IMessage {

    return await _waitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PostOutboxInline,
      timeoutMilliseconds);
  }

  /// <summary>
  /// Waits for PostOutboxDetached lifecycle stage to complete.
  /// Fires after message published to transport (non-blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPostOutboxDetachedAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 30000)
    where TMessage : IMessage {

    return await _waitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PostOutboxDetached,
      timeoutMilliseconds);
  }

  /// <summary>
  /// Waits for PreInboxInline lifecycle stage to complete.
  /// Fires before invoking local receptor for received message (blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPreInboxInlineAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 30000)
    where TMessage : IMessage {

    return await _waitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PreInboxInline,
      timeoutMilliseconds);
  }

  /// <summary>
  /// Waits for PreInboxDetached lifecycle stage to complete.
  /// Fires parallel with receptor invocation (non-blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPreInboxDetachedAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 30000)
    where TMessage : IMessage {

    return await _waitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PreInboxDetached,
      timeoutMilliseconds);
  }

  /// <summary>
  /// Waits for PostInboxInline lifecycle stage to complete.
  /// Fires after receptor completes (blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPostInboxInlineAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 30000)
    where TMessage : IMessage {

    return await _waitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PostInboxInline,
      timeoutMilliseconds);
  }

  /// <summary>
  /// Waits for PostInboxDetached lifecycle stage to complete.
  /// Fires after receptor completes (non-blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPostInboxDetachedAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 30000)
    where TMessage : IMessage {

    return await _waitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PostInboxDetached,
      timeoutMilliseconds);
  }

  /// <summary>
  /// Waits for PrePerspectiveInline lifecycle stage to complete.
  /// Fires before perspective RunAsync() processes events (blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TEvent>> WaitForPrePerspectiveInlineAsync<TEvent>(
    this IHost host,
    string? perspectiveName = null,
    int timeoutMilliseconds = 30000)
    where TEvent : IEvent {

    return await _waitForLifecycleStageAsync<TEvent>(
      host,
      LifecycleStage.PrePerspectiveInline,
      timeoutMilliseconds,
      perspectiveName);
  }

  /// <summary>
  /// Waits for PrePerspectiveDetached lifecycle stage to complete.
  /// Fires parallel with perspective RunAsync() (non-blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TEvent>> WaitForPrePerspectiveDetachedAsync<TEvent>(
    this IHost host,
    string? perspectiveName = null,
    int timeoutMilliseconds = 30000)
    where TEvent : IEvent {

    return await _waitForLifecycleStageAsync<TEvent>(
      host,
      LifecycleStage.PrePerspectiveDetached,
      timeoutMilliseconds,
      perspectiveName);
  }

  /// <summary>
  /// Waits for PostPerspectiveDetached lifecycle stage to complete.
  /// Fires after perspective completes, before checkpoint reported (non-blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TEvent>> WaitForPostPerspectiveDetachedAsync<TEvent>(
    this IHost host,
    string? perspectiveName = null,
    int timeoutMilliseconds = 30000)
    where TEvent : IEvent {

    return await _waitForLifecycleStageAsync<TEvent>(
      host,
      LifecycleStage.PostPerspectiveDetached,
      timeoutMilliseconds,
      perspectiveName);
  }

  /// <summary>
  /// Core helper method that registers a receptor, waits for completion, and returns the receptor.
  /// Returns the receptor so tests can inspect invocation count, last message, etc.
  /// </summary>
  private static async Task<GenericLifecycleCompletionReceptor<TMessage>> _waitForLifecycleStageAsync<TMessage>(
    IHost host,
    LifecycleStage stage,
    int timeoutMilliseconds,
    string? perspectiveName = null)
    where TMessage : IMessage {

    ArgumentNullException.ThrowIfNull(host);

    // Create completion source for signaling
    // CRITICAL: Use RunContinuationsAsynchronously to prevent deadlocks
    var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    // Create receptor that will signal completion
    var receptor = new GenericLifecycleCompletionReceptor<TMessage>(
      completionSource,
      expectedStage: stage,
      perspectiveName: perspectiveName);

    // Get registry from host
    var registry = host.Services.GetRequiredService<IReceptorRegistry>();

    // Register receptor at specified stage
    registry.Register<TMessage>(receptor, stage);

    try {
      // Wait for completion with scaled timeout (respects WHIZBANG_TEST_TIMEOUT_MULTIPLIER)
      var effectiveTimeout = Whizbang.Testing.TestTimeouts.Scale(timeoutMilliseconds);
      await completionSource.Task.WaitAsync(TimeSpan.FromMilliseconds(effectiveTimeout));
    } finally {
      // Always unregister, even if timeout occurs
      registry.Unregister<TMessage>(receptor, stage);
    }

    return receptor;
  }
}
