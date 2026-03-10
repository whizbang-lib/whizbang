using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Whizbang.Core;
using Whizbang.Core.Async;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Testing.Lifecycle;

/// <summary>
/// Awaiter for lifecycle stage completion with proper async safety.
/// Encapsulates TaskCompletionSource creation with RunContinuationsAsynchronously to prevent deadlocks.
/// </summary>
/// <typeparam name="TMessage">The message type to wait for.</typeparam>
public sealed class LifecycleStageAwaiter<TMessage> : IAwaiterIdentity, IDisposable
  where TMessage : IMessage {

  private readonly TaskCompletionSource<TMessage> _tcs =
    new(TaskCreationOptions.RunContinuationsAsynchronously);

  public Guid AwaiterId { get; } = TrackedGuid.NewMedo();

  private readonly ILifecycleReceptorRegistry _registry;
  private readonly LifecycleStage _stage;
  private readonly LifecycleCompletionReceptor<TMessage> _receptor;
  private bool _disposed;

  /// <summary>
  /// Gets the number of times the receptor has been invoked.
  /// </summary>
  public int InvocationCount => _receptor.InvocationCount;

  /// <summary>
  /// Gets the last message received.
  /// </summary>
  public TMessage? LastMessage => _receptor.LastMessage;

  /// <summary>
  /// Creates a new lifecycle stage awaiter.
  /// </summary>
  /// <param name="host">The host containing the ILifecycleReceptorRegistry.</param>
  /// <param name="stage">The lifecycle stage to wait for.</param>
  /// <param name="perspectiveName">Optional perspective name to filter by.</param>
  /// <param name="messageFilter">Optional message filter predicate.</param>
  /// <param name="skipInboxForDistributeStages">
  /// When true, Distribute stage handlers skip Inbox-sourced messages to avoid duplicate counts.
  /// Default is true for Distribute stages, preventing the same event from being counted twice
  /// (once when published via Outbox, again when received via Inbox).
  /// </param>
  public LifecycleStageAwaiter(
    IHost host,
    LifecycleStage stage,
    string? perspectiveName = null,
    Func<TMessage, bool>? messageFilter = null,
    bool skipInboxForDistributeStages = true) {

    ArgumentNullException.ThrowIfNull(host);

    _registry = host.Services.GetRequiredService<ILifecycleReceptorRegistry>();
    _stage = stage;
    _receptor = new LifecycleCompletionReceptor<TMessage>(
      _tcs,
      perspectiveName,
      messageFilter,
      expectedStage: stage,
      skipInboxForDistributeStages);

    // Register immediately
    _registry.Register<TMessage>(_receptor, stage);
  }

  /// <summary>
  /// Waits for the lifecycle stage to complete.
  /// </summary>
  /// <param name="timeout">Maximum time to wait.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The message that triggered completion.</returns>
  /// <exception cref="TimeoutException">Thrown if not completed within timeout.</exception>
  public async Task<TMessage> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default) {
    return await AsyncTimeoutHelper.WaitWithTimeoutAsync(
        _tcs.Task, timeout, $"Lifecycle stage {_stage} not completed within {timeout}", cancellationToken);
  }

  /// <summary>
  /// Waits for the lifecycle stage to complete with default timeout.
  /// </summary>
  /// <param name="timeoutMilliseconds">Maximum time to wait in milliseconds.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The message that triggered completion.</returns>
  public Task<TMessage> WaitAsync(int timeoutMilliseconds = 15000, CancellationToken cancellationToken = default) {
    return WaitAsync(TimeSpan.FromMilliseconds(timeoutMilliseconds), cancellationToken);
  }

  /// <inheritdoc />
  public void Dispose() {
    if (_disposed) {
      return;
    }

    _registry.Unregister<TMessage>(_receptor, _stage);
    _disposed = true;
  }
}

/// <summary>
/// Internal receptor that signals completion when invoked.
/// </summary>
internal sealed class LifecycleCompletionReceptor<TMessage> : IReceptor<TMessage>, IAcceptsLifecycleContext
  where TMessage : IMessage {

  private readonly TaskCompletionSource<TMessage> _tcs;
  private readonly string? _perspectiveName;
  private readonly Func<TMessage, bool>? _messageFilter;
  private readonly LifecycleStage? _expectedStage;
  private readonly bool _skipInboxForDistributeStages;
  private static readonly AsyncLocal<ILifecycleContext?> _asyncLocalContext = new();
  private int _invocationCount;

  public int InvocationCount => _invocationCount;
  public TMessage? LastMessage { get; private set; }

  // CA1822: Uses static AsyncLocal but needs instance access for interface pattern
#pragma warning disable CA1822
  public ILifecycleContext? LastLifecycleContext => _asyncLocalContext.Value;
#pragma warning restore CA1822

  public LifecycleCompletionReceptor(
    TaskCompletionSource<TMessage> tcs,
    string? perspectiveName = null,
    Func<TMessage, bool>? messageFilter = null,
    LifecycleStage? expectedStage = null,
    bool skipInboxForDistributeStages = true) {
    _tcs = tcs;
    _perspectiveName = perspectiveName;
    _messageFilter = messageFilter;
    _expectedStage = expectedStage;
    _skipInboxForDistributeStages = skipInboxForDistributeStages;
  }

  public ValueTask HandleAsync(TMessage message, CancellationToken cancellationToken = default) {
    LastMessage = message;

    // Apply message filter
    if (_messageFilter is not null && !_messageFilter(message)) {
      return ValueTask.CompletedTask;
    }

    // Validate stage if specified (allows cross-stage registration debugging)
    if (_expectedStage.HasValue && LastLifecycleContext?.CurrentStage != _expectedStage.Value) {
      return ValueTask.CompletedTask;
    }

    // Filter by perspective name if specified
    if (_perspectiveName is not null &&
        LastLifecycleContext?.PerspectiveType?.Name != _perspectiveName) {
      return ValueTask.CompletedTask;
    }

    // CRITICAL FIX: For Distribute stages, only count Outbox invocations (publishing)
    // Skip Inbox invocations (receiving from transport) to avoid duplicate counts.
    // This prevents counting the same event twice: once when published, again when received.
    if (_skipInboxForDistributeStages && LastLifecycleContext is not null) {
      var isDistributeStage = LastLifecycleContext.CurrentStage == LifecycleStage.PreDistributeInline ||
                              LastLifecycleContext.CurrentStage == LifecycleStage.PreDistributeAsync ||
                              LastLifecycleContext.CurrentStage == LifecycleStage.DistributeAsync ||
                              LastLifecycleContext.CurrentStage == LifecycleStage.PostDistributeAsync ||
                              LastLifecycleContext.CurrentStage == LifecycleStage.PostDistributeInline;

      if (isDistributeStage && LastLifecycleContext.MessageSource == MessageSource.Inbox) {
        return ValueTask.CompletedTask;
      }
    }

    Interlocked.Increment(ref _invocationCount);
    _tcs.TrySetResult(message);
    return ValueTask.CompletedTask;
  }

  public void SetLifecycleContext(ILifecycleContext context) {
    _asyncLocalContext.Value = context;
  }
}

/// <summary>
/// Factory for creating lifecycle stage awaiters with fluent API.
/// </summary>
public static class LifecycleAwaiter {
  /// <summary>
  /// Creates an awaiter for a specific lifecycle stage.
  /// </summary>
  public static LifecycleStageAwaiter<TMessage> For<TMessage>(
    IHost host,
    LifecycleStage stage,
    string? perspectiveName = null,
    Func<TMessage, bool>? messageFilter = null,
    bool skipInboxForDistributeStages = true)
    where TMessage : IMessage {
    return new LifecycleStageAwaiter<TMessage>(host, stage, perspectiveName, messageFilter, skipInboxForDistributeStages);
  }

  /// <summary>
  /// Creates an awaiter for PostPerspectiveInline (most common test synchronization point).
  /// </summary>
  public static LifecycleStageAwaiter<TEvent> ForPerspectiveCompletion<TEvent>(
    IHost host,
    string? perspectiveName = null)
    where TEvent : IEvent {
    return new LifecycleStageAwaiter<TEvent>(host, LifecycleStage.PostPerspectiveInline, perspectiveName);
  }

  /// <summary>
  /// Creates an awaiter for PrePerspectiveInline (before perspective runs).
  /// </summary>
  public static LifecycleStageAwaiter<TEvent> ForPrePerspective<TEvent>(
    IHost host,
    string? perspectiveName = null)
    where TEvent : IEvent {
    return new LifecycleStageAwaiter<TEvent>(host, LifecycleStage.PrePerspectiveInline, perspectiveName);
  }

  /// <summary>
  /// Creates an awaiter for ImmediateAsync (fires right after command handler returns).
  /// </summary>
  public static LifecycleStageAwaiter<TCommand> ForImmediateAsync<TCommand>(IHost host)
    where TCommand : ICommand {
    return new LifecycleStageAwaiter<TCommand>(host, LifecycleStage.ImmediateAsync);
  }

  /// <summary>
  /// Creates an awaiter for PostDistributeInline (after ProcessWorkBatchAsync completes).
  /// Automatically skips Inbox-sourced messages to avoid duplicate counts.
  /// </summary>
  public static LifecycleStageAwaiter<TEvent> ForPostDistribute<TEvent>(IHost host)
    where TEvent : IEvent {
    return new LifecycleStageAwaiter<TEvent>(host, LifecycleStage.PostDistributeInline);
  }

  /// <summary>
  /// Creates an awaiter for PreDistributeInline (before ProcessWorkBatchAsync).
  /// Automatically skips Inbox-sourced messages to avoid duplicate counts.
  /// </summary>
  public static LifecycleStageAwaiter<TEvent> ForPreDistribute<TEvent>(IHost host)
    where TEvent : IEvent {
    return new LifecycleStageAwaiter<TEvent>(host, LifecycleStage.PreDistributeInline);
  }

  /// <summary>
  /// Creates an awaiter for PostOutboxInline (after message is published to transport).
  /// </summary>
  public static LifecycleStageAwaiter<TEvent> ForPostOutbox<TEvent>(IHost host)
    where TEvent : IEvent {
    return new LifecycleStageAwaiter<TEvent>(host, LifecycleStage.PostOutboxInline);
  }

  /// <summary>
  /// Creates an awaiter for PostInboxInline (after receptor completes processing).
  /// </summary>
  public static LifecycleStageAwaiter<TEvent> ForPostInbox<TEvent>(IHost host)
    where TEvent : IEvent {
    return new LifecycleStageAwaiter<TEvent>(host, LifecycleStage.PostInboxInline);
  }
}
