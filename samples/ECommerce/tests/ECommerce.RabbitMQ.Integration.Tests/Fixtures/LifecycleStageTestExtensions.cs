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
  /// Waits for ImmediateAsync lifecycle stage to complete.
  /// Fires immediately after receptor HandleAsync() returns, before database operations.
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForImmediateAsyncAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 5000)
    where TMessage : IMessage {

    return await WaitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.ImmediateAsync,
      timeoutMilliseconds);
  }

  /// <summary>
  /// Waits for PreDistributeInline lifecycle stage to complete.
  /// Fires before ProcessWorkBatchAsync() call (blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPreDistributeInlineAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 10000)
    where TMessage : IMessage {

    return await WaitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PreDistributeInline,
      timeoutMilliseconds);
  }

  /// <summary>
  /// Waits for PreDistributeAsync lifecycle stage to complete.
  /// Fires before ProcessWorkBatchAsync() call (non-blocking, backgrounded).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPreDistributeAsyncAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 10000)
    where TMessage : IMessage {

    return await WaitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PreDistributeAsync,
      timeoutMilliseconds);
  }

  /// <summary>
  /// Waits for DistributeAsync lifecycle stage to complete.
  /// Fires in parallel with ProcessWorkBatchAsync() (non-blocking, backgrounded).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForDistributeAsyncAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 10000)
    where TMessage : IMessage {

    return await WaitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.DistributeAsync,
      timeoutMilliseconds);
  }

  /// <summary>
  /// Waits for PostDistributeInline lifecycle stage to complete.
  /// Fires after ProcessWorkBatchAsync() completes (blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPostDistributeInlineAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 10000)
    where TMessage : IMessage {

    return await WaitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PostDistributeInline,
      timeoutMilliseconds);
  }

  /// <summary>
  /// Waits for PostDistributeAsync lifecycle stage to complete.
  /// Fires after ProcessWorkBatchAsync() completes (non-blocking, backgrounded).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPostDistributeAsyncAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 10000)
    where TMessage : IMessage {

    return await WaitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PostDistributeAsync,
      timeoutMilliseconds);
  }

  /// <summary>
  /// Waits for PreOutboxInline lifecycle stage to complete.
  /// Fires before publishing message to transport (blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPreOutboxInlineAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 15000)
    where TMessage : IMessage {

    return await WaitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PreOutboxInline,
      timeoutMilliseconds);
  }

  /// <summary>
  /// Waits for PreOutboxAsync lifecycle stage to complete.
  /// Fires parallel with transport publish (non-blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPreOutboxAsyncAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 15000)
    where TMessage : IMessage {

    return await WaitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PreOutboxAsync,
      timeoutMilliseconds);
  }

  /// <summary>
  /// Waits for PostOutboxInline lifecycle stage to complete.
  /// Fires after message published to transport (blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPostOutboxInlineAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 15000)
    where TMessage : IMessage {

    return await WaitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PostOutboxInline,
      timeoutMilliseconds);
  }

  /// <summary>
  /// Waits for PostOutboxAsync lifecycle stage to complete.
  /// Fires after message published to transport (non-blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPostOutboxAsyncAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 15000)
    where TMessage : IMessage {

    return await WaitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PostOutboxAsync,
      timeoutMilliseconds);
  }

  /// <summary>
  /// Waits for PreInboxInline lifecycle stage to complete.
  /// Fires before invoking local receptor for received message (blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPreInboxInlineAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 15000)
    where TMessage : IMessage {

    return await WaitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PreInboxInline,
      timeoutMilliseconds);
  }

  /// <summary>
  /// Waits for PreInboxAsync lifecycle stage to complete.
  /// Fires parallel with receptor invocation (non-blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPreInboxAsyncAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 15000)
    where TMessage : IMessage {

    return await WaitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PreInboxAsync,
      timeoutMilliseconds);
  }

  /// <summary>
  /// Waits for PostInboxInline lifecycle stage to complete.
  /// Fires after receptor completes (blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPostInboxInlineAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 15000)
    where TMessage : IMessage {

    return await WaitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PostInboxInline,
      timeoutMilliseconds);
  }

  /// <summary>
  /// Waits for PostInboxAsync lifecycle stage to complete.
  /// Fires after receptor completes (non-blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForPostInboxAsyncAsync<TMessage>(
    this IHost host,
    int timeoutMilliseconds = 15000)
    where TMessage : IMessage {

    return await WaitForLifecycleStageAsync<TMessage>(
      host,
      LifecycleStage.PostInboxAsync,
      timeoutMilliseconds);
  }

  /// <summary>
  /// Waits for PrePerspectiveInline lifecycle stage to complete.
  /// Fires before perspective RunAsync() processes events (blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TEvent>> WaitForPrePerspectiveInlineAsync<TEvent>(
    this IHost host,
    string? perspectiveName = null,
    int timeoutMilliseconds = 15000)
    where TEvent : IEvent {

    return await WaitForLifecycleStageAsync<TEvent>(
      host,
      LifecycleStage.PrePerspectiveInline,
      timeoutMilliseconds,
      perspectiveName);
  }

  /// <summary>
  /// Waits for PrePerspectiveAsync lifecycle stage to complete.
  /// Fires parallel with perspective RunAsync() (non-blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TEvent>> WaitForPrePerspectiveAsyncAsync<TEvent>(
    this IHost host,
    string? perspectiveName = null,
    int timeoutMilliseconds = 15000)
    where TEvent : IEvent {

    return await WaitForLifecycleStageAsync<TEvent>(
      host,
      LifecycleStage.PrePerspectiveAsync,
      timeoutMilliseconds,
      perspectiveName);
  }

  /// <summary>
  /// Waits for PostPerspectiveAsync lifecycle stage to complete.
  /// Fires after perspective completes, before checkpoint reported (non-blocking).
  /// </summary>
  public static async Task<GenericLifecycleCompletionReceptor<TEvent>> WaitForPostPerspectiveAsyncAsync<TEvent>(
    this IHost host,
    string? perspectiveName = null,
    int timeoutMilliseconds = 15000)
    where TEvent : IEvent {

    return await WaitForLifecycleStageAsync<TEvent>(
      host,
      LifecycleStage.PostPerspectiveAsync,
      timeoutMilliseconds,
      perspectiveName);
  }

  /// <summary>
  /// Core helper method that registers a receptor, waits for completion, and returns the receptor.
  /// Returns the receptor so tests can inspect invocation count, last message, etc.
  /// </summary>
  private static async Task<GenericLifecycleCompletionReceptor<TMessage>> WaitForLifecycleStageAsync<TMessage>(
    IHost host,
    LifecycleStage stage,
    int timeoutMilliseconds,
    string? perspectiveName = null)
    where TMessage : IMessage {

    ArgumentNullException.ThrowIfNull(host);

    // Create completion source for signaling
    var completionSource = new TaskCompletionSource<bool>();

    // Create receptor that will signal completion
    var receptor = new GenericLifecycleCompletionReceptor<TMessage>(
      completionSource,
      expectedStage: stage,
      perspectiveName: perspectiveName);

    // Get registry from host
    var registry = host.Services.GetRequiredService<ILifecycleReceptorRegistry>();

    // Register receptor at specified stage
    registry.Register<TMessage>(receptor, stage);

    try {
      // Wait for completion with timeout
      await completionSource.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMilliseconds));
    } finally {
      // Always unregister, even if timeout occurs
      registry.Unregister<TMessage>(receptor, stage);
    }

    return receptor;
  }
}
