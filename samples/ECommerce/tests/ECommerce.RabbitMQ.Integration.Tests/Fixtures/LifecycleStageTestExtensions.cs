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
    int timeoutMilliseconds = 45000,
    Func<TMessage, bool>? messageFilter = null,
    CancellationToken cancellationToken = default)
    where TMessage : IMessage {

    return await _waitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.ImmediateDetached,
      timeoutMilliseconds,
      messageFilter: messageFilter,
      cancellationToken: cancellationToken);
  }

  /// <summary>
  /// Waits for PreDistributeInline lifecycle stage to complete.
  /// Fires before ProcessWorkBatchAsync() call (blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPreDistributeInlineAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 45000,
    Func<TMessage, bool>? messageFilter = null,
    CancellationToken cancellationToken = default)
    where TMessage : IMessage {

    return await _waitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PreDistributeInline,
      timeoutMilliseconds,
      messageFilter: messageFilter,
      cancellationToken: cancellationToken);
  }

  /// <summary>
  /// Waits for PreDistributeDetached lifecycle stage to complete.
  /// Fires before ProcessWorkBatchAsync() call (non-blocking, backgrounded).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPreDistributeDetachedAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 45000,
    Func<TMessage, bool>? messageFilter = null,
    CancellationToken cancellationToken = default)
    where TMessage : IMessage {

    return await _waitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PreDistributeDetached,
      timeoutMilliseconds,
      messageFilter: messageFilter,
      cancellationToken: cancellationToken);
  }

  /// <summary>
  /// Waits for DistributeDetached lifecycle stage to complete.
  /// Fires in parallel with ProcessWorkBatchAsync() (non-blocking, backgrounded).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForDistributeDetachedAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 45000,
    Func<TMessage, bool>? messageFilter = null,
    CancellationToken cancellationToken = default)
    where TMessage : IMessage {

    return await _waitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.DistributeDetached,
      timeoutMilliseconds,
      messageFilter: messageFilter,
      cancellationToken: cancellationToken);
  }

  /// <summary>
  /// Waits for PostDistributeInline lifecycle stage to complete.
  /// Fires after ProcessWorkBatchAsync() completes (blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPostDistributeInlineAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 45000,
    Func<TMessage, bool>? messageFilter = null,
    CancellationToken cancellationToken = default)
    where TMessage : IMessage {

    return await _waitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PostDistributeInline,
      timeoutMilliseconds,
      messageFilter: messageFilter,
      cancellationToken: cancellationToken);
  }

  /// <summary>
  /// Waits for PostDistributeDetached lifecycle stage to complete.
  /// Fires after ProcessWorkBatchAsync() completes (non-blocking, backgrounded).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPostDistributeDetachedAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 45000,
    Func<TMessage, bool>? messageFilter = null,
    CancellationToken cancellationToken = default)
    where TMessage : IMessage {

    return await _waitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PostDistributeDetached,
      timeoutMilliseconds,
      messageFilter: messageFilter,
      cancellationToken: cancellationToken);
  }

  /// <summary>
  /// Waits for PreOutboxInline lifecycle stage to complete.
  /// Fires before publishing message to transport (blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPreOutboxInlineAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 45000,
    Func<TMessage, bool>? messageFilter = null,
    CancellationToken cancellationToken = default)
    where TMessage : IMessage {

    return await _waitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PreOutboxInline,
      timeoutMilliseconds,
      messageFilter: messageFilter,
      cancellationToken: cancellationToken);
  }

  /// <summary>
  /// Waits for PreOutboxDetached lifecycle stage to complete.
  /// Fires parallel with transport publish (non-blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPreOutboxDetachedAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 45000,
    Func<TMessage, bool>? messageFilter = null,
    CancellationToken cancellationToken = default)
    where TMessage : IMessage {

    return await _waitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PreOutboxDetached,
      timeoutMilliseconds,
      messageFilter: messageFilter,
      cancellationToken: cancellationToken);
  }

  /// <summary>
  /// Waits for PostOutboxInline lifecycle stage to complete.
  /// Fires after message published to transport (blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPostOutboxInlineAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 45000,
    Func<TMessage, bool>? messageFilter = null,
    CancellationToken cancellationToken = default)
    where TMessage : IMessage {

    return await _waitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PostOutboxInline,
      timeoutMilliseconds,
      messageFilter: messageFilter,
      cancellationToken: cancellationToken);
  }

  /// <summary>
  /// Waits for PostOutboxDetached lifecycle stage to complete.
  /// Fires after message published to transport (non-blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPostOutboxDetachedAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 45000,
    Func<TMessage, bool>? messageFilter = null,
    CancellationToken cancellationToken = default)
    where TMessage : IMessage {

    return await _waitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PostOutboxDetached,
      timeoutMilliseconds,
      messageFilter: messageFilter,
      cancellationToken: cancellationToken);
  }

  /// <summary>
  /// Waits for PreInboxInline lifecycle stage to complete.
  /// Fires before invoking local receptor for received message (blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPreInboxInlineAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 45000,
    Func<TMessage, bool>? messageFilter = null,
    CancellationToken cancellationToken = default)
    where TMessage : IMessage {

    return await _waitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PreInboxInline,
      timeoutMilliseconds,
      messageFilter: messageFilter,
      cancellationToken: cancellationToken);
  }

  /// <summary>
  /// Waits for PreInboxDetached lifecycle stage to complete.
  /// Fires parallel with receptor invocation (non-blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPreInboxDetachedAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 45000,
    Func<TMessage, bool>? messageFilter = null,
    CancellationToken cancellationToken = default)
    where TMessage : IMessage {

    return await _waitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PreInboxDetached,
      timeoutMilliseconds,
      messageFilter: messageFilter,
      cancellationToken: cancellationToken);
  }

  /// <summary>
  /// Waits for PostInboxInline lifecycle stage to complete.
  /// Fires after receptor completes (blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPostInboxInlineAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 45000,
    Func<TMessage, bool>? messageFilter = null,
    CancellationToken cancellationToken = default)
    where TMessage : IMessage {

    return await _waitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PostInboxInline,
      timeoutMilliseconds,
      messageFilter: messageFilter,
      cancellationToken: cancellationToken);
  }

  /// <summary>
  /// Waits for PostInboxDetached lifecycle stage to complete.
  /// Fires after receptor completes (non-blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPostInboxDetachedAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 45000,
    Func<TMessage, bool>? messageFilter = null,
    CancellationToken cancellationToken = default)
    where TMessage : IMessage {

    return await _waitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PostInboxDetached,
      timeoutMilliseconds,
      messageFilter: messageFilter,
      cancellationToken: cancellationToken);
  }

  /// <summary>
  /// Waits for PrePerspectiveInline lifecycle stage to complete.
  /// Fires before perspective RunAsync() processes events (blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TEvent>> WaitForPrePerspectiveInlineAsync<TEvent>(
    this IHost host,
    string? perspectiveName = null,
    int timeoutMilliseconds = 45000,
    Func<TEvent, bool>? messageFilter = null,
    CancellationToken cancellationToken = default)
    where TEvent : IEvent {

    return await _waitForLifecycleStageAsync<TEvent>(
      host,
      LifecycleStage.PrePerspectiveInline,
      timeoutMilliseconds,
      perspectiveName,
      messageFilter,
      cancellationToken);
  }

  /// <summary>
  /// Waits for PrePerspectiveDetached lifecycle stage to complete.
  /// Fires parallel with perspective RunAsync() (non-blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TEvent>> WaitForPrePerspectiveDetachedAsync<TEvent>(
    this IHost host,
    string? perspectiveName = null,
    int timeoutMilliseconds = 45000,
    Func<TEvent, bool>? messageFilter = null,
    CancellationToken cancellationToken = default)
    where TEvent : IEvent {

    return await _waitForLifecycleStageAsync<TEvent>(
      host,
      LifecycleStage.PrePerspectiveDetached,
      timeoutMilliseconds,
      perspectiveName,
      messageFilter,
      cancellationToken);
  }

  /// <summary>
  /// Waits for PostPerspectiveDetached lifecycle stage to complete.
  /// Fires after perspective completes, before checkpoint reported (non-blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TEvent>> WaitForPostPerspectiveDetachedAsync<TEvent>(
    this IHost host,
    string? perspectiveName = null,
    int timeoutMilliseconds = 45000,
    Func<TEvent, bool>? messageFilter = null,
    CancellationToken cancellationToken = default)
    where TEvent : IEvent {

    return await _waitForLifecycleStageAsync<TEvent>(
      host,
      LifecycleStage.PostPerspectiveDetached,
      timeoutMilliseconds,
      perspectiveName,
      messageFilter,
      cancellationToken);
  }

  /// <summary>
  /// Waits for PostPerspectiveInline lifecycle stage to complete.
  /// Fires after perspective completes, blocking checkpoint report (blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TEvent>> WaitForPostPerspectiveInlineAsync<TEvent>(
    this IHost host,
    string? perspectiveName = null,
    int timeoutMilliseconds = 45000,
    Func<TEvent, bool>? messageFilter = null,
    CancellationToken cancellationToken = default)
    where TEvent : IEvent {

    return await _waitForLifecycleStageAsync<TEvent>(
      host,
      LifecycleStage.PostPerspectiveInline,
      timeoutMilliseconds,
      perspectiveName,
      messageFilter,
      cancellationToken);
  }

  /// <summary>
  /// Core helper method that registers a receptor, waits for completion, and returns the receptor.
  /// Returns the receptor so tests can inspect invocation count, last message, etc.
  /// </summary>
  /// <param name="messageFilter">Optional predicate that gates the completion signal. When supplied,
  /// the receptor still records LastMessage / InvocationCount, but only signals completion when the
  /// filter returns true. Use this to ignore stale messages that survive shared-fixture cleanup
  /// (in-flight events from a prior test arriving after queue purge but before the consumer flushes).</param>
  /// <param name="cancellationToken">When supplied (i.e., a CT that <c>CanBeCanceled</c>), the
  /// helper waits ONLY on that CT — the per-helper <paramref name="timeoutMilliseconds"/> is
  /// ignored. This is the deterministic-test path: tests inject the per-method CancellationToken
  /// from <c>[Timeout(N)]</c> so the test-framework deadline is the single source of truth.
  /// When omitted (default CT), the helper falls back to the internal scaled timer for backward
  /// compatibility with callers that don't plumb the CT through.</param>
  private static async Task<GenericLifecycleCompletionReceptor<TMessage>> _waitForLifecycleStageAsync<TMessage>(
    IHost host,
    LifecycleStage stage,
    int timeoutMilliseconds,
    string? perspectiveName = null,
    Func<TMessage, bool>? messageFilter = null,
    CancellationToken cancellationToken = default)
    where TMessage : IMessage {

    ArgumentNullException.ThrowIfNull(host);

    // Create completion source for signaling
    // CRITICAL: Use RunContinuationsAsynchronously to prevent deadlocks
    var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    // Create receptor that will signal completion
    var receptor = new GenericLifecycleCompletionReceptor<TMessage>(
      completionSource,
      expectedStage: stage,
      perspectiveName: perspectiveName,
      messageFilter: messageFilter);

    // Get registry from host
    var registry = host.Services.GetRequiredService<IReceptorRegistry>();

    // Register receptor at specified stage
    registry.Register<TMessage>(receptor, stage);

    try {
      if (cancellationToken.CanBeCanceled) {
        // Deterministic path: only the caller's CT bounds the wait. The
        // test-framework deadline (e.g. [Timeout(180_000)]) cancels the CT;
        // no separate internal timer that could fire first and produce
        // spurious flakes when CI is slow under load.
        await completionSource.Task.WaitAsync(cancellationToken);
      } else {
        // Legacy/back-compat path for callers that don't plumb their CT
        // through — fall back to the scaled internal timer.
        var effectiveTimeout = Whizbang.Testing.TestTimeouts.Scale(timeoutMilliseconds);
        await completionSource.Task.WaitAsync(TimeSpan.FromMilliseconds(effectiveTimeout));
      }
    } finally {
      // Always unregister, even if timeout / cancellation occurs
      registry.Unregister<TMessage>(receptor, stage);
    }

    return receptor;
  }
}
