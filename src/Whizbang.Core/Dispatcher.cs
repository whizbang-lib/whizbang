using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Transports;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Core;

/// <summary>
/// Delegate for invoking a receptor's ReceiveAsync method.
/// Generated code creates these delegates with proper type safety - zero reflection.
/// </summary>
public delegate ValueTask<TResult> ReceptorInvoker<TResult>(object message);

/// <summary>
/// Delegate for invoking a void receptor's ReceiveAsync method without returning a result.
/// Generated code creates these delegates with proper type safety - zero reflection.
/// Enables zero-allocation pattern for command/event handling.
/// </summary>
public delegate ValueTask VoidReceptorInvoker(object message);

/// <summary>
/// Delegate for invoking multiple receptors for publish operations.
/// Generated code creates these delegates with proper type safety - zero reflection.
/// </summary>
public delegate Task ReceptorPublisher<in TEvent>(TEvent eventData);

/// <summary>
/// Delegate for invoking a synchronous receptor's Handle method.
/// Generated code creates these delegates with proper type safety - zero reflection.
/// The result is wrapped in a pre-completed ValueTask for uniform handling.
/// </summary>
/// <docs>core-concepts/receptors#synchronous-receptors</docs>
public delegate TResult SyncReceptorInvoker<out TResult>(object message);

/// <summary>
/// Delegate for invoking a void synchronous receptor's Handle method.
/// Generated code creates these delegates with proper type safety - zero reflection.
/// </summary>
/// <docs>core-concepts/receptors#synchronous-receptors</docs>
public delegate void VoidSyncReceptorInvoker(object message);

/// <summary>
/// Base dispatcher class with core logic. The source generator creates a derived class
/// that implements the abstract lookup methods, returning strongly-typed delegates.
/// This achieves zero-reflection while keeping functional logic in the base class.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Integration/DispatcherReceptorIntegrationTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "Parameters 'jsonOptions' and 'receptorInvoker' retained for backward compatibility with generated code")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "S1172:Unused method parameters should be removed", Justification = "Parameters 'jsonOptions' and 'receptorInvoker' retained for backward compatibility with generated code")]
public abstract class Dispatcher(
  IServiceProvider serviceProvider,
  IServiceInstanceProvider instanceProvider,
  ITraceStore? traceStore = null,
  JsonSerializerOptions? jsonOptions = null,
  ITopicRegistry? topicRegistry = null,
  ITopicRoutingStrategy? topicRoutingStrategy = null,
  IReceptorInvoker? receptorInvoker = null,
  IEnvelopeSerializer? envelopeSerializer = null,
  IEnvelopeRegistry? envelopeRegistry = null,
  IOutboxRoutingStrategy? outboxRoutingStrategy = null,
  ILifecycleInvoker? lifecycleInvoker = null,
  IStreamIdExtractor? streamIdExtractor = null,
  IReceptorRegistry? receptorRegistry = null
  ) : IDispatcher {
  private readonly IServiceProvider _internalServiceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
  private readonly IServiceScopeFactory _scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
  private readonly IServiceInstanceProvider _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
  private readonly ITraceStore? _traceStore = traceStore;
  private readonly ITopicRegistry? _topicRegistry = topicRegistry;
  private readonly ITopicRoutingStrategy _topicRoutingStrategy = topicRoutingStrategy ?? PassthroughRoutingStrategy.Instance;
  // NOTE: receptorInvoker parameter retained for API compatibility but not used
  // IReceptorInvoker is now scoped and resolved by workers, not the Dispatcher
  private readonly IReceptorRegistry? _receptorRegistry = receptorRegistry ?? serviceProvider.GetService<IReceptorRegistry>();
  private readonly IStreamIdExtractor? _streamIdExtractor = streamIdExtractor ?? serviceProvider.GetService<IStreamIdExtractor>();
  // Lifecycle invoker for runtime-registered receptors (test infrastructure, observers)
  private readonly ILifecycleInvoker? _lifecycleInvoker = lifecycleInvoker ?? serviceProvider.GetService<ILifecycleInvoker>();
  // Resolve from service provider if not injected (for backwards compatibility with generated code)
  private readonly IEnvelopeSerializer? _envelopeSerializer = envelopeSerializer ?? serviceProvider.GetService<IEnvelopeSerializer>();
  // Resolve from service provider if not injected (for backwards compatibility with generated code)
  private readonly IEnvelopeRegistry? _envelopeRegistry = envelopeRegistry ?? serviceProvider.GetService<IEnvelopeRegistry>();
  // Outbox routing strategy for determining actual transport destinations (inbox for commands, namespace for events)
  private readonly IOutboxRoutingStrategy? _outboxRoutingStrategy = outboxRoutingStrategy ?? serviceProvider.GetService<IOutboxRoutingStrategy>();
  // Owned domains for routing decisions - resolved from RoutingOptions if available
  private readonly HashSet<string> _ownedDomains = _resolveOwnedDomains(serviceProvider);
  // Security context accessor is resolved lazily from scope - it's a scoped service
  // DO NOT resolve in constructor - will fail with "Cannot resolve scoped service from root provider"

  // Unused parameters retained for backward compatibility with generated code
  private readonly JsonSerializerOptions? _ = jsonOptions;
  private readonly IReceptorInvoker? __ = receptorInvoker;

  // Lazy-resolved logger for diagnostic tracing (avoids constructor changes)
  private ILogger? _cascadeLogger;
#pragma warning disable IDE1006 // Naming rule - property follows internal naming convention
  private ILogger CascadeLogger => _cascadeLogger ??= _internalServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("Whizbang.Core.Dispatcher.Cascade") ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
#pragma warning restore IDE1006

  /// <summary>
  /// Resolves owned domains from RoutingOptions in DI container.
  /// </summary>
  private static HashSet<string> _resolveOwnedDomains(IServiceProvider sp) {
    var routingOptions = sp.GetService<Microsoft.Extensions.Options.IOptions<RoutingOptions>>()?.Value;
    return routingOptions?.OwnedDomains?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
  }

  /// <summary>
  /// Extracts security context from the ambient scope if propagation is enabled.
  /// Returns null if no context is available or propagation is disabled.
  /// </summary>
  /// <docs>core-concepts/message-security#automatic-security-propagation</docs>
  /// <tests>Whizbang.Core.Tests/Dispatcher/DispatcherSecurityPropagationTests.cs</tests>
  private static SecurityContext? _getSecurityContextForPropagation() {
    // Use static accessor - IScopeContextAccessor is scoped but AsyncLocal is static
    if (ScopeContextAccessor.CurrentContext is not ImmutableScopeContext ctx) {
      return null;
    }

    if (!ctx.ShouldPropagate) {
      return null;
    }

    return new SecurityContext {
      UserId = ctx.Scope.UserId,
      TenantId = ctx.Scope.TenantId
    };
  }

  /// <summary>
  /// Gets the service provider for receptor resolution.
  /// Available to generated derived class.
  /// </summary>
#pragma warning disable IDE1006 // CA1707 takes precedence - protected properties should not have underscores
  protected IServiceProvider ServiceProvider => _internalServiceProvider;
#pragma warning restore IDE1006

  /// <summary>
  /// Gets the service provider for extension methods (security context, etc.).
  /// Internal access for DispatcherSecurityExtensions.
  /// </summary>
  internal IServiceProvider InternalServiceProvider => _internalServiceProvider;

  // ========================================
  // SEND PATTERN - Command Dispatch with Acknowledgment
  // ========================================

  /// <summary>
  /// Sends a typed message and returns a delivery receipt (AOT-compatible).
  /// Use this for async workflows, remote execution, or inbox pattern.
  /// Type information is preserved at compile time, avoiding reflection.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:Send_WithValidMessage_ShouldReturnDeliveryReceiptAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:Send_WithContext_ShouldPreserveCorrelationIdInReceiptAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:SendAsync_Generic_CreatesTypedEnvelopeForTracingAsync</tests>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message) where TMessage : notnull {
    var context = MessageContext.New();
    return _sendAsyncInternalAsync<TMessage>(message, context);
  }

  /// <summary>
  /// Sends a message and returns a delivery receipt (not the business result).
  /// Creates a new message context automatically.
  /// For AOT compatibility, use the generic overload SendAsync&lt;TMessage&gt;.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public Task<IDeliveryReceipt> SendAsync(object message) {
    var context = MessageContext.New();
    return SendAsync(message, context);
  }

  /// <summary>
  /// Sends a message with an explicit context and returns a delivery receipt.
  /// Uses generated delegate to invoke receptor with zero reflection.
  /// Creates MessageEnvelope with hop for observability.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public async Task<IDeliveryReceipt> SendAsync(
    object message,
    IMessageContext context,
    [CallerMemberName] string callerMemberName = "",
    [CallerFilePath] string callerFilePath = "",
    [CallerLineNumber] int callerLineNumber = 0
  ) {
    ArgumentNullException.ThrowIfNull(message);

    ArgumentNullException.ThrowIfNull(context);

    var messageType = message.GetType();

    // Get strongly-typed delegate from generated code
    var invoker = GetReceptorInvoker<object>(message, messageType);

    // If no local receptor exists, check for work coordinator strategy
    if (invoker == null) {
      // Try strategy-based outbox pattern (new work coordinator pattern)
      // Route to outbox for remote delivery (AOT-compatible, no reflection)
      return await _sendToOutboxViaScopeAsync(message, messageType, context, callerMemberName, callerFilePath, callerLineNumber);
    }

    // Create envelope with hop for observability
    var envelope = _createEnvelope(message, context, callerMemberName, callerFilePath, callerLineNumber);

    // Register envelope so receptor can look it up via IEventStore.AppendAsync(message)
    _envelopeRegistry?.Register(envelope);
    try {
      // Store envelope if trace store is configured
      if (_traceStore != null) {
        await _traceStore.StoreAsync(envelope);
      }

      // Invoke using delegate - zero reflection, strongly typed
      var result = await invoker(message);

      // Auto-cascade: Extract and publish any IEvent instances from result (tuples, arrays, etc.)
      // Pass messageType so we can look up receptor's [DefaultRouting] attribute
      await _cascadeEventsFromResultAsync(result, messageType);

      // NOTE: We do NOT invoke _receptorInvoker here for LocalImmediateInline because:
      // 1. The dispatcher already invokes the business receptor via the generated delegate above
      // 2. Invoking _receptorInvoker would cause double invocation of receptors without [FireAt]
      // 3. IReceptorInvoker is meant for TransportConsumerWorker (PostInbox) and
      //    WorkCoordinatorPublisherWorker (PreOutbox), not for local dispatch

      // Invoke runtime-registered lifecycle receptors (test infrastructure, observers)
      // These are registered via ILifecycleReceptorRegistry, not compile-time [FireAt] attributes
      if (_lifecycleInvoker is not null) {
        var lifecycleContext = new LifecycleExecutionContext {
          CurrentStage = LifecycleStage.ImmediateAsync,
          EventId = null,
          StreamId = null,
          LastProcessedEventId = null,
          MessageSource = MessageSource.Local,
          AttemptNumber = null
        };

        // Fire ImmediateAsync stage (fires after receptor returns, before DB writes)
        await _lifecycleInvoker.InvokeAsync(envelope, LifecycleStage.ImmediateAsync, lifecycleContext, default);

        // Fire LocalImmediateAsync stage (same timing as ImmediateAsync but for local-only receptors)
        lifecycleContext = lifecycleContext with { CurrentStage = LifecycleStage.LocalImmediateAsync };
        await _lifecycleInvoker.InvokeAsync(envelope, LifecycleStage.LocalImmediateAsync, lifecycleContext, default);

        // Fire LocalImmediateInline stage (blocking, local-only)
        lifecycleContext = lifecycleContext with { CurrentStage = LifecycleStage.LocalImmediateInline };
        await _lifecycleInvoker.InvokeAsync(envelope, LifecycleStage.LocalImmediateInline, lifecycleContext, default);
      }
    } finally {
      // Unregister envelope after receptor completes (or throws)
      _envelopeRegistry?.Unregister(envelope);
    }

    // Extract stream ID from [StreamId] attribute for delivery receipt
    var streamId = _streamIdExtractor?.ExtractStreamId(message, messageType);

    // Return delivery receipt
    var destination = messageType.Name; // Will be enhanced with actual receptor name in future
    return DeliveryReceipt.Delivered(
      envelope.MessageId,
      destination,
      context.CorrelationId,
      context.CausationId,
      streamId
    );
  }

  /// <summary>
  /// Sends a typed message with dispatch options (AOT-compatible).
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message, DispatchOptions options) where TMessage : notnull {
    options.CancellationToken.ThrowIfCancellationRequested();
    var context = MessageContext.New();
    return _sendAsyncInternalWithOptionsAsync<TMessage>(message, context, options);
  }

  /// <summary>
  /// Sends a message with dispatch options.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public Task<IDeliveryReceipt> SendAsync(object message, DispatchOptions options) {
    options.CancellationToken.ThrowIfCancellationRequested();
    var context = MessageContext.New();
    return SendAsync(message, context, options);
  }

  /// <summary>
  /// Sends a message with explicit context and dispatch options.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public async Task<IDeliveryReceipt> SendAsync(
    object message,
    IMessageContext context,
    DispatchOptions options,
    [CallerMemberName] string callerMemberName = "",
    [CallerFilePath] string callerFilePath = "",
    [CallerLineNumber] int callerLineNumber = 0
  ) {
    options.CancellationToken.ThrowIfCancellationRequested();
    ArgumentNullException.ThrowIfNull(message);
    ArgumentNullException.ThrowIfNull(context);

    var messageType = message.GetType();
    var invoker = GetReceptorInvoker<object>(message, messageType);

    if (invoker == null) {
      return await _sendToOutboxViaScopeAsync(message, messageType, context, callerMemberName, callerFilePath, callerLineNumber);
    }

    var envelope = _createEnvelope(message, context, callerMemberName, callerFilePath, callerLineNumber);
    _envelopeRegistry?.Register(envelope);
    try {
      if (_traceStore != null) {
        await _traceStore.StoreAsync(envelope, options.CancellationToken);
      }

      options.CancellationToken.ThrowIfCancellationRequested();
      var result = await invoker(message);
      await _cascadeEventsFromResultAsync(result, messageType);

      // NOTE: We do NOT invoke _receptorInvoker here - dispatcher already invoked receptor above
    } finally {
      _envelopeRegistry?.Unregister(envelope);
    }

    // Extract stream ID from [StreamId] attribute for delivery receipt
    var streamId = _streamIdExtractor?.ExtractStreamId(message, messageType);

    var destination = messageType.Name;
    return DeliveryReceipt.Delivered(
      envelope.MessageId,
      destination,
      context.CorrelationId,
      context.CausationId,
      streamId
    );
  }

  /// <summary>
  /// Internal generic implementation of SendAsync that preserves type information.
  /// This method is called by the public SendAsync&lt;TMessage&gt; overload to avoid type erasure.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  [SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance", Justification = "Interface parameter required as this method is called from public API with IMessageContext")]
  private async Task<IDeliveryReceipt> _sendAsyncInternalAsync<TMessage>(
    TMessage message,
    IMessageContext context,
    [CallerMemberName] string callerMemberName = "",
    [CallerFilePath] string callerFilePath = "",
    [CallerLineNumber] int callerLineNumber = 0
  ) where TMessage : notnull {
    ArgumentNullException.ThrowIfNull(message);
    ArgumentNullException.ThrowIfNull(context);

    var messageType = typeof(TMessage);

    // Get strongly-typed delegate from generated code
    var invoker = GetReceptorInvoker<object>(message, messageType);

    // If no local receptor exists, check for work coordinator strategy
    if (invoker == null) {
      // Try strategy-based outbox pattern (new work coordinator pattern)
      // Route to outbox for remote delivery (AOT-compatible, no reflection)
      return await _sendToOutboxViaScopeAsync<TMessage>(message, messageType, context, callerMemberName, callerFilePath, callerLineNumber);
    }

    // Create envelope with hop for observability - generic version preserves type!
    var envelope = _createEnvelope<TMessage>(message, context, callerMemberName, callerFilePath, callerLineNumber);

    // Register envelope so receptor can look it up via IEventStore.AppendAsync(message)
    _envelopeRegistry?.Register(envelope);
    try {
      // Store envelope if trace store is configured
      if (_traceStore != null) {
        await _traceStore.StoreAsync(envelope);
      }

      // Invoke using delegate - zero reflection, strongly typed
      var result = await invoker(message);

      // Auto-cascade: Extract and publish any IEvent instances from result (tuples, arrays, etc.)
      await _cascadeEventsFromResultAsync(result, messageType);

      // NOTE: We do NOT invoke _receptorInvoker here - dispatcher already invoked receptor above

      // Invoke runtime-registered lifecycle receptors (test infrastructure, observers)
      // These are registered via ILifecycleReceptorRegistry, not compile-time [FireAt] attributes
      if (_lifecycleInvoker is not null) {
        var lifecycleContext = new LifecycleExecutionContext {
          CurrentStage = LifecycleStage.ImmediateAsync,
          EventId = null,
          StreamId = null,
          LastProcessedEventId = null,
          MessageSource = MessageSource.Local,
          AttemptNumber = null
        };

        // Fire ImmediateAsync stage (fires after receptor returns, before DB writes)
        await _lifecycleInvoker.InvokeAsync(envelope, LifecycleStage.ImmediateAsync, lifecycleContext, default);

        // Fire LocalImmediateAsync stage (same timing as ImmediateAsync but for local-only receptors)
        lifecycleContext = lifecycleContext with { CurrentStage = LifecycleStage.LocalImmediateAsync };
        await _lifecycleInvoker.InvokeAsync(envelope, LifecycleStage.LocalImmediateAsync, lifecycleContext, default);

        // Fire LocalImmediateInline stage (blocking, local-only)
        lifecycleContext = lifecycleContext with { CurrentStage = LifecycleStage.LocalImmediateInline };
        await _lifecycleInvoker.InvokeAsync(envelope, LifecycleStage.LocalImmediateInline, lifecycleContext, default);
      }
    } finally {
      // Unregister envelope after receptor completes (or throws)
      _envelopeRegistry?.Unregister(envelope);
    }

    // Extract stream ID from [StreamId] attribute for delivery receipt
    var streamId = _streamIdExtractor?.ExtractStreamId(message, messageType);

    // Return delivery receipt
    var destination = messageType.Name; // Will be enhanced with actual receptor name in future
    return DeliveryReceipt.Delivered(
      envelope.MessageId,
      destination,
      context.CorrelationId,
      context.CausationId,
      streamId
    );
  }

  /// <summary>
  /// Internal generic implementation of SendAsync with DispatchOptions that preserves type information.
  /// This method is called by the public SendAsync&lt;TMessage&gt; overload to avoid type erasure.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  [SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance", Justification = "Interface parameter required as this method is called from public API with IMessageContext")]
  private async Task<IDeliveryReceipt> _sendAsyncInternalWithOptionsAsync<TMessage>(
    TMessage message,
    IMessageContext context,
    DispatchOptions options,
    [CallerMemberName] string callerMemberName = "",
    [CallerFilePath] string callerFilePath = "",
    [CallerLineNumber] int callerLineNumber = 0
  ) where TMessage : notnull {
    ArgumentNullException.ThrowIfNull(message);
    ArgumentNullException.ThrowIfNull(context);

    var messageType = typeof(TMessage);
    var invoker = GetReceptorInvoker<object>(message, messageType);

    if (invoker == null) {
      return await _sendToOutboxViaScopeAsync<TMessage>(message, messageType, context, callerMemberName, callerFilePath, callerLineNumber);
    }

    var envelope = _createEnvelope<TMessage>(message, context, callerMemberName, callerFilePath, callerLineNumber);
    _envelopeRegistry?.Register(envelope);
    try {
      if (_traceStore != null) {
        await _traceStore.StoreAsync(envelope, options.CancellationToken);
      }

      options.CancellationToken.ThrowIfCancellationRequested();
      var result = await invoker(message);
      await _cascadeEventsFromResultAsync(result, messageType);

      // NOTE: We do NOT invoke _receptorInvoker here - dispatcher already invoked receptor above

      // Invoke runtime-registered lifecycle receptors (test infrastructure, observers)
      if (_lifecycleInvoker is not null) {
        var lifecycleContext = new LifecycleExecutionContext {
          CurrentStage = LifecycleStage.ImmediateAsync,
          EventId = null,
          StreamId = null,
          LastProcessedEventId = null,
          MessageSource = MessageSource.Local,
          AttemptNumber = null
        };

        await _lifecycleInvoker.InvokeAsync(envelope, LifecycleStage.ImmediateAsync, lifecycleContext, options.CancellationToken);
        lifecycleContext = lifecycleContext with { CurrentStage = LifecycleStage.LocalImmediateAsync };
        await _lifecycleInvoker.InvokeAsync(envelope, LifecycleStage.LocalImmediateAsync, lifecycleContext, options.CancellationToken);
        lifecycleContext = lifecycleContext with { CurrentStage = LifecycleStage.LocalImmediateInline };
        await _lifecycleInvoker.InvokeAsync(envelope, LifecycleStage.LocalImmediateInline, lifecycleContext, options.CancellationToken);
      }
    } finally {
      _envelopeRegistry?.Unregister(envelope);
    }

    // Extract stream ID from [StreamId] attribute for delivery receipt
    var streamId = _streamIdExtractor?.ExtractStreamId(message, messageType);

    var destination = messageType.Name;
    return DeliveryReceipt.Delivered(
      envelope.MessageId,
      destination,
      context.CorrelationId,
      context.CausationId,
      streamId
    );
  }

  // ========================================
  // LOCAL INVOKE PATTERN - In-Process RPC
  // ========================================

  /// <summary>
  /// Invokes a receptor in-process with typed message and returns the typed business result (AOT-compatible).
  /// PERFORMANCE: Zero allocation, target &lt; 20ns per invocation.
  /// RESTRICTION: In-process only - throws InvalidOperationException if used with remote transport.
  /// Type information is preserved at compile time, avoiding reflection.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:LocalInvoke_WithValidMessage_ShouldReturnBusinessResultAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:LocalInvoke_WithContext_ShouldPreserveContextAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:LocalInvokeAsync_DoesNotRequireTypePreservation_ForInProcessRPCAsync</tests>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(TMessage message) where TMessage : notnull {
    var context = MessageContext.New();
    return LocalInvokeAsync<TResult>((object)message, context);
  }

  /// <summary>
  /// Invokes a receptor in-process and returns the typed business result.
  /// Creates a new message context automatically.
  /// PERFORMANCE: Zero allocation when trace store is null, target &lt; 20ns per invocation.
  /// For AOT compatibility, use the generic overload LocalInvokeAsync&lt;TMessage, TResult&gt;.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public ValueTask<TResult> LocalInvokeAsync<TResult>(object message) {
    var context = MessageContext.New();
    return LocalInvokeAsync<TResult>(message, context);
  }

  /// <summary>
  /// Invokes a receptor in-process with typed message and explicit context, returning the typed business result (AOT-compatible).
  /// Type information is preserved at compile time, avoiding reflection.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public ValueTask<TResult> LocalInvokeAsync<TMessage, TResult>(
    TMessage message,
    IMessageContext context,
    [CallerMemberName] string callerMemberName = "",
    [CallerFilePath] string callerFilePath = "",
    [CallerLineNumber] int callerLineNumber = 0
  ) where TMessage : notnull {
    return _localInvokeAsyncInternal<TMessage, TResult>(message, context, callerMemberName, callerFilePath, callerLineNumber);
  }

  /// <summary>
  /// Invokes a receptor in-process with explicit context and returns the typed business result.
  /// Uses generated delegate to invoke receptor with zero reflection.
  /// Skips envelope creation when trace store is null for optimal performance.
  /// PERFORMANCE: Zero allocation fast path for synchronously-completed receptors (no async/await overhead).
  /// For AOT compatibility, use the generic overload LocalInvokeAsync&lt;TMessage, TResult&gt;.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public ValueTask<TResult> LocalInvokeAsync<TResult>(
    object message,
    IMessageContext context,
    [CallerMemberName] string callerMemberName = "",
    [CallerFilePath] string callerFilePath = "",
    [CallerLineNumber] int callerLineNumber = 0
  ) {
    ArgumentNullException.ThrowIfNull(message);

    ArgumentNullException.ThrowIfNull(context);

    var messageType = message.GetType();

    // Try async receptor first (async takes precedence)
    var asyncInvoker = GetReceptorInvoker<TResult>(message, messageType);
    if (asyncInvoker != null) {
      // Use wrapper that catches InvalidCastException and falls back to RPC extraction
      // This handles the case where receptor returns a complex type (tuple, etc.)
      // but caller requests a specific type from within that complex type
      return _localInvokeWithCastFallbackAsync(asyncInvoker, message, messageType, context, callerMemberName, callerFilePath, callerLineNumber);
    }

    // Fallback to sync receptor
    var syncInvoker = GetSyncReceptorInvoker<TResult>(message, messageType);
    if (syncInvoker != null) {
      return _localInvokeSyncWithCascadeAsync(syncInvoker, message, messageType);
    }

    // RPC extraction fallback: receptor returns complex type containing TResult
    // Extract TResult from the result and cascade remaining values
    var anyInvoker = GetReceptorInvokerAny(message, messageType);
    if (anyInvoker != null) {
      return _localInvokeWithRpcExtractionAsync<TResult>(anyInvoker, message, messageType);
    }

    throw new ReceptorNotFoundException(messageType);
  }

  /// <summary>
  /// Wrapper that tries the typed invoker first, but falls back to RPC extraction
  /// on InvalidCastException. This handles the case where the receptor returns a
  /// complex type (tuple, array, etc.) but the caller requests a specific type.
  /// </summary>
  /// <docs>core-concepts/rpc-extraction</docs>
  /// <tests>Whizbang.Core.Tests/Dispatcher/DispatcherRpcExtractionTests.cs</tests>
  private async ValueTask<TResult> _localInvokeWithCastFallbackAsync<TResult>(
    ReceptorInvoker<TResult> asyncInvoker,
    object message,
    Type messageType,
    IMessageContext context,
    string callerMemberName,
    string callerFilePath,
    int callerLineNumber
  ) {
    try {
      // OPTIMIZATION: Skip envelope creation when trace store is null
      // This achieves zero allocation for high-throughput scenarios
      if (_traceStore != null || _receptorRegistry != null) {
        return await _localInvokeWithTracingAsync(message, messageType, context, asyncInvoker, callerMemberName, callerFilePath, callerLineNumber);
      }

      // Fast path with cascade support for receptor tuple/array returns
      // Invoke using delegate, then extract and publish any IEvent instances
      return await _localInvokeWithCascadeAsync(asyncInvoker, message, messageType);
    } catch (InvalidCastException) {
      // The typed invoker failed because the receptor returns a complex type
      // containing TResult, not TResult directly. Fall back to RPC extraction.
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
      CascadeLogger.LogDebug("[RPC] InvalidCastException caught, falling back to RPC extraction for {MessageType} -> {ResultType}",
        messageType.Name, typeof(TResult).Name);
#pragma warning restore CA1848

      var anyInvoker = GetReceptorInvokerAny(message, messageType);
      if (anyInvoker != null) {
        return await _localInvokeWithRpcExtractionAsync<TResult>(anyInvoker, message, messageType);
      }

      // If no invoker found at all, re-throw the original exception
      throw;
    }
  }

  /// <summary>
  /// Fast path for LocalInvoke with sync receptor and cascade support.
  /// Invokes receptor synchronously, automatically publishes any IEvent instances from the return value,
  /// and returns a pre-completed ValueTask (zero async overhead when cascade has no events).
  /// </summary>
  /// <docs>core-concepts/dispatcher#synchronous-invocation</docs>
  private ValueTask<TResult> _localInvokeSyncWithCascadeAsync<TResult>(
    SyncReceptorInvoker<TResult> syncInvoker,
    object message,
    Type messageType
  ) {
    // Invoke synchronously
    var result = syncInvoker(message);

    // Auto-cascade any events (still async for publishing)
    var cascadeTask = _cascadeEventsFromResultAsync(result, messageType);
    if (!cascadeTask.IsCompletedSuccessfully) {
      return _awaitCascadeAndReturnResultAsync(cascadeTask, result);
    }

    // Return pre-completed ValueTask (zero allocation)
    return new ValueTask<TResult>(result);
  }

  /// <summary>
  /// Helper method to await cascade task and return result.
  /// This is a separate method to avoid state machine overhead in the fast path.
  /// </summary>
  private static async ValueTask<TResult> _awaitCascadeAndReturnResultAsync<TResult>(Task cascadeTask, TResult result) {
    await cascadeTask;
    return result;
  }

  /// <summary>
  /// Fast path for LocalInvoke with cascade support.
  /// Invokes receptor and automatically publishes any IEvent instances from the return value.
  /// Supports tuples like (Result, Event), arrays like IEvent[], and nested structures.
  /// </summary>
  private async ValueTask<TResult> _localInvokeWithCascadeAsync<TResult>(
    ReceptorInvoker<TResult> invoker,
    object message,
    Type messageType
  ) {
    var result = await invoker(message);
    await _cascadeEventsFromResultAsync(result, messageType);
    return result;
  }

  /// <summary>
  /// RPC extraction path for LocalInvokeAsync when receptor returns a complex type containing TResult.
  /// Extracts TResult from the result and cascades all remaining values.
  /// </summary>
  /// <typeparam name="TResult">The type requested by the RPC caller.</typeparam>
  /// <param name="invoker">The type-erased receptor invoker.</param>
  /// <param name="message">The message to dispatch.</param>
  /// <param name="messageType">The runtime type of the message.</param>
  /// <returns>The extracted TResult value.</returns>
  /// <exception cref="InvalidOperationException">Thrown when TResult cannot be extracted from the receptor result.</exception>
  /// <docs>core-concepts/rpc-extraction</docs>
  /// <tests>Whizbang.Core.Tests/Dispatcher/DispatcherRpcExtractionTests.cs</tests>
  private async ValueTask<TResult> _localInvokeWithRpcExtractionAsync<TResult>(
    Func<object, ValueTask<object?>> invoker,
    object message,
    Type messageType
  ) {
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
    CascadeLogger.LogDebug("[RPC] RpcExtraction: Invoking receptor for {MessageType}, extracting {ResultType}",
      messageType.Name, typeof(TResult).Name);

    // 1. Invoke receptor to get full result
    var fullResult = await invoker(message);
    CascadeLogger.LogDebug("[RPC] RpcExtraction: Receptor returned {ResultType}, IsNull={IsNull}",
      fullResult?.GetType().Name ?? "null", fullResult == null);

    // 2. Extract the requested TResult from the result
    if (!Internal.ResponseExtractor.TryExtractResponse<TResult>(fullResult, out var response)) {
      throw new InvalidOperationException(
        $"Could not extract {typeof(TResult).Name} from receptor result of type {fullResult?.GetType().Name ?? "null"}. " +
        $"The receptor for {messageType.Name} does not return a value of type {typeof(TResult).Name}.");
    }
    CascadeLogger.LogDebug("[RPC] RpcExtraction: Successfully extracted {ResultType}", typeof(TResult).Name);

    // 3. Cascade remaining messages (excluding the extracted response)
    await _cascadeEventsExcludingResponseAsync(fullResult, response, messageType);

    // 4. Return the extracted response
    return response!;
#pragma warning restore CA1848
  }

  /// <summary>
  /// Cascades events from a result, excluding the RPC response that was returned to the caller.
  /// Uses ReferenceEquals to identify the exact instance to exclude.
  /// </summary>
  /// <typeparam name="TResult">The type of the extracted RPC response.</typeparam>
  /// <param name="result">The full receptor result containing multiple values.</param>
  /// <param name="extractedResponse">The value that was extracted and returned to the RPC caller.</param>
  /// <param name="originalMessageType">The type of the original message for routing lookup.</param>
  /// <docs>core-concepts/rpc-extraction</docs>
  /// <tests>Whizbang.Core.Tests/Dispatcher/DispatcherRpcExtractionTests.cs</tests>
  private async Task _cascadeEventsExcludingResponseAsync<TResult>(
    object? result,
    TResult? extractedResponse,
    Type? originalMessageType = null
  ) {
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
    CascadeLogger.LogDebug("[RPC] CascadeExcludingResponse: ResultType={ResultType}, ExtractedType={ExtractedType}",
      result?.GetType().Name ?? "null", typeof(TResult).Name);

    // Fast path: Skip if result is null
    if (result == null) {
      CascadeLogger.LogDebug("[RPC] CascadeExcludingResponse: Result is null, skipping cascade");
      return;
    }

    // Look up receptor default routing from [DefaultRouting] attribute on the receptor
    Dispatch.DispatchMode? receptorDefault = originalMessageType is not null
        ? GetReceptorDefaultRouting(originalMessageType)
        : null;
    CascadeLogger.LogDebug("[RPC] CascadeExcludingResponse: ReceptorDefaultRouting={ReceptorDefault}", receptorDefault);

    // Use MessageExtractor to find all IMessage instances with routing info
    var extractedCount = 0;
    var skippedCount = 0;
    foreach (var (msg, mode) in Internal.MessageExtractor.ExtractMessagesWithRouting(result, receptorDefault)) {
      // Skip the extracted response - it goes to RPC caller, not cascade
      if (extractedResponse != null && ReferenceEquals(msg, extractedResponse)) {
        skippedCount++;
        CascadeLogger.LogDebug("[RPC] CascadeExcludingResponse: Skipping extracted response (ReferenceEquals match)");
        continue;
      }

      extractedCount++;
      var msgType = msg.GetType();
      CascadeLogger.LogDebug("[RPC] CascadeExcludingResponse: Cascading message {Count}: Type={MessageType}, Mode={Mode}",
        extractedCount, msgType.Name, mode);

      // Local dispatch: Invoke in-process receptors
      if (mode.HasFlag(Dispatch.DispatchMode.Local)) {
        CascadeLogger.LogDebug("[RPC] CascadeExcludingResponse: Dispatching locally for {MessageType}", msgType.Name);
        var publisher = GetUntypedReceptorPublisher(msgType);
        if (publisher != null) {
          await publisher(msg);
        }
      }

      // Outbox dispatch: Write to outbox for cross-service delivery
      if (mode.HasFlag(Dispatch.DispatchMode.Outbox)) {
        CascadeLogger.LogDebug("[RPC] CascadeExcludingResponse: Calling CascadeToOutboxAsync for {MessageType}", msgType.Name);
        await CascadeToOutboxAsync(msg, msgType);
      }
    }

    CascadeLogger.LogDebug("[RPC] CascadeExcludingResponse: Cascaded {CascadeCount} messages, skipped {SkipCount} (RPC response)",
      extractedCount, skippedCount);
#pragma warning restore CA1848
  }

  /// <summary>
  /// Void path cascade support for non-void receptors.
  /// When void LocalInvokeAsync is called but a non-void receptor is found,
  /// invoke it and cascade any events from the result.
  /// </summary>
  private async ValueTask _localInvokeVoidWithAnyInvokerAndCascadeAsync(
    Func<object, ValueTask<object?>> invoker,
    object message,
    Type messageType
  ) {
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
    CascadeLogger.LogDebug("[CASCADE] VoidWithAnyInvoker: Invoking receptor for {MessageType}", messageType.Name);
    var result = await invoker(message);
    CascadeLogger.LogDebug("[CASCADE] VoidWithAnyInvoker: Receptor returned {ResultType}, IsNull={IsNull}", result?.GetType().Name ?? "null", result == null);
    if (result != null) {
      await _cascadeEventsFromResultAsync(result, messageType);
    } else {
      CascadeLogger.LogWarning("[CASCADE] VoidWithAnyInvoker: Receptor returned null, no cascade will occur");
    }
#pragma warning restore CA1848
  }

  /// <summary>
  /// Slow path for LocalInvoke when tracing is enabled.
  /// Uses async/await to store envelope before invoking receptor.
  /// </summary>
  private async ValueTask<TResult> _localInvokeWithTracingAsync<TResult>(
    object message,
    Type messageType,
    IMessageContext context,
    ReceptorInvoker<TResult> invoker,
    string callerMemberName,
    string callerFilePath,
    int callerLineNumber
  ) {
    var envelope = _createEnvelope(message, context, callerMemberName, callerFilePath, callerLineNumber);

    // Register envelope so receptor can look it up via IEventStore.AppendAsync(message)
    _envelopeRegistry?.Register(envelope);
    try {
      if (_traceStore != null) {
        await _traceStore.StoreAsync(envelope);
      }

      // Invoke using delegate - zero reflection, strongly typed
      var result = await invoker(message);

      // Auto-cascade: Extract and publish any IEvent instances from receptor return value
      // Supports tuples like (Result, Event), arrays like IEvent[], and nested structures
      await _cascadeEventsFromResultAsync(result, messageType);

      // NOTE: We do NOT invoke _receptorInvoker here for LocalImmediateInline because:
      // 1. The dispatcher already invokes the business receptor via the generated delegate above
      // 2. Invoking _receptorInvoker would cause double invocation of receptors without [FireAt]
      // 3. IReceptorInvoker is meant for TransportConsumerWorker (PostInbox) and
      //    WorkCoordinatorPublisherWorker (PreOutbox), not for local dispatch

      return result;
    } finally {
      // Unregister envelope after receptor completes (or throws)
      _envelopeRegistry?.Unregister(envelope);
    }
  }

  /// <summary>
  /// Internal generic implementation of LocalInvokeAsync that preserves type information.
  /// This method is called by the public LocalInvokeAsync&lt;TMessage, TResult&gt; overload to avoid type erasure.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  private ValueTask<TResult> _localInvokeAsyncInternal<TMessage, TResult>(
    TMessage message,
    IMessageContext context,
    [CallerMemberName] string callerMemberName = "",
    [CallerFilePath] string callerFilePath = "",
    [CallerLineNumber] int callerLineNumber = 0
  ) where TMessage : notnull {
    ArgumentNullException.ThrowIfNull(message);
    ArgumentNullException.ThrowIfNull(context);

    var messageType = typeof(TMessage);

    // Get strongly-typed delegate from generated code
    var invoker = GetReceptorInvoker<TResult>(message, messageType) ?? throw new ReceptorNotFoundException(messageType);

    // OPTIMIZATION: Skip envelope creation when trace store is null
    // This achieves zero allocation for high-throughput scenarios
    if (_traceStore != null || _receptorRegistry != null) {
      return _localInvokeWithTracingAsyncInternalAsync<TMessage, TResult>(message, context, invoker, callerMemberName, callerFilePath, callerLineNumber);
    }

    // Fast path with cascade support for receptor tuple/array returns
    // Invoke using delegate, then extract and publish any IEvent instances
    return _localInvokeWithCascadeAsync(invoker, message, messageType);
  }

  /// <summary>
  /// Internal generic tracing method for LocalInvoke when tracing is enabled.
  /// Uses async/await to store envelope before invoking receptor.
  /// Preserves type information to create correctly-typed MessageEnvelope.
  /// </summary>
  private async ValueTask<TResult> _localInvokeWithTracingAsyncInternalAsync<TMessage, TResult>(
    TMessage message,
    IMessageContext context,
    ReceptorInvoker<TResult> invoker,
    string callerMemberName,
    string callerFilePath,
    int callerLineNumber
  ) {
    var envelope = _createEnvelope<TMessage>(message, context, callerMemberName, callerFilePath, callerLineNumber);

    // Register envelope so receptor can look it up via IEventStore.AppendAsync(message)
    _envelopeRegistry?.Register(envelope);
    try {
      if (_traceStore != null) {
        await _traceStore.StoreAsync(envelope);
      }

      // Invoke using delegate - zero reflection, strongly typed
      var result = await invoker(message!);

      // Auto-cascade: Extract and publish any IEvent instances from receptor return value
      // Supports tuples like (Result, Event), arrays like IEvent[], and nested structures
      await _cascadeEventsFromResultAsync(result, typeof(TMessage));

      // NOTE: We do NOT invoke _receptorInvoker here for LocalImmediateInline because:
      // 1. The dispatcher already invokes the business receptor via the generated delegate above
      // 2. Invoking _receptorInvoker would cause double invocation of receptors without [FireAt]
      // 3. IReceptorInvoker is meant for TransportConsumerWorker (PostInbox) and
      //    WorkCoordinatorPublisherWorker (PreOutbox), not for local dispatch

      return result;
    } finally {
      // Unregister envelope after receptor completes (or throws)
      _envelopeRegistry?.Unregister(envelope);
    }
  }

  // ========================================
  // VOID LOCAL INVOKE PATTERN - Zero Allocation Command/Event Handling
  // ========================================

  /// <summary>
  /// Invokes a void receptor in-process with typed message without returning a business result (AOT-compatible).
  /// PERFORMANCE: Zero allocation target for command/event patterns.
  /// RESTRICTION: In-process only - throws InvalidOperationException if used with remote transport.
  /// Type information is preserved at compile time, avoiding reflection.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public ValueTask LocalInvokeAsync<TMessage>(TMessage message) where TMessage : notnull {
    var context = MessageContext.New();
    return LocalInvokeAsync((object)message, context);
  }

  /// <summary>
  /// Invokes a void receptor in-process without returning a business result.
  /// Creates a new message context automatically.
  /// PERFORMANCE: Zero allocation target for command/event patterns.
  /// For AOT compatibility, use the generic overload LocalInvokeAsync&lt;TMessage&gt;.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public ValueTask LocalInvokeAsync(object message) {
    var context = MessageContext.New();
    return LocalInvokeAsync(message, context);
  }

  /// <summary>
  /// Invokes a void receptor in-process with typed message and explicit context without returning a business result (AOT-compatible).
  /// Type information is preserved at compile time, avoiding reflection.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public ValueTask LocalInvokeAsync<TMessage>(
    TMessage message,
    IMessageContext context,
    [CallerMemberName] string callerMemberName = "",
    [CallerFilePath] string callerFilePath = "",
    [CallerLineNumber] int callerLineNumber = 0
  ) where TMessage : notnull {
    return _localInvokeAsyncInternal<TMessage>(message, context, callerMemberName, callerFilePath, callerLineNumber);
  }

  /// <summary>
  /// Invokes a void receptor in-process with explicit context without returning a business result.
  /// Uses generated delegate to invoke receptor with zero reflection.
  /// Skips envelope creation when trace store is null for optimal performance.
  /// PERFORMANCE: Zero allocation fast path for synchronously-completed receptors.
  /// For AOT compatibility, use the generic overload LocalInvokeAsync&lt;TMessage&gt;.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public ValueTask LocalInvokeAsync(
    object message,
    IMessageContext context,
    [CallerMemberName] string callerMemberName = "",
    [CallerFilePath] string callerFilePath = "",
    [CallerLineNumber] int callerLineNumber = 0
  ) {
    ArgumentNullException.ThrowIfNull(message);

    ArgumentNullException.ThrowIfNull(context);

    var messageType = message.GetType();

    // Try async receptor first (async takes precedence)
    var asyncInvoker = GetVoidReceptorInvoker(message, messageType);
    if (asyncInvoker != null) {
      // OPTIMIZATION: Skip envelope creation when trace store is null
      // This achieves zero allocation for high-throughput scenarios
      if (_traceStore != null) {
        return _localInvokeVoidWithTracingAsync(message, context, asyncInvoker, callerMemberName, callerFilePath, callerLineNumber);
      }

      // FAST PATH: Zero allocation when no tracing
      // Invoke using delegate - zero reflection, strongly typed
      // Avoid async/await state machine allocation by returning task directly
      return asyncInvoker(message);
    }

    // Fallback to void sync receptor
    var syncInvoker = GetVoidSyncReceptorInvoker(message, messageType);
    if (syncInvoker != null) {
      // Invoke synchronously - returns pre-completed ValueTask
      syncInvoker(message);
      return ValueTask.CompletedTask;
    }

    // Fallback to any receptor (void or non-void) for cascade support
    // This enables void LocalInvokeAsync to cascade events from non-void receptors
    var anyInvoker = GetReceptorInvokerAny(message, messageType);
    if (anyInvoker != null) {
      return _localInvokeVoidWithAnyInvokerAndCascadeAsync(anyInvoker, message, messageType);
    }

    throw new ReceptorNotFoundException(messageType);
  }

  /// <summary>
  /// Slow path for void LocalInvoke when tracing is enabled.
  /// Uses async/await to store envelope before invoking receptor.
  /// </summary>
  private async ValueTask _localInvokeVoidWithTracingAsync(
    object message,
    IMessageContext context,
    VoidReceptorInvoker invoker,
    string callerMemberName,
    string callerFilePath,
    int callerLineNumber
  ) {
    var envelope = _createEnvelope(message, context, callerMemberName, callerFilePath, callerLineNumber);

    // Register envelope so receptor can look it up via IEventStore.AppendAsync(message)
    _envelopeRegistry?.Register(envelope);
    try {
      if (_traceStore != null) {
        await _traceStore.StoreAsync(envelope);
      }

      // Invoke using delegate - zero reflection, strongly typed
      await invoker(message);
    } finally {
      // Unregister envelope after receptor completes (or throws)
      _envelopeRegistry?.Unregister(envelope);
    }
  }

  /// <summary>
  /// Internal generic implementation of void LocalInvokeAsync that preserves type information.
  /// This method is called by the public LocalInvokeAsync&lt;TMessage&gt; overload to avoid type erasure.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  private ValueTask _localInvokeAsyncInternal<TMessage>(
    TMessage message,
    IMessageContext context,
    [CallerMemberName] string callerMemberName = "",
    [CallerFilePath] string callerFilePath = "",
    [CallerLineNumber] int callerLineNumber = 0
  ) where TMessage : notnull {
    ArgumentNullException.ThrowIfNull(message);
    ArgumentNullException.ThrowIfNull(context);

    var messageType = typeof(TMessage);

    // Try async receptor first (async takes precedence)
    var asyncInvoker = GetVoidReceptorInvoker(message, messageType);
    if (asyncInvoker != null) {
      // OPTIMIZATION: Skip envelope creation when trace store AND lifecycle invoker are null
      // This achieves zero allocation for high-throughput scenarios
      if (_traceStore != null || _receptorRegistry != null) {
        return _localInvokeVoidWithTracingAsyncInternalAsync<TMessage>(message, context, asyncInvoker, callerMemberName, callerFilePath, callerLineNumber);
      }

      // FAST PATH: Zero allocation when no tracing and no lifecycle invoker
      // Invoke using delegate - zero reflection, strongly typed
      // Avoid async/await state machine allocation by returning task directly
      return asyncInvoker(message);
    }

    // Fallback to void sync receptor
    var syncInvoker = GetVoidSyncReceptorInvoker(message, messageType);
    if (syncInvoker != null) {
      // Invoke synchronously - returns pre-completed ValueTask
      syncInvoker(message);
      return ValueTask.CompletedTask;
    }

    // Fallback to any receptor (void or non-void) for cascade support
    // This enables void LocalInvokeAsync to cascade events from non-void receptors
    var anyInvoker = GetReceptorInvokerAny(message, messageType);
    if (anyInvoker != null) {
      return _localInvokeVoidWithAnyInvokerAndCascadeAsync(anyInvoker, message, messageType);
    }

    throw new ReceptorNotFoundException(messageType);
  }

  /// <summary>
  /// Internal generic tracing method for void LocalInvoke when tracing is enabled.
  /// Uses async/await to store envelope before invoking receptor.
  /// Preserves type information to create correctly-typed MessageEnvelope.
  /// </summary>
  private async ValueTask _localInvokeVoidWithTracingAsyncInternalAsync<TMessage>(
    TMessage message,
    IMessageContext context,
    VoidReceptorInvoker invoker,
    string callerMemberName,
    string callerFilePath,
    int callerLineNumber
  ) {
    var envelope = _createEnvelope<TMessage>(message, context, callerMemberName, callerFilePath, callerLineNumber);

    // Register envelope so receptor can look it up via IEventStore.AppendAsync(message)
    _envelopeRegistry?.Register(envelope);
    try {
      if (_traceStore != null) {
        await _traceStore.StoreAsync(envelope);
      }

      // Invoke using delegate - zero reflection, strongly typed
      await invoker(message!);

      // NOTE: We do NOT invoke _receptorInvoker here - dispatcher already invoked receptor above
    } finally {
      // Unregister envelope after receptor completes (or throws)
      _envelopeRegistry?.Unregister(envelope);
    }
  }

  // ========================================
  // LOCAL INVOKE WITH DISPATCH OPTIONS
  // ========================================

  /// <summary>
  /// Invokes a receptor in-process with dispatch options and returns the typed business result.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public ValueTask<TResult> LocalInvokeAsync<TResult>(object message, DispatchOptions options) {
    options.CancellationToken.ThrowIfCancellationRequested();
    var context = MessageContext.New();
    return _localInvokeWithOptionsAsync<TResult>(message, context, options);
  }

  /// <summary>
  /// Invokes a void receptor in-process with dispatch options.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public ValueTask LocalInvokeAsync(object message, DispatchOptions options) {
    options.CancellationToken.ThrowIfCancellationRequested();
    var context = MessageContext.New();
    return _localInvokeVoidWithOptionsAsync(message, context, options);
  }

  /// <summary>
  /// Internal implementation of LocalInvokeAsync with DispatchOptions.
  /// </summary>
  private async ValueTask<TResult> _localInvokeWithOptionsAsync<TResult>(
    object message,
    IMessageContext context,
    DispatchOptions options,
    [CallerMemberName] string callerMemberName = "",
    [CallerFilePath] string callerFilePath = "",
    [CallerLineNumber] int callerLineNumber = 0
  ) {
    ArgumentNullException.ThrowIfNull(message);
    ArgumentNullException.ThrowIfNull(context);

    var messageType = message.GetType();
    var asyncInvoker = GetReceptorInvoker<TResult>(message, messageType);

    if (asyncInvoker != null) {
      if (_traceStore != null || _receptorRegistry != null) {
        return await _localInvokeWithTracingAndOptionsAsync(message, messageType, context, asyncInvoker, options, callerMemberName, callerFilePath, callerLineNumber);
      }
      options.CancellationToken.ThrowIfCancellationRequested();
      return await _localInvokeWithCascadeAsync(asyncInvoker, message, messageType);
    }

    var syncInvoker = GetSyncReceptorInvoker<TResult>(message, messageType);
    if (syncInvoker != null) {
      options.CancellationToken.ThrowIfCancellationRequested();
      return await _localInvokeSyncWithCascadeAsync(syncInvoker, message, messageType);
    }

    throw new ReceptorNotFoundException(messageType);
  }

  /// <summary>
  /// Internal implementation of void LocalInvokeAsync with DispatchOptions.
  /// </summary>
  private async ValueTask _localInvokeVoidWithOptionsAsync(
    object message,
    IMessageContext context,
    DispatchOptions options,
    [CallerMemberName] string callerMemberName = "",
    [CallerFilePath] string callerFilePath = "",
    [CallerLineNumber] int callerLineNumber = 0
  ) {
    ArgumentNullException.ThrowIfNull(message);
    ArgumentNullException.ThrowIfNull(context);

    var messageType = message.GetType();
    var asyncInvoker = GetVoidReceptorInvoker(message, messageType);

    if (asyncInvoker != null) {
      if (_traceStore != null) {
        await _localInvokeVoidWithTracingAndOptionsAsync(message, context, asyncInvoker, options, callerMemberName, callerFilePath, callerLineNumber);
        return;
      }
      options.CancellationToken.ThrowIfCancellationRequested();
      await asyncInvoker(message);
      return;
    }

    var syncInvoker = GetVoidSyncReceptorInvoker(message, messageType);
    if (syncInvoker != null) {
      options.CancellationToken.ThrowIfCancellationRequested();
      syncInvoker(message);
      return;
    }

    // Fallback: Try to find any receptor (including those that return values)
    // This allows void LocalInvokeAsync to call receptors that return events for cascading
    var anyInvoker = GetReceptorInvokerAny(message, messageType);
    if (anyInvoker != null) {
      options.CancellationToken.ThrowIfCancellationRequested();
      await _localInvokeVoidWithAnyInvokerAndCascadeAsync(anyInvoker, message, messageType);
      return;
    }

    throw new ReceptorNotFoundException(messageType);
  }

  /// <summary>
  /// LocalInvoke with tracing and DispatchOptions support.
  /// </summary>
  private async ValueTask<TResult> _localInvokeWithTracingAndOptionsAsync<TResult>(
    object message,
    Type messageType,
    IMessageContext context,
    ReceptorInvoker<TResult> invoker,
    DispatchOptions options,
    string callerMemberName,
    string callerFilePath,
    int callerLineNumber
  ) {
    var envelope = _createEnvelope(message, context, callerMemberName, callerFilePath, callerLineNumber);
    _envelopeRegistry?.Register(envelope);
    try {
      if (_traceStore != null) {
        await _traceStore.StoreAsync(envelope, options.CancellationToken);
      }

      options.CancellationToken.ThrowIfCancellationRequested();
      var result = await invoker(message);
      await _cascadeEventsFromResultAsync(result, messageType);

      // NOTE: We do NOT invoke _receptorInvoker here - dispatcher already invoked receptor above

      return result;
    } finally {
      _envelopeRegistry?.Unregister(envelope);
    }
  }

  /// <summary>
  /// Void LocalInvoke with tracing and DispatchOptions support.
  /// </summary>
  private async ValueTask _localInvokeVoidWithTracingAndOptionsAsync(
    object message,
    IMessageContext context,
    VoidReceptorInvoker invoker,
    DispatchOptions options,
    string callerMemberName,
    string callerFilePath,
    int callerLineNumber
  ) {
    var envelope = _createEnvelope(message, context, callerMemberName, callerFilePath, callerLineNumber);
    _envelopeRegistry?.Register(envelope);
    try {
      if (_traceStore != null) {
        await _traceStore.StoreAsync(envelope, options.CancellationToken);
      }

      options.CancellationToken.ThrowIfCancellationRequested();
      await invoker(message);
    } finally {
      _envelopeRegistry?.Unregister(envelope);
    }
  }

  /// <summary>
  /// Creates a MessageEnvelope with initial hop containing caller information and context.
  /// Generic version - preserves type information at compile time.
  /// </summary>
  private MessageEnvelope<TMessage> _createEnvelope<TMessage>(
    TMessage message,
    IMessageContext context,
    string callerMemberName,
    string callerFilePath,
    int callerLineNumber
  ) {
    var envelope = new MessageEnvelope<TMessage> {
      MessageId = MessageId.New(),
      Payload = message!,
      Hops = []
    };

    // Extract aggregate ID and add to hop metadata (for streamId extraction)
    var hopMetadata = _createHopMetadata(message!, typeof(TMessage));

    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _instanceProvider.ToInfo(),
      Timestamp = DateTimeOffset.UtcNow,
      CorrelationId = context.CorrelationId,
      CausationId = context.CausationId,
      CallerMemberName = callerMemberName,
      CallerFilePath = callerFilePath,
      CallerLineNumber = callerLineNumber,
      Metadata = hopMetadata,
      SecurityContext = _getSecurityContextForPropagation()
    };

    envelope.AddHop(hop);
    return envelope;
  }

  /// <summary>
  /// Creates a MessageEnvelope with initial hop containing caller information and context.
  /// Non-generic version - creates MessageEnvelope&lt;object&gt;.
  /// Used when compile-time type is unknown (e.g., from non-generic SendAsync).
  /// For AOT compatibility, prefer using the generic overload when the type is known.
  /// </summary>
  private MessageEnvelope<object> _createEnvelope(
    object message,
    IMessageContext context,
    string callerMemberName,
    string callerFilePath,
    int callerLineNumber
  ) {
    var envelope = new MessageEnvelope<object> {
      MessageId = MessageId.New(),
      Payload = message,
      Hops = []
    };

    // Extract aggregate ID and add to hop metadata (for streamId extraction)
    var messageType = message.GetType();
    var hopMetadata = _createHopMetadata(message, messageType);

    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _instanceProvider.ToInfo(),
      Timestamp = DateTimeOffset.UtcNow,
      CorrelationId = context.CorrelationId,
      CausationId = context.CausationId,
      CallerMemberName = callerMemberName,
      CallerFilePath = callerFilePath,
      CallerLineNumber = callerLineNumber,
      Metadata = hopMetadata,
      SecurityContext = _getSecurityContextForPropagation()
    };

    envelope.AddHop(hop);
    return envelope;
  }

  // ========================================
  // AUTO-CASCADE - Automatic Event Publishing from Receptor Returns
  // ========================================

  /// <summary>
  /// Extracts IMessage instances from receptor return values and dispatches them based on routing.
  /// Supports tuples, arrays, nested structures, and Routed&lt;T&gt; wrappers via MessageExtractor.
  /// This enables the clean pattern: return (result, Route.Local(@event)) - where @event is dispatched locally.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Uses the AOT-compatible GetUntypedReceptorPublisher method which is implemented by
  /// source-generated code. The generated code knows all event types at compile time and
  /// returns type-erased delegates that cast internally.
  /// </para>
  /// <para>
  /// Routing behavior based on DispatchMode:
  /// <list type="bullet">
  ///   <item>Local: Invoke in-process receptors only via GetUntypedReceptorPublisher</item>
  ///   <item>Outbox: Write to outbox for cross-service delivery via _cascadeToOutboxAsync</item>
  ///   <item>Both: Both local dispatch AND outbox write</item>
  ///   <item>Default (unwrapped): Outbox only (system default)</item>
  /// </list>
  /// </para>
  /// </remarks>
  /// <docs>core-concepts/dispatcher#routed-message-cascading</docs>
  /// <tests>Whizbang.Core.Tests/Dispatcher/DispatcherCascadeTests.cs:LocalInvokeAsync_TupleWithEvent_AutoPublishesEventAsync</tests>
  /// <tests>Whizbang.Core.Tests/Dispatcher/DispatcherRoutedCascadeTests.cs:CascadeFromResult_WithRouteLocal_InvokesLocalReceptorAsync</tests>
  private async Task _cascadeEventsFromResultAsync<TResult>(TResult result, Type? originalMessageType = null) {
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
    CascadeLogger.LogDebug("[CASCADE] CascadeEventsFromResult: ResultType={ResultType}, OriginalMessageType={OriginalMessageType}",
      result?.GetType().Name ?? "null", originalMessageType?.Name ?? "null");

    // Fast path: Skip if result is null
    if (result == null) {
      CascadeLogger.LogWarning("[CASCADE] CascadeEventsFromResult: Result is null, skipping cascade");
      return;
    }

    // Look up receptor default routing from [DefaultRouting] attribute on the receptor
    // This is done via the generated GetReceptorDefaultRouting method
    Dispatch.DispatchMode? receptorDefault = originalMessageType is not null
        ? GetReceptorDefaultRouting(originalMessageType)
        : null;
    CascadeLogger.LogDebug("[CASCADE] CascadeEventsFromResult: ReceptorDefaultRouting={ReceptorDefault}", receptorDefault);

    // Use MessageExtractor to find all IMessage instances with routing info
    // This handles tuples, arrays, nested structures, Routed<T> wrappers, etc. using ITuple interface (AOT-safe)
    var extractedCount = 0;
    foreach (var (msg, mode) in Internal.MessageExtractor.ExtractMessagesWithRouting(result, receptorDefault)) {
      extractedCount++;
      var messageType = msg.GetType();
      CascadeLogger.LogDebug("[CASCADE] CascadeEventsFromResult: Extracted message {Count}: Type={MessageType}, Mode={Mode}",
        extractedCount, messageType.Name, mode);

      // Local dispatch: Invoke in-process receptors (for Local, LocalNoPersist, Both)
      // Check for LocalDispatch flag specifically, not the composite Local mode
      if (mode.HasFlag(Dispatch.DispatchMode.LocalDispatch)) {
        CascadeLogger.LogDebug("[CASCADE] CascadeEventsFromResult: Dispatching locally for {MessageType}", messageType.Name);
        var publisher = GetUntypedReceptorPublisher(messageType);
        if (publisher != null) {
          // Establish message context for cascade: propagates UserId from parent scope
          Security.SecurityContextHelper.EstablishMessageContextForCascade();
          await publisher(msg);
        }
      }

      // Event store only: Store to event store without transport (for Local, EventStoreOnly)
      // When EventStore is set but Outbox is NOT set, store with null destination
      if (mode.HasFlag(Dispatch.DispatchMode.EventStore) && !mode.HasFlag(Dispatch.DispatchMode.Outbox)) {
        if (msg is IEvent) {
          CascadeLogger.LogDebug("[CASCADE] CascadeEventsFromResult: Calling CascadeToEventStoreOnlyAsync for {MessageType}", messageType.Name);
          await CascadeToEventStoreOnlyAsync(msg, messageType);
        }
      }

      // Outbox dispatch: Write to outbox for cross-service delivery (for Outbox, Both)
      if (mode.HasFlag(Dispatch.DispatchMode.Outbox)) {
        CascadeLogger.LogDebug("[CASCADE] CascadeEventsFromResult: Calling CascadeToOutboxAsync for {MessageType}", messageType.Name);
        await CascadeToOutboxAsync(msg, messageType);
      }
    }

    if (extractedCount == 0) {
      CascadeLogger.LogWarning("[CASCADE] CascadeEventsFromResult: No messages extracted from result type {ResultType}. " +
        "This may indicate the result does not implement IMessage or is not wrapped in a supported collection/tuple.",
        result.GetType().Name);
    } else {
      CascadeLogger.LogDebug("[CASCADE] CascadeEventsFromResult: Extracted {Count} messages total", extractedCount);
    }
#pragma warning restore CA1848
  }

  /// <summary>
  /// Publishes a cascaded message to the outbox for cross-service delivery.
  /// Uses source-generated type-switch dispatch for AOT compatibility.
  /// </summary>
  /// <param name="message">The message to cascade to outbox.</param>
  /// <param name="messageType">The runtime type of the message.</param>
  /// <returns>A task representing the asynchronous operation.</returns>
  /// <remarks>
  /// <para>
  /// This base implementation is a no-op. The source generator creates an override
  /// with a type-switched dispatch table that calls PublishToOutboxAsync for each
  /// known event type. This avoids reflection and maintains AOT compatibility.
  /// </para>
  /// </remarks>
  /// <docs>core-concepts/dispatcher#auto-cascade-to-outbox</docs>
  /// <tests>Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:Generator_WithEventReturningReceptor_GeneratesCascadeToOutboxAsync</tests>
  protected virtual Task CascadeToOutboxAsync(IMessage message, Type messageType, IMessageEnvelope? sourceEnvelope = null) {
    // Base implementation is a no-op.
    // GeneratedDispatcher overrides this with type-switched dispatch to PublishToOutboxAsync.
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
    CascadeLogger.LogWarning("[CASCADE] CascadeToOutboxAsync: BASE IMPLEMENTATION CALLED for {MessageType}. " +
      "This means the generated dispatcher does NOT have an override for this message type. " +
      "The message will NOT be written to the outbox!", messageType.Name);
#pragma warning restore CA1848
    return Task.CompletedTask;
  }

  /// <summary>
  /// Cascades a message to the event store only (no transport).
  /// Uses destination=null to store events and create perspective events, but skip transport publishing.
  /// </summary>
  /// <param name="message">The message to cascade.</param>
  /// <param name="messageType">The runtime type of the message.</param>
  /// <remarks>
  /// <para>
  /// This base implementation is a no-op. The source generator creates an override
  /// with a type-switched dispatch table that calls PublishToOutboxAsync with eventStoreOnly=true.
  /// This avoids reflection and maintains AOT compatibility.
  /// </para>
  /// <para>
  /// Called by CascadeMessageAsync when DispatchMode.EventStore flag is set without DispatchMode.Outbox.
  /// Events are stored to wh_event_store and perspective events are created, but transport is skipped.
  /// </para>
  /// </remarks>
  /// <docs>core-concepts/dispatcher#event-store-only</docs>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherRoutedCascadeTests.cs:CascadeEventStoreOnly_*</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LocalEventStorageTests.cs:RouteEventStoreOnly_*</tests>
  protected virtual Task CascadeToEventStoreOnlyAsync(IMessage message, Type messageType, IMessageEnvelope? sourceEnvelope = null) {
    // Base implementation is a no-op.
    // GeneratedDispatcher overrides this with type-switched dispatch to PublishToOutboxAsync(eventStoreOnly: true).
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
    CascadeLogger.LogWarning("[CASCADE] CascadeToEventStoreOnlyAsync: BASE IMPLEMENTATION CALLED for {MessageType}. " +
      "This means the generated dispatcher does NOT have an override for this message type. " +
      "The event will NOT be written to the event store!", messageType.Name);
#pragma warning restore CA1848
    return Task.CompletedTask;
  }

  /// <summary>
  /// Publishes an event to all registered handlers.
  /// Uses generated delegate to invoke receptors with zero reflection.
  /// After local handlers complete, publishes to outbox for cross-service delivery (if configured).
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:Publish_WithEvent_ShouldNotifyAllHandlersAsync</tests>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public async Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData) {
    // S2955 suppressed: TEvent is constrained to IEvent in practice (always reference types)
    // Adding where TEvent : class would be a breaking API change
#pragma warning disable S2955 // Generic parameters not constrained to reference types should not be compared to 'null'
    if (eventData == null) {
      throw new ArgumentNullException(nameof(eventData));
    }
#pragma warning restore S2955

    var eventType = eventData.GetType();

    // Create MessageId once - used for outbox and will be used by process_work_batch for event storage
    var messageId = MessageId.New();

    // Get strongly-typed delegate from generated code
    var publisher = GetReceptorPublisher(eventData, eventType);

    // Invoke local handlers - zero reflection, strongly typed
    await publisher(eventData);

    // Publish event for cross-service delivery if work coordinator strategy is available
    // process_work_batch will store events to wh_event_store and create perspective events atomically
    await PublishToOutboxAsync(eventData, eventType, messageId);

    // Extract stream ID from [StreamId] attribute for delivery receipt
    var streamId = _streamIdExtractor?.ExtractStreamId(eventData, eventType);

    // Return delivery receipt with stream ID
    var destination = eventType.Name;
    return DeliveryReceipt.Delivered(
      messageId,
      destination,
      correlationId: null,
      causationId: null,
      streamId: streamId);
  }

  /// <summary>
  /// Publishes an event to all registered handlers with dispatch options.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:PublishAsync_WithDispatchOptions_CompletesAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:PublishAsync_WithCancelledToken_ThrowsOperationCanceledExceptionAsync</tests>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public async Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData, DispatchOptions options) {
#pragma warning disable S2955 // Generic parameters not constrained to reference types should not be compared to 'null'
    if (eventData == null) {
      throw new ArgumentNullException(nameof(eventData));
    }
#pragma warning restore S2955

    options.CancellationToken.ThrowIfCancellationRequested();

    var eventType = eventData.GetType();
    var messageId = MessageId.New();
    var publisher = GetReceptorPublisher(eventData, eventType);

    options.CancellationToken.ThrowIfCancellationRequested();
    await publisher(eventData);

    await PublishToOutboxAsync(eventData, eventType, messageId);

    // Extract stream ID from [StreamId] attribute for delivery receipt
    var streamId = _streamIdExtractor?.ExtractStreamId(eventData, eventType);

    // Return delivery receipt with stream ID
    var destination = eventType.Name;
    return DeliveryReceipt.Delivered(
      messageId,
      destination,
      correlationId: null,
      causationId: null,
      streamId: streamId);
  }

  /// <summary>
  /// Cascades a message with explicit routing mode.
  /// Called by IEventCascader after resolving routing from wrappers and attributes.
  /// </summary>
  /// <docs>core-concepts/dispatcher#cascade-to-outbox</docs>
  public async Task CascadeMessageAsync(IMessage message, IMessageEnvelope? sourceEnvelope, Dispatch.DispatchMode mode, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(message);
    cancellationToken.ThrowIfCancellationRequested();

    var messageType = message.GetType();

#pragma warning disable CA1848 // Diagnostic logging - performance not critical
    CascadeLogger.LogDebug("[CASCADE] CascadeMessageAsync: Message={MessageType}, Mode={Mode}", messageType.Name, mode);
#pragma warning restore CA1848

    // Local dispatch: Invoke in-process receptors (for Local, LocalNoPersist, Both)
    if (mode.HasFlag(Dispatch.DispatchMode.LocalDispatch)) {
#pragma warning disable CA1848
      CascadeLogger.LogDebug("[CASCADE] CascadeMessageAsync: Dispatching locally for {MessageType}", messageType.Name);
#pragma warning restore CA1848
      var publisher = GetUntypedReceptorPublisher(messageType);
      if (publisher != null) {
        await publisher(message);
      }
    }

    // Event store only: Store to event store without transport (for Local, EventStoreOnly)
    // Uses destination=null to store event and create perspective events, but skip transport.
    // This path is NOT taken if Outbox flag is also set (Outbox handles event storage via transport).
    if (mode.HasFlag(Dispatch.DispatchMode.EventStore) && !mode.HasFlag(Dispatch.DispatchMode.Outbox)) {
      // Only events are stored (commands are silently skipped, consistent with current behavior)
      if (message is IEvent) {
#pragma warning disable CA1848
        CascadeLogger.LogDebug("[CASCADE] CascadeMessageAsync: Calling CascadeToEventStoreOnlyAsync for {MessageType}", messageType.Name);
#pragma warning restore CA1848
        await CascadeToEventStoreOnlyAsync(message, messageType, sourceEnvelope);
      }
    }

    // Outbox dispatch: Write to outbox for cross-service delivery (for Outbox, Both)
    if (mode.HasFlag(Dispatch.DispatchMode.Outbox)) {
#pragma warning disable CA1848
      CascadeLogger.LogDebug("[CASCADE] CascadeMessageAsync: Calling CascadeToOutboxAsync for {MessageType}", messageType.Name);
#pragma warning restore CA1848
      await CascadeToOutboxAsync(message, messageType, sourceEnvelope);
    }
  }

  /// <summary>
  /// Publishes an event to the outbox for cross-service delivery using work coordinator strategy.
  /// Queues event for batched processing.
  /// Resolves IWorkCoordinatorStrategy from active scope (scoped service).
  /// Creates a complete MessageEnvelope with a hop indicating "stored to outbox".
  /// </summary>
  /// <typeparam name="TEvent">The type of event to publish.</typeparam>
  /// <param name="eventData">The event data to publish.</param>
  /// <param name="eventType">The runtime type of the event.</param>
  /// <param name="messageId">The unique message ID for this event.</param>
  /// <param name="eventStoreOnly">
  /// When true, event is stored in event store only (no transport).
  /// Destination is set to null, which bypasses transport publishing.
  /// </param>
  /// <remarks>
  /// <para>
  /// Protected to allow generated dispatcher to call this method from CascadeToOutboxAsync override.
  /// </para>
  /// <para>
  /// Security context inheritance: The new envelope's initial hop inherits SecurityContext from
  /// the sourceEnvelope when ambient context (ScopeContextAccessor.CurrentContext) is unavailable.
  /// This ensures cascaded events carry the security context from their originating command.
  /// </para>
  /// </remarks>
  /// <docs>core-concepts/dispatcher#auto-cascade-to-outbox</docs>
  /// <tests>Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:Generator_CascadeToOutbox_CallsPublishToOutboxWithMessageIdAsync</tests>
  protected async Task PublishToOutboxAsync<TEvent>(TEvent eventData, Type eventType, MessageId messageId, IMessageEnvelope? sourceEnvelope = null, bool eventStoreOnly = false) {
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
    CascadeLogger.LogDebug("[CASCADE] PublishToOutboxAsync: Called for {EventType}, MessageId={MessageId}", eventType.Name, messageId);
#pragma warning restore CA1848

    // Create scope to resolve scoped IWorkCoordinatorStrategy
    var scope = _scopeFactory.CreateScope();
    try {
      var strategy = scope.ServiceProvider.GetService<IWorkCoordinatorStrategy>();
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
      CascadeLogger.LogDebug("[CASCADE] PublishToOutboxAsync: Strategy resolved: {StrategyType}", strategy?.GetType().Name ?? "null");
#pragma warning restore CA1848

      // If no strategy is registered, skip outbox routing (local-only event)
      if (strategy == null) {
        // Log diagnostic warning for configuration issues (error path only, performance not critical)
#pragma warning disable CA1848 // Use LoggerMessage delegates for performance - acceptable in error path
        var logger = scope.ServiceProvider.GetService<ILogger<Dispatcher>>();
        logger?.LogWarning(
          "IWorkCoordinatorStrategy not registered - event will not be published to outbox for cross-service delivery. " +
          "Register IWorkCoordinatorStrategy (ImmediateWorkCoordinatorStrategy, ScopedWorkCoordinatorStrategy, " +
          "or IntervalWorkCoordinatorStrategy) to enable outbox pattern. EventType: {EventType}",
          eventType.Name);
#pragma warning restore CA1848
        return;
      }

      // Resolve destination topic using registry and routing strategy
      // When eventStoreOnly is true, use null destination to bypass transport
      string? destination = eventStoreOnly ? null : _resolveEventTopic(eventType);

      // Create MessageEnvelope wrapping the event (using SAME messageId as event store)
      var envelope = new MessageEnvelope<TEvent> {
        MessageId = messageId,
        Payload = eventData,
        Hops = []
      };

      // Extract aggregate ID and add to hop metadata (for streamId extraction)
      var hopMetadata = _createHopMetadata(eventData!, eventType);

      // Add hop indicating message is being stored to outbox
      // When destination is null (event-store-only), use "(event-store)" as topic indicator
      // SecurityContext: First try ambient context, then inherit from source envelope
      var hop = new MessageHop {
        Type = HopType.Current,
        ServiceInstance = _instanceProvider.ToInfo(),
        Topic = destination ?? "(event-store)",
        Timestamp = DateTimeOffset.UtcNow,
        Metadata = hopMetadata,
        SecurityContext = _getSecurityContextForPropagation() ?? sourceEnvelope?.GetCurrentSecurityContext()
      };
      envelope.AddHop(hop);

      System.Diagnostics.Debug.WriteLine($"[Dispatcher] Queueing event {eventType.Name} to work coordinator with destination '{destination}'");
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
      CascadeLogger.LogDebug("[CASCADE] PublishToOutboxAsync: Destination={Destination}", destination);
#pragma warning restore CA1848

      // Serialize envelope to OutboxMessage
      var newOutboxMessage = _serializeToNewOutboxMessage(envelope, eventData!, eventType, destination);
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
      CascadeLogger.LogDebug("[CASCADE] PublishToOutboxAsync: Created NewOutboxMessage, MessageId={MessageId}, Type={Type}",
        newOutboxMessage.MessageId, newOutboxMessage.MessageType);
#pragma warning restore CA1848

      // Queue event for batched processing
      strategy.QueueOutboxMessage(newOutboxMessage);
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
      CascadeLogger.LogDebug("[CASCADE] PublishToOutboxAsync: Called QueueOutboxMessage");
#pragma warning restore CA1848

      // Flush strategy to execute the batch
      var workBatch = await strategy.FlushAsync(WorkBatchFlags.None);
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
      CascadeLogger.LogDebug("[CASCADE] PublishToOutboxAsync: FlushAsync returned OutboxWork={OutboxCount}, InboxWork={InboxCount}",
        workBatch.OutboxWork.Count, workBatch.InboxWork.Count);
#pragma warning restore CA1848

      System.Diagnostics.Debug.WriteLine($"[Dispatcher] Successfully queued event {eventType.Name} via work coordinator");
    } finally {
      // Dispose scope asynchronously to properly handle services that only implement IAsyncDisposable
      if (scope is IAsyncDisposable asyncDisposable) {
        await asyncDisposable.DisposeAsync();
      } else {
        scope.Dispose();
      }
    }
  }

  /// <summary>
  /// Resolves the Service Bus topic for an event type using the topic registry and routing strategy.
  /// First attempts registry lookup (source-generated or configured), then falls back to convention.
  /// Finally applies routing strategy for transformations (e.g., pool suffix, tenant prefix).
  /// </summary>
  /// <param name="eventType">The event type to resolve topic for</param>
  /// <param name="context">Optional routing context (tenant ID, region, etc.)</param>
  /// <returns>The resolved topic name</returns>
  private string _resolveEventTopic(Type eventType, IReadOnlyDictionary<string, object>? context = null) {
    // PRIORITY: Use outbox routing strategy if configured (routes events to namespace topics)
    // This ensures events are stored with their ACTUAL destination in the outbox,
    // providing proper durability guarantees
    if (_outboxRoutingStrategy != null) {
      var destination = _outboxRoutingStrategy.GetDestination(eventType, _ownedDomains, MessageKind.Event);
      return destination.Address;
    }

    // FALLBACK: Convention-based routing (for backwards compatibility)
    // 1. Try registry first (source-generated or configured)
    var baseTopic = _topicRegistry?.GetBaseTopic(eventType);

    // 2. Fallback to convention if not found in registry
    if (baseTopic == null) {
      var typeName = eventType.Name;

      // Convention-based routing: ProductXxxEvent → "products", InventoryXxxEvent → "inventory"
      if (typeName.StartsWith("Product", StringComparison.Ordinal)) {
        baseTopic = "products";
      } else if (typeName.StartsWith("Inventory", StringComparison.Ordinal)) {
        baseTopic = "inventory";
      } else if (typeName.StartsWith("Order", StringComparison.Ordinal)) {
        baseTopic = "orders";
      } else {
        // Default: use lowercase type name without "Event" suffix
        baseTopic = typeName.Replace("Event", "").ToLowerInvariant();
      }
    }

    // 3. Apply routing strategy (pool suffix, tenant prefix, etc.)
    // _topicRoutingStrategy is never null (defaults to PassthroughRoutingStrategy if not provided)
    // baseTopic is never null here: either from registry or convention fallback always assigns a value
    var resolvedTopic = _topicRoutingStrategy!.ResolveTopic(eventType, baseTopic, context);
    return resolvedTopic;
  }

  /// <summary>
  /// Resolves the Service Bus destination for a command type using the topic registry and routing strategy.
  /// First attempts registry lookup (source-generated or configured), then falls back to convention.
  /// Finally applies routing strategy for transformations (e.g., pool suffix, tenant prefix).
  /// </summary>
  /// <param name="commandType">The command type to resolve destination for</param>
  /// <param name="context">Optional routing context (tenant ID, region, etc.)</param>
  /// <returns>The resolved destination name</returns>
  private string _resolveCommandDestination(Type commandType, IReadOnlyDictionary<string, object>? context = null) {
    // PRIORITY: Use outbox routing strategy if configured (routes commands to inbox)
    // This ensures commands are stored with their ACTUAL destination in the outbox,
    // providing proper durability guarantees
    if (_outboxRoutingStrategy != null) {
      var destination = _outboxRoutingStrategy.GetDestination(commandType, _ownedDomains, MessageKind.Command);
      return destination.Address;
    }

    // FALLBACK: Convention-based routing (for backwards compatibility)
    // 1. Try registry first (source-generated or configured)
    var baseTopic = _topicRegistry?.GetBaseTopic(commandType);

    // 2. Fallback to convention if not found in registry
    if (baseTopic == null) {
      var typeName = commandType.Name;

      // Convention-based routing: ProductXxxCommand → "products", InventoryXxxCommand → "inventory"
      if (typeName.StartsWith("Product", StringComparison.Ordinal) || typeName.StartsWith("CreateProduct", StringComparison.Ordinal)) {
        baseTopic = "products";
      } else if (typeName.StartsWith("Inventory", StringComparison.Ordinal)) {
        baseTopic = "inventory";
      } else if (typeName.StartsWith("Order", StringComparison.Ordinal)) {
        baseTopic = "orders";
      } else {
        // Default: use lowercase type name without "Command" suffix
        baseTopic = typeName.Replace("Command", "").ToLowerInvariant();
      }
    }

    // 3. Apply routing strategy (pool suffix, tenant prefix, etc.)
    return _topicRoutingStrategy.ResolveTopic(commandType, baseTopic, context);
  }

  /// <summary>
  /// Sends a typed message to the outbox for remote delivery using work coordinator strategy (AOT-compatible).
  /// Creates a MessageEnvelope&lt;TMessage&gt; with proper type information and queues for batched processing.
  /// Resolves IWorkCoordinatorStrategy from active scope (scoped service).
  /// Type information is preserved at compile time, avoiding reflection.
  /// </summary>
  private async Task<IDeliveryReceipt> _sendToOutboxViaScopeAsync<TMessage>(
    TMessage message,
    Type messageType,
    IMessageContext context,
    string callerMemberName,
    string callerFilePath,
    int callerLineNumber
  ) where TMessage : notnull {
    // Create scope to resolve scoped IWorkCoordinatorStrategy
    var scope = _scopeFactory.CreateScope();
    try {
      var strategy = scope.ServiceProvider.GetService<IWorkCoordinatorStrategy>();

      // If no strategy is registered, throw - no local receptor and no outbox
      if (strategy == null) {
        // Log diagnostic warning for configuration issues (error path only, performance not critical)
#pragma warning disable CA1848 // Use LoggerMessage delegates for performance - acceptable in error path
        var logger = scope.ServiceProvider.GetService<ILogger<Dispatcher>>();
        logger?.LogWarning(
          "IWorkCoordinatorStrategy not registered - cannot send command to outbox and no local handler found. " +
          "Register IWorkCoordinatorStrategy (ImmediateWorkCoordinatorStrategy, ScopedWorkCoordinatorStrategy, " +
          "or IntervalWorkCoordinatorStrategy) to enable outbox pattern. MessageType: {MessageType}",
          messageType.Name);
#pragma warning restore CA1848
        throw new ReceptorNotFoundException(messageType);
      }

      // Resolve destination using registry and routing strategy
      var destination = _resolveCommandDestination(messageType);

      // Create envelope with hop for observability - generic version preserves type!
      var envelope = _createEnvelope<TMessage>(message, context, callerMemberName, callerFilePath, callerLineNumber);

      // Serialize envelope to OutboxMessage
      var newOutboxMessage = _serializeToNewOutboxMessage(envelope, message!, messageType, destination);

      // Queue message for batched processing
      strategy.QueueOutboxMessage(newOutboxMessage);

      // Flush strategy to execute the batch (strategy determines when to actually flush)
      // For immediate strategy, this happens right away
      // For scoped strategy, this happens on scope disposal
      // For interval strategy, this happens on timer
      await strategy.FlushAsync(WorkBatchFlags.None);

      // Extract stream ID from [StreamId] attribute for delivery receipt
      var streamId = _streamIdExtractor?.ExtractStreamId(message!, messageType);

      // Return delivery receipt with Accepted status (message queued)
      return DeliveryReceipt.Accepted(
        envelope.MessageId,
        destination,
        context.CorrelationId,
        context.CausationId,
        streamId
      );
    } finally {
      // Dispose scope asynchronously to properly handle services that only implement IAsyncDisposable
      if (scope is IAsyncDisposable asyncDisposable) {
        await asyncDisposable.DisposeAsync();
      } else {
        scope.Dispose();
      }
    }
  }

  /// <summary>
  /// Sends a message to the outbox for remote delivery using work coordinator strategy.
  /// Creates a MessageEnvelope with proper type information and queues for batched processing.
  /// Resolves IWorkCoordinatorStrategy from active scope (scoped service).
  /// AOT-compatible - uses JsonTypeInfo for serialization, no reflection.
  /// For AOT compatibility, use the generic overload SendToOutboxViaScopeAsync&lt;TMessage&gt;.
  /// </summary>
  private async Task<IDeliveryReceipt> _sendToOutboxViaScopeAsync(
    object message,
    Type messageType,
    IMessageContext context,
    string callerMemberName,
    string callerFilePath,
    int callerLineNumber
  ) {
    // Create scope to resolve scoped IWorkCoordinatorStrategy
    var scope = _scopeFactory.CreateScope();
    try {
      var strategy = scope.ServiceProvider.GetService<IWorkCoordinatorStrategy>();

      // If no strategy is registered, throw - no local receptor and no outbox
      if (strategy == null) {
        // Log diagnostic warning for configuration issues (error path only, performance not critical)
#pragma warning disable CA1848 // Use LoggerMessage delegates for performance - acceptable in error path
        var logger = scope.ServiceProvider.GetService<ILogger<Dispatcher>>();
        logger?.LogWarning(
          "IWorkCoordinatorStrategy not registered - cannot send command to outbox and no local handler found. " +
          "Register IWorkCoordinatorStrategy (ImmediateWorkCoordinatorStrategy, ScopedWorkCoordinatorStrategy, " +
          "or IntervalWorkCoordinatorStrategy) to enable outbox pattern. MessageType: {MessageType}",
          messageType.Name);
#pragma warning restore CA1848
        throw new ReceptorNotFoundException(messageType);
      }

      // Resolve destination using registry and routing strategy
      var destination = _resolveCommandDestination(messageType);

      // Create envelope with hop for observability (returns IMessageEnvelope)
      // WARN: This creates MessageEnvelope<object> - type information is lost
      // For AOT compatibility, use the generic overload SendToOutboxViaScopeAsync<TMessage>
      var envelope = _createEnvelope(message, context, callerMemberName, callerFilePath, callerLineNumber);

      // Serialize envelope to OutboxMessage
      var newOutboxMessage = _serializeToNewOutboxMessage(envelope, message, messageType, destination);

      // Queue message for batched processing
      strategy.QueueOutboxMessage(newOutboxMessage);

      // Flush strategy to execute the batch (strategy determines when to actually flush)
      // For immediate strategy, this happens right away
      // For scoped strategy, this happens on scope disposal
      // For interval strategy, this happens on timer
      await strategy.FlushAsync(WorkBatchFlags.None);

      // Extract stream ID from [StreamId] attribute for delivery receipt
      var streamId = _streamIdExtractor?.ExtractStreamId(message, messageType);

      // Return delivery receipt with Accepted status (message queued)
      return DeliveryReceipt.Accepted(
        envelope.MessageId,
        destination,
        context.CorrelationId,
        context.CausationId,
        streamId
      );
    } finally {
      // Dispose scope asynchronously to properly handle services that only implement IAsyncDisposable
      if (scope is IAsyncDisposable asyncDisposable) {
        await asyncDisposable.DisposeAsync();
      } else {
        scope.Dispose();
      }
    }
  }

  /// <summary>
  /// Sends multiple messages to the outbox in a single batch operation.
  /// Creates ONE scope, queues all messages, and flushes once for optimal performance.
  /// </summary>
  private async Task<List<IDeliveryReceipt>> _sendManyToOutboxAsync(
    List<(object message, Type messageType, IMessageContext context)> messages,
    [CallerMemberName] string callerMemberName = "",
    [CallerFilePath] string callerFilePath = "",
    [CallerLineNumber] int callerLineNumber = 0
  ) {
    var receipts = new List<IDeliveryReceipt>();

    // Create ONE scope for all messages
    var scope = _scopeFactory.CreateScope();
    try {
      var strategy = scope.ServiceProvider.GetService<IWorkCoordinatorStrategy>();

      // If no strategy is registered, throw
      if (strategy == null) {
        throw new InvalidOperationException("No IWorkCoordinatorStrategy registered. Cannot route messages to outbox.");
      }

      // Queue ALL messages to the strategy
      foreach (var (message, messageType, context) in messages) {
        var destination = _resolveCommandDestination(messageType);
        var envelope = _createEnvelope(message, context, callerMemberName, callerFilePath, callerLineNumber);
        var newOutboxMessage = _serializeToNewOutboxMessage(envelope, message, messageType, destination);

        strategy.QueueOutboxMessage(newOutboxMessage);

        // Extract stream ID from [StreamId] attribute for delivery receipt
        var streamId = _streamIdExtractor?.ExtractStreamId(message, messageType);

        // Create receipt for this message
        receipts.Add(DeliveryReceipt.Accepted(
          envelope.MessageId,
          destination,
          context.CorrelationId,
          context.CausationId,
          streamId
        ));
      }

      // Flush ONCE for all messages
      await strategy.FlushAsync(WorkBatchFlags.None);

    } finally {
      // Dispose scope asynchronously
      if (scope is IAsyncDisposable asyncDisposable) {
        await asyncDisposable.DisposeAsync();
      } else {
        scope.Dispose();
      }
    }

    return receipts;
  }

  // ========================================
  // BATCH OPERATIONS
  // ========================================

  /// <summary>
  /// Sends multiple typed messages and collects all delivery receipts (AOT-compatible).
  /// Type information is preserved at compile time, avoiding reflection.
  /// Optimized for batch operations - creates a single scope and flushes once for outbox messages.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:SendMany_WithMultipleCommands_ShouldReturnAllReceiptsAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs:SendManyAsync_Generic_CreatesTypedEnvelopesAsync</tests>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public async Task<IEnumerable<IDeliveryReceipt>> SendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull {
    ArgumentNullException.ThrowIfNull(messages);

    var messageList = messages.ToList();
    var receipts = new List<IDeliveryReceipt>();

    // Separate messages into local and outbox-bound
    var localMessages = new List<TMessage>();
    var outboxMessages = new List<(object message, Type messageType, IMessageContext context)>();

    var messageType = typeof(TMessage);
    foreach (var message in messageList) {
      var invoker = GetReceptorInvoker<TMessage>(message, messageType);

      if (invoker != null) {
        // Has local receptor
        localMessages.Add(message);
      } else {
        // No local receptor - route to outbox
        outboxMessages.Add((message, messageType, MessageContext.New()));
      }
    }

    // Process local messages individually (fast path)
    foreach (var message in localMessages) {
      var receipt = await _sendAsyncInternalAsync<TMessage>(message, MessageContext.New());
      receipts.Add(receipt);
    }

    // Process outbox messages in a single batch (optimized)
    if (outboxMessages.Count > 0) {
      var outboxReceipts = await _sendManyToOutboxAsync(outboxMessages);
      receipts.AddRange(outboxReceipts);
    }

    return receipts;
  }

  /// <summary>
  /// Sends multiple messages and collects all delivery receipts.
  /// Optimized for batch operations - creates a single scope and flushes once.
  /// For AOT compatibility, use the generic overload SendManyAsync&lt;TMessage&gt;.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public async Task<IEnumerable<IDeliveryReceipt>> SendManyAsync(IEnumerable<object> messages) {
    ArgumentNullException.ThrowIfNull(messages);

    var messageList = messages.ToList();
    var receipts = new List<IDeliveryReceipt>();

    // Separate messages into local and outbox-bound
    var localMessages = new List<(object message, Type messageType)>();
    var outboxMessages = new List<(object message, Type messageType, IMessageContext context)>();

    foreach (var message in messageList) {
      var messageType = message.GetType();
      var invoker = GetReceptorInvoker<object>(message, messageType);

      if (invoker != null) {
        // Has local receptor
        localMessages.Add((message, messageType));
      } else {
        // No local receptor - route to outbox
        outboxMessages.Add((message, messageType, MessageContext.New()));
      }
    }

    // Process local messages individually (fast path)
    foreach (var (message, _) in localMessages) {
      var receipt = await SendAsync(message);
      receipts.Add(receipt);
    }

    // Process outbox messages in a single batch (optimized)
    if (outboxMessages.Count > 0) {
      var outboxReceipts = await _sendManyToOutboxAsync(outboxMessages);
      receipts.AddRange(outboxReceipts);
    }

    return receipts;
  }

  /// <summary>
  /// Invokes multiple receptors in-process and collects all typed business results.
  /// PERFORMANCE: Zero allocation when trace store is null, target &lt; 20ns per invocation.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public async ValueTask<IEnumerable<TResult>> LocalInvokeManyAsync<TResult>(IEnumerable<object> messages) {
    ArgumentNullException.ThrowIfNull(messages);

    var results = new List<TResult>();
    foreach (var message in messages) {
      var result = await LocalInvokeAsync<TResult>(message);
      results.Add(result);
    }
    return results;
  }

  // ========================================
  // SERIALIZATION HELPERS
  // ========================================

  /// <summary>
  /// Creates an OutboxMessage for work coordinator pattern.
  /// Extracts stream_id from aggregate ID or falls back to message ID.
  /// Type information is preserved in the MessageEnvelope&lt;TMessage&gt; instance itself.
  /// </summary>
  private OutboxMessage _serializeToNewOutboxMessage<TMessage>(
    IMessageEnvelope<TMessage> envelope,
    TMessage payload,
    Type payloadType,
    string? destination
  ) {
    // DIAGNOSTIC: Check if TMess age is JsonElement BEFORE calling serializer
    if (typeof(TMessage) == typeof(JsonElement)) {
      throw new InvalidOperationException(
        $"BUG IN DISPATCHER: _serializeToNewOutboxMessage called with TMessage=JsonElement. " +
        $"MessageId: {envelope.MessageId}. " +
        $"Envelope type: {envelope.GetType().FullName}. " +
        $"Payload type: {(payload?.GetType().FullName ?? "null")}. " +
        $"PayloadType parameter: {payloadType.FullName}. " +
        $"This indicates Dispatcher is being passed a MessageEnvelope<JsonElement> instead of a strongly-typed envelope.");
    }

    // Extract stream_id: try aggregate ID from first hop, fall back to message ID
    var streamId = _extractStreamId(envelope);

    // Use centralized envelope serializer (REQUIRED)
    if (_envelopeSerializer == null) {
      throw new InvalidOperationException(
        "IEnvelopeSerializer is required but not registered. " +
        "Ensure you call services.AddWhizbang() to register core services.");
    }

    var serialized = _envelopeSerializer.SerializeEnvelope(envelope);

    // DIAGNOSTIC: Log if MessageType is JsonElement (should never happen after serializer checks)
    if (serialized.MessageType.Contains("JsonElement", StringComparison.OrdinalIgnoreCase)) {
      throw new InvalidOperationException(
        $"CRITICAL BUG: EnvelopeSerializer returned MessageType='{serialized.MessageType}' which contains 'JsonElement'. " +
        $"MessageId: {envelope.MessageId}. " +
        $"Envelope type: {envelope.GetType().FullName}. " +
        $"TMessage type parameter: {typeof(TMessage).FullName}. " +
        $"Payload type: {(payload?.GetType().FullName ?? "null")}. " +
        $"PayloadType parameter: {payloadType.FullName}. " +
        $"The serializer defensive checks should have caught this!");
    }

    var outboxMessage = new OutboxMessage {
      MessageId = envelope.MessageId.Value,
      Destination = destination,
      Envelope = serialized.JsonEnvelope,
      Metadata = new EnvelopeMetadata {
        MessageId = envelope.MessageId,
        Hops = envelope.Hops.ToList()
      },
      EnvelopeType = serialized.EnvelopeType,
      StreamId = streamId,
      IsEvent = payload is IEvent,
      MessageType = serialized.MessageType
    };

    // FINAL CHECK: Throw if ANY type string contains JsonElement
    if (outboxMessage.MessageType.Contains("JsonElement", StringComparison.OrdinalIgnoreCase) ||
        outboxMessage.EnvelopeType.Contains("JsonElement", StringComparison.OrdinalIgnoreCase)) {
      throw new InvalidOperationException(
        $"FINAL CHECK FAILED: OutboxMessage contains JsonElement in type metadata. " +
        $"MessageId={outboxMessage.MessageId}, " +
        $"MessageType={outboxMessage.MessageType}, " +
        $"EnvelopeType={outboxMessage.EnvelopeType}, " +
        $"TMessage={typeof(TMessage).FullName}, " +
        $"PayloadType={payloadType.FullName}, " +
        $"Payload runtime type={payload?.GetType().FullName ?? "null"}. " +
        $"This means either: (1) Envelope parameter was MessageEnvelope<JsonElement>, " +
        $"(2) Payload was JsonElement, or (3) PayloadType parameter was typeof(JsonElement). " +
        $"All these cases should have been caught by earlier checks!");
    }

    return outboxMessage;
  }

  /// <summary>
  /// Extracts stream_id from envelope for stream-based ordering.
  /// Tries to get stream ID from first hop metadata, falls back to message ID.
  /// </summary>
  private static Guid _extractStreamId(IMessageEnvelope envelope) {
    // Check first hop for stream ID (stored as "AggregateId" for backward compatibility)
    var firstHop = envelope.Hops.FirstOrDefault();
    if (firstHop?.Metadata != null && firstHop.Metadata.TryGetValue("AggregateId", out var streamIdElem) &&
        streamIdElem.ValueKind == JsonValueKind.String) {
      var streamIdStr = streamIdElem.GetString();
      if (streamIdStr != null && Guid.TryParse(streamIdStr, out var parsedStreamId)) {
        return parsedStreamId;
      }
    }

    // Fall back to message ID (ensures all messages have a stream)
    return envelope.MessageId.Value;
  }

  /// <summary>
  /// Creates hop metadata with StreamId extracted from the message using [StreamId] attribute.
  /// Returns null if no stream ID extractor is configured or no ID found.
  /// </summary>
  private Dictionary<string, JsonElement>? _createHopMetadata(object message, Type messageType) {
    if (_streamIdExtractor == null) {
      return null;
    }

    var streamId = _streamIdExtractor.ExtractStreamId(message, messageType);
    if (streamId == null) {
      return null;
    }

    // Create JsonElement for stream ID (AOT-safe approach using JsonDocument.Parse)
    // Wrap GUID string in quotes for valid JSON string value
    // Note: Key is "AggregateId" for backward compatibility with existing envelopes
    var jsonString = $"\"{streamId.Value}\"";
    using var doc = JsonDocument.Parse(jsonString);
    var streamIdElement = doc.RootElement.Clone(); // Clone to survive disposal

    return new Dictionary<string, JsonElement> {
      ["AggregateId"] = streamIdElement
    };
  }

  /// <summary>
  /// Implemented by generated code - returns a strongly-typed delegate for invoking a receptor.
  /// The delegate encapsulates the receptor lookup and invocation with zero reflection.
  /// </summary>
  protected abstract ReceptorInvoker<TResult>? GetReceptorInvoker<TResult>(object message, Type messageType);

  /// <summary>
  /// Implemented by generated code - returns a strongly-typed delegate for invoking a void receptor.
  /// The delegate encapsulates the receptor lookup and invocation with zero reflection.
  /// Returns null if no void receptor is registered for the message type.
  /// </summary>
  protected abstract VoidReceptorInvoker? GetVoidReceptorInvoker(object message, Type messageType);

  /// <summary>
  /// Implemented by generated code - returns a strongly-typed delegate for publishing to receptors.
  /// The delegate encapsulates finding all receptors and invoking them with zero reflection.
  /// </summary>
  protected abstract ReceptorPublisher<TEvent> GetReceptorPublisher<TEvent>(TEvent eventData, Type eventType);

  /// <summary>
  /// Implemented by generated code - returns a type-erased delegate for publishing events.
  /// Used by auto-cascade to publish events extracted from receptor return values.
  /// The delegate accepts an object and internally casts to the correct event type.
  /// AOT-compatible because the generated code knows all event types at compile time.
  /// </summary>
  /// <param name="eventType">The runtime type of the event (e.g., typeof(OrderCreatedEvent))</param>
  /// <returns>A delegate that publishes the event to all registered receptors, or null if no receptors registered</returns>
  protected abstract Func<object, Task>? GetUntypedReceptorPublisher(Type eventType);

  /// <summary>
  /// Implemented by generated code - returns a sync delegate for invoking a sync receptor.
  /// The delegate encapsulates the receptor lookup and invocation with zero reflection.
  /// Returns null if no sync receptor found (falls back to async).
  /// </summary>
  /// <docs>core-concepts/dispatcher#synchronous-invocation</docs>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherSyncTests.cs:LocalInvokeAsync_SyncReceptor_InvokesSynchronouslyAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherSyncTests.cs:LocalInvokeAsync_SyncReceptor_ReturnsCompletedValueTaskAsync</tests>
  protected abstract SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType);

  /// <summary>
  /// Implemented by generated code - returns a void sync delegate for invoking a sync receptor.
  /// The delegate encapsulates the receptor lookup and invocation with zero reflection.
  /// Returns null if no void sync receptor found.
  /// </summary>
  /// <docs>core-concepts/dispatcher#synchronous-invocation</docs>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherSyncTests.cs:LocalInvokeAsync_VoidSyncReceptor_ExecutesSynchronouslyAsync</tests>
  protected abstract VoidSyncReceptorInvoker? GetVoidSyncReceptorInvoker(object message, Type messageType);

  /// <summary>
  /// Implemented by generated code - returns a type-erased delegate for invoking ANY receptor.
  /// This enables void LocalInvokeAsync paths to cascade events from non-void receptors.
  /// Returns a delegate that invokes the receptor and returns the result as object? (null for void).
  /// Returns null if no receptor (void or non-void) is registered for the message type.
  /// </summary>
  /// <remarks>
  /// Priority order:
  /// 1. Non-void async receptor (IReceptor&lt;TMessage, TResponse&gt;)
  /// 2. Non-void sync receptor (ISyncReceptor&lt;TMessage, TResponse&gt;)
  /// 3. Void async receptor (IReceptor&lt;TMessage&gt;)
  /// 4. Void sync receptor (ISyncReceptor&lt;TMessage&gt;)
  /// </remarks>
  /// <docs>core-concepts/dispatcher#void-cascade</docs>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherVoidCascadeTests.cs</tests>
  protected abstract Func<object, ValueTask<object?>>? GetReceptorInvokerAny(object message, Type messageType);

  /// <summary>
  /// Implemented by generated code - returns the default dispatch routing for a message type
  /// based on the [DefaultRouting] attribute on the receptor class that handles the message.
  /// Used by cascade to apply receptor-level routing policy to all returned messages.
  /// Returns null if no receptor with [DefaultRouting] is registered for the message type.
  /// </summary>
  /// <param name="messageType">The runtime type of the message</param>
  /// <returns>The default dispatch mode from the receptor's [DefaultRouting] attribute, or null</returns>
  /// <docs>core-concepts/dispatcher#routed-message-cascading</docs>
  /// <tests>tests/Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs</tests>
  protected abstract Dispatch.DispatchMode? GetReceptorDefaultRouting(Type messageType);
}
