using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
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
/// Base dispatcher class with core logic. The source generator creates a derived class
/// that implements the abstract lookup methods, returning strongly-typed delegates.
/// This achieves zero-reflection while keeping functional logic in the base class.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Integration/DispatcherReceptorIntegrationTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "Parameter 'jsonOptions' retained for backward compatibility with generated code")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "S1172:Unused method parameters should be removed", Justification = "Parameter 'jsonOptions' retained for backward compatibility with generated code")]
public abstract class Dispatcher(
  IServiceProvider serviceProvider,
  IServiceInstanceProvider instanceProvider,
  ITraceStore? traceStore = null,
  JsonSerializerOptions? jsonOptions = null,
  Routing.ITopicRegistry? topicRegistry = null,
  Routing.ITopicRoutingStrategy? topicRoutingStrategy = null,
  IAggregateIdExtractor? aggregateIdExtractor = null,
  ILifecycleInvoker? lifecycleInvoker = null,
  IEnvelopeSerializer? envelopeSerializer = null
  ) : IDispatcher {
  private readonly IServiceProvider _internalServiceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
  private readonly IServiceScopeFactory _scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
  private readonly IServiceInstanceProvider _instanceProvider = instanceProvider ?? throw new ArgumentNullException(nameof(instanceProvider));
  private readonly ITraceStore? _traceStore = traceStore;
  private readonly Routing.ITopicRegistry? _topicRegistry = topicRegistry;
  private readonly Routing.ITopicRoutingStrategy _topicRoutingStrategy = topicRoutingStrategy ?? Routing.PassthroughRoutingStrategy.Instance;
  private readonly IAggregateIdExtractor? _aggregateIdExtractor = aggregateIdExtractor;
  private readonly ILifecycleInvoker? _lifecycleInvoker = lifecycleInvoker;
  // Resolve from service provider if not injected (for backwards compatibility with generated code)
  private readonly IEnvelopeSerializer? _envelopeSerializer = envelopeSerializer ?? serviceProvider.GetService<IEnvelopeSerializer>();

  // Unused parameter retained for backward compatibility with generated code
  private readonly JsonSerializerOptions? _ = jsonOptions;

  /// <summary>
  /// Gets the service provider for receptor resolution.
  /// Available to generated derived class.
  /// </summary>
#pragma warning disable IDE1006 // CA1707 takes precedence - protected properties should not have underscores
  protected IServiceProvider ServiceProvider => _internalServiceProvider;
#pragma warning restore IDE1006

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

    // Store envelope if trace store is configured
    if (_traceStore != null) {
      await _traceStore.StoreAsync(envelope);
    }

    // Invoke using delegate - zero reflection, strongly typed
    await invoker(message);

    // Invoke lifecycle receptors at ImmediateAsync stage (after receptor completes, before any database operations)
    if (_lifecycleInvoker is not null) {
      var lifecycleContext = new LifecycleExecutionContext {
        CurrentStage = LifecycleStage.ImmediateAsync,
        EventId = null,
        StreamId = null,
        LastProcessedEventId = null,
        MessageSource = MessageSource.Local,
        AttemptNumber = 1 // Local dispatch is always first attempt
      };
      await _lifecycleInvoker.InvokeAsync(message, LifecycleStage.ImmediateAsync, lifecycleContext, default);
    }

    // Return delivery receipt
    var destination = messageType.Name; // Will be enhanced with actual receptor name in future
    return DeliveryReceipt.Delivered(
      envelope.MessageId,
      destination,
      context.CorrelationId,
      context.CausationId
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

    // Store envelope if trace store is configured
    if (_traceStore != null) {
      await _traceStore.StoreAsync(envelope);
    }

    // Invoke using delegate - zero reflection, strongly typed
    await invoker(message);

    // Invoke lifecycle receptors at ImmediateAsync stage (after receptor completes, before any database operations)
    if (_lifecycleInvoker is not null) {
      var lifecycleContext = new LifecycleExecutionContext {
        CurrentStage = LifecycleStage.ImmediateAsync,
        EventId = null,
        StreamId = null,
        LastProcessedEventId = null,
        MessageSource = MessageSource.Local,
        AttemptNumber = 1 // Local dispatch is always first attempt
      };
      await _lifecycleInvoker.InvokeAsync(message, LifecycleStage.ImmediateAsync, lifecycleContext, default);
    }

    // Return delivery receipt
    var destination = messageType.Name; // Will be enhanced with actual receptor name in future
    return DeliveryReceipt.Delivered(
      envelope.MessageId,
      destination,
      context.CorrelationId,
      context.CausationId
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

    // Get strongly-typed delegate from generated code
    var invoker = GetReceptorInvoker<TResult>(message, messageType) ?? throw new HandlerNotFoundException(messageType);

    // OPTIMIZATION: Skip envelope creation when trace store is null
    // This achieves zero allocation for high-throughput scenarios
    if (_traceStore != null || _lifecycleInvoker != null) {
      return _localInvokeWithTracingAsync(message, context, invoker, callerMemberName, callerFilePath, callerLineNumber);
    }

    // FAST PATH: Zero allocation when no tracing and no lifecycle invoker
    // Invoke using delegate - zero reflection, strongly typed
    // Avoid async/await state machine allocation by returning task directly
    return invoker(message);
  }

  /// <summary>
  /// Slow path for LocalInvoke when tracing is enabled.
  /// Uses async/await to store envelope before invoking receptor.
  /// </summary>
  private async ValueTask<TResult> _localInvokeWithTracingAsync<TResult>(
    object message,
    IMessageContext context,
    ReceptorInvoker<TResult> invoker,
    string callerMemberName,
    string callerFilePath,
    int callerLineNumber
  ) {
    var envelope = _createEnvelope(message, context, callerMemberName, callerFilePath, callerLineNumber);
    await _traceStore!.StoreAsync(envelope);

    // Invoke using delegate - zero reflection, strongly typed
    var result = await invoker(message);

    // Invoke lifecycle receptors at ImmediateAsync stage (after receptor completes, before any database operations)
    if (_lifecycleInvoker is not null) {
      var lifecycleContext = new LifecycleExecutionContext {
        CurrentStage = LifecycleStage.ImmediateAsync,
        EventId = null,
        StreamId = null,
        LastProcessedEventId = null,
        MessageSource = MessageSource.Local,
        AttemptNumber = 1 // Local dispatch is always first attempt
      };
      await _lifecycleInvoker.InvokeAsync(message, LifecycleStage.ImmediateAsync, lifecycleContext, default);
    }

    return result;
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
    var invoker = GetReceptorInvoker<TResult>(message, messageType) ?? throw new HandlerNotFoundException(messageType);

    // OPTIMIZATION: Skip envelope creation when trace store is null
    // This achieves zero allocation for high-throughput scenarios
    if (_traceStore != null || _lifecycleInvoker != null) {
      return _localInvokeWithTracingAsyncInternalAsync<TMessage, TResult>(message, context, invoker, callerMemberName, callerFilePath, callerLineNumber);
    }

    // FAST PATH: Zero allocation when no tracing and no lifecycle invoker
    // Invoke using delegate - zero reflection, strongly typed
    // Avoid async/await state machine allocation by returning task directly
    return invoker(message);
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
    await _traceStore!.StoreAsync(envelope);

    // Invoke using delegate - zero reflection, strongly typed
    var result = await invoker(message!);

    // Invoke lifecycle receptors at ImmediateAsync stage (after receptor completes, before any database operations)
    if (_lifecycleInvoker is not null) {
      var lifecycleContext = new LifecycleExecutionContext {
        CurrentStage = LifecycleStage.ImmediateAsync,
        EventId = null,
        StreamId = null,
        LastProcessedEventId = null
      };
      await _lifecycleInvoker.InvokeAsync(message!, LifecycleStage.ImmediateAsync, lifecycleContext, default);
    }

    return result;
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

    // Get strongly-typed void delegate from generated code
    var invoker = GetVoidReceptorInvoker(message, messageType) ?? throw new HandlerNotFoundException(messageType);

    // OPTIMIZATION: Skip envelope creation when trace store is null
    // This achieves zero allocation for high-throughput scenarios
    if (_traceStore != null) {
      return _localInvokeVoidWithTracingAsync(message, context, invoker, callerMemberName, callerFilePath, callerLineNumber);
    }

    // FAST PATH: Zero allocation when no tracing
    // Invoke using delegate - zero reflection, strongly typed
    // Avoid async/await state machine allocation by returning task directly
    return invoker(message);
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
    await _traceStore!.StoreAsync(envelope);

    // Invoke using delegate - zero reflection, strongly typed
    await invoker(message);
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

    // Get strongly-typed void delegate from generated code
    var invoker = GetVoidReceptorInvoker(message, messageType) ?? throw new HandlerNotFoundException(messageType);

    // OPTIMIZATION: Skip envelope creation when trace store AND lifecycle invoker are null
    // This achieves zero allocation for high-throughput scenarios
    if (_traceStore != null || _lifecycleInvoker != null) {
      return _localInvokeVoidWithTracingAsyncInternalAsync<TMessage>(message, context, invoker, callerMemberName, callerFilePath, callerLineNumber);
    }

    // FAST PATH: Zero allocation when no tracing and no lifecycle invoker
    // Invoke using delegate - zero reflection, strongly typed
    // Avoid async/await state machine allocation by returning task directly
    return invoker(message);
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
    if (_traceStore != null) {
      await _traceStore.StoreAsync(envelope);
    }

    // Invoke using delegate - zero reflection, strongly typed
    await invoker(message!);

    // Invoke lifecycle receptors at ImmediateAsync stage (after receptor completes, before any database operations)
    if (_lifecycleInvoker is not null) {
      var lifecycleContext = new LifecycleExecutionContext {
        CurrentStage = LifecycleStage.ImmediateAsync,
        EventId = null,
        StreamId = null,
        LastProcessedEventId = null
      };
      await _lifecycleInvoker.InvokeAsync(message!, LifecycleStage.ImmediateAsync, lifecycleContext, default);
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
      Metadata = hopMetadata
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
      Metadata = hopMetadata
    };

    envelope.AddHop(hop);
    return envelope;
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
  public async Task PublishAsync<TEvent>(TEvent eventData) {
    if (eventData == null) {
      throw new ArgumentNullException(nameof(eventData));
    }

    var eventType = eventData.GetType();

    // Create MessageId once - used for outbox and will be used by process_work_batch for event storage
    var messageId = MessageId.New();

    // Get strongly-typed delegate from generated code
    var publisher = GetReceptorPublisher(eventData, eventType);

    // Invoke local handlers - zero reflection, strongly typed
    await publisher(eventData);

    // Publish event for cross-service delivery if work coordinator strategy is available
    // process_work_batch will store events to wh_event_store and create perspective events atomically
    await _publishToOutboxViaScopeAsync(eventData, eventType, messageId);
  }

  /// <summary>
  /// Publishes an event to the outbox for cross-service delivery using work coordinator strategy.
  /// Queues event for batched processing.
  /// Resolves IWorkCoordinatorStrategy from active scope (scoped service).
  /// Creates a complete MessageEnvelope with a hop indicating "stored to outbox".
  /// </summary>
  private async Task _publishToOutboxViaScopeAsync<TEvent>(TEvent eventData, Type eventType, MessageId messageId) {
    // Create scope to resolve scoped IWorkCoordinatorStrategy
    var scope = _scopeFactory.CreateScope();
    try {
      var strategy = scope.ServiceProvider.GetService<IWorkCoordinatorStrategy>();

      // If no strategy is registered, skip outbox routing (local-only event)
      if (strategy == null) {
        return;
      }

      // Resolve destination topic using registry and routing strategy
      var destination = _resolveEventTopic(eventType);

      // Create MessageEnvelope wrapping the event (using SAME messageId as event store)
      var envelope = new MessageEnvelope<TEvent> {
        MessageId = messageId,
        Payload = eventData,
        Hops = []
      };

      // Extract aggregate ID and add to hop metadata (for streamId extraction)
      var hopMetadata = _createHopMetadata(eventData!, eventType);

      // Add hop indicating message is being stored to outbox
      var hop = new MessageHop {
        Type = HopType.Current,
        ServiceInstance = _instanceProvider.ToInfo(),
        Topic = destination,
        Timestamp = DateTimeOffset.UtcNow,
        Metadata = hopMetadata
      };
      envelope.AddHop(hop);

      System.Diagnostics.Debug.WriteLine($"[Dispatcher] Queueing event {eventType.Name} to work coordinator with destination '{destination}'");

      // Serialize envelope to OutboxMessage
      var newOutboxMessage = _serializeToNewOutboxMessage(envelope, eventData!, eventType, destination);

      // Queue event for batched processing
      strategy.QueueOutboxMessage(newOutboxMessage);

      // Flush strategy to execute the batch
      await strategy.FlushAsync(WorkBatchFlags.None);

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

    // baseTopic should never be null here due to convention fallback, but add defensive check
    if (baseTopic == null) {
      throw new InvalidOperationException($"Unable to resolve base topic for event type {eventType.Name}");
    }

    // 3. Apply routing strategy (pool suffix, tenant prefix, etc.)
    // _topicRoutingStrategy is never null (defaults to PassthroughRoutingStrategy if not provided)
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
        throw new HandlerNotFoundException(messageType);
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

      // Return delivery receipt with Accepted status (message queued)
      return DeliveryReceipt.Accepted(
        envelope.MessageId,
        destination,
        context.CorrelationId,
        context.CausationId
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
        throw new HandlerNotFoundException(messageType);
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

      // Return delivery receipt with Accepted status (message queued)
      return DeliveryReceipt.Accepted(
        envelope.MessageId,
        destination,
        context.CorrelationId,
        context.CausationId
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

        // Create receipt for this message
        receipts.Add(DeliveryReceipt.Accepted(
          envelope.MessageId,
          destination,
          context.CorrelationId,
          context.CausationId
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
    string destination
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
  /// Tries to get aggregate ID from first hop metadata, falls back to message ID.
  /// </summary>
  private static Guid _extractStreamId(IMessageEnvelope envelope) {
    // Check first hop for aggregate ID or stream key
    var firstHop = envelope.Hops.FirstOrDefault();
    if (firstHop?.Metadata != null && firstHop.Metadata.TryGetValue("AggregateId", out var aggregateIdElem) &&
        aggregateIdElem.ValueKind == JsonValueKind.String) {
      var aggregateIdStr = aggregateIdElem.GetString();
      if (aggregateIdStr != null && Guid.TryParse(aggregateIdStr, out var parsedAggregateId)) {
        return parsedAggregateId;
      }
    }

    // Fall back to message ID (ensures all messages have a stream)
    return envelope.MessageId.Value;
  }

  /// <summary>
  /// Creates hop metadata with AggregateId extracted from the message.
  /// Returns null if no aggregate ID extractor is configured or no ID found.
  /// </summary>
  private Dictionary<string, JsonElement>? _createHopMetadata(object message, Type messageType) {
    if (_aggregateIdExtractor == null) {
      return null;
    }

    var aggregateId = _aggregateIdExtractor.ExtractAggregateId(message, messageType);
    if (aggregateId == null) {
      return null;
    }

    // Create JsonElement for aggregate ID (AOT-safe approach using JsonDocument.Parse)
    // Wrap GUID string in quotes for valid JSON string value
    var jsonString = $"\"{aggregateId.Value}\"";
    using var doc = JsonDocument.Parse(jsonString);
    var aggregateIdElement = doc.RootElement.Clone(); // Clone to survive disposal

    return new Dictionary<string, JsonElement> {
      ["AggregateId"] = aggregateIdElement
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
}
