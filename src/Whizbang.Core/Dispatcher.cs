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
public delegate Task ReceptorPublisher<in TEvent>(TEvent @event);

/// <summary>
/// Base dispatcher class with core logic. The source generator creates a derived class
/// that implements the abstract lookup methods, returning strongly-typed delegates.
/// This achieves zero-reflection while keeping functional logic in the base class.
/// </summary>
public abstract class Dispatcher(
  IServiceProvider serviceProvider,
  ITraceStore? traceStore = null,
  IOutbox? outbox = null,
  ITransport? transport = null,
  JsonSerializerOptions? jsonOptions = null
  ) : IDispatcher {
  private readonly IServiceProvider _internalServiceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
  private readonly IServiceScopeFactory _scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
  private readonly ITraceStore? _traceStore = traceStore;
  private readonly IOutbox? _outbox = outbox;
  private readonly ITransport? _transport = transport;
  private readonly JsonSerializerOptions? _jsonOptions = jsonOptions;

  /// <summary>
  /// Gets the service provider for receptor resolution.
  /// Available to generated derived class.
  /// </summary>
  protected IServiceProvider _serviceProvider => _internalServiceProvider;

  // ========================================
  // SEND PATTERN - Command Dispatch with Acknowledgment
  // ========================================

  /// <summary>
  /// Sends a typed message and returns a delivery receipt (AOT-compatible).
  /// Use this for async workflows, remote execution, or inbox pattern.
  /// Type information is preserved at compile time, avoiding reflection.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public Task<IDeliveryReceipt> SendAsync<TMessage>(TMessage message) where TMessage : notnull {
    var context = MessageContext.New();
    return SendAsync((object)message, context);
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
    var invoker = _getReceptorInvoker<object>(message, messageType);

    // If no local receptor exists, check for outbox fallback
    if (invoker == null) {
      // Try to resolve IOutbox from constructor parameter first, then from a scope (it may be Scoped, e.g., EF Core implementation)
      var scope = _scopeFactory.CreateScope();
      try {
        var outbox = _outbox ?? scope.ServiceProvider.GetService<IOutbox>();
        if (outbox != null && _jsonOptions != null) {
          // Route to outbox for remote delivery (AOT-compatible, no reflection)
          return await SendToOutboxViaScopeAsync(message, messageType, context, outbox, callerMemberName, callerFilePath, callerLineNumber);
        }
      } finally {
        // Dispose scope asynchronously to properly handle services that only implement IAsyncDisposable
        if (scope is IAsyncDisposable asyncDisposable) {
          await asyncDisposable.DisposeAsync();
        } else {
          scope.Dispose();
        }
      }

      // No receptor and no outbox - throw
      throw new HandlerNotFoundException(messageType);
    }

    // Create envelope with hop for observability
    var envelope = _createEnvelope(message, context, callerMemberName, callerFilePath, callerLineNumber);

    // Store envelope if trace store is configured
    if (_traceStore != null) {
      await _traceStore.StoreAsync(envelope);
    }

    // Invoke using delegate - zero reflection, strongly typed
    await invoker(message);

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
    return LocalInvokeAsync<TResult>((object)message, context, callerMemberName, callerFilePath, callerLineNumber);
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
    var invoker = _getReceptorInvoker<TResult>(message, messageType) ?? throw new HandlerNotFoundException(messageType);

    // OPTIMIZATION: Skip envelope creation when trace store is null
    // This achieves zero allocation for high-throughput scenarios
    if (_traceStore != null) {
      return _localInvokeWithTracingAsync(message, context, invoker, callerMemberName, callerFilePath, callerLineNumber);
    }

    // FAST PATH: Zero allocation when no tracing
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
    return LocalInvokeAsync((object)message, context, callerMemberName, callerFilePath, callerLineNumber);
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
    var invoker = _getVoidReceptorInvoker(message, messageType) ?? throw new HandlerNotFoundException(messageType);

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
  /// Creates a MessageEnvelope with initial hop containing caller information and context.
  /// </summary>
  private static IMessageEnvelope _createEnvelope<TMessage>(
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

    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "Unknown",
      Timestamp = DateTimeOffset.UtcNow,
      CorrelationId = context.CorrelationId,
      CausationId = context.CausationId,
      CallerMemberName = callerMemberName,
      CallerFilePath = callerFilePath,
      CallerLineNumber = callerLineNumber
    };

    envelope.AddHop(hop);
    return envelope;
  }

  /// <summary>
  /// Publishes an event to all registered handlers.
  /// Uses generated delegate to invoke receptors with zero reflection.
  /// After local handlers complete, publishes to outbox for cross-service delivery (if configured).
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public async Task PublishAsync<TEvent>(TEvent @event) {
    if (@event == null) {
      throw new ArgumentNullException(nameof(@event));
    }

    var eventType = @event.GetType();

    // If this is an IEvent, write to Event Store FIRST (which queues to IPerspectiveInvoker)
    // Create a scope to resolve scoped services (IEventStore, IPerspectiveInvoker)
    var scope = _scopeFactory.CreateScope();
    try {
      var eventStore = scope.ServiceProvider.GetService<IEventStore>();
      var aggregateIdExtractor = scope.ServiceProvider.GetService<IAggregateIdExtractor>();
      var perspectiveInvoker = scope.ServiceProvider.GetService<IPerspectiveInvoker>();

      if (@event is IEvent && eventStore != null) {
        // Create envelope with event
        var messageId = MessageId.New();
        var envelope = new MessageEnvelope<TEvent> {
          MessageId = messageId,
          Payload = @event,
          Hops = []
        };

        // Add hop indicating message is being stored to event store
        var hop = new MessageHop {
          Type = HopType.Current,
          ServiceName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "Unknown",
          Timestamp = DateTimeOffset.UtcNow
        };
        envelope.AddHop(hop);

        // Determine stream ID: use aggregate ID if available, otherwise use message ID
        // This ensures ALL events are persisted while maintaining proper streams for aggregates
        Guid streamId;
        if (aggregateIdExtractor != null) {
          var aggregateId = aggregateIdExtractor.ExtractAggregateId(@event, eventType);
          streamId = aggregateId ?? messageId.Value;
        } else {
          streamId = messageId.Value;
        }

        // Write to Event Store (this queues event to IPerspectiveInvoker)
        await eventStore.AppendAsync(streamId, envelope);
      }

      // Manually dispose the perspective invoker to invoke perspectives
      // Must call DisposeAsync since IPerspectiveInvoker only implements IAsyncDisposable
      if (perspectiveInvoker != null) {
        await perspectiveInvoker.DisposeAsync();
      }
    } finally {
      // Dispose scope asynchronously to properly handle services that only implement IAsyncDisposable
      if (scope is IAsyncDisposable asyncDisposable) {
        await asyncDisposable.DisposeAsync();
      } else {
        scope.Dispose();
      }
    }

    // Get strongly-typed delegate from generated code
    var publisher = _getReceptorPublisher(@event, eventType);

    // Invoke local handlers - zero reflection, strongly typed
    await publisher(@event);

    // If outbox is configured, publish event for cross-service delivery
    // Resolve IOutbox from a scope (it may be Scoped, e.g., EF Core implementation)
    var publishScope = _scopeFactory.CreateScope();
    try {
      var publishOutbox = publishScope.ServiceProvider.GetService<IOutbox>();
      if (publishOutbox != null && _jsonOptions != null) {
        await PublishToOutboxViaScopeAsync(@event, eventType, publishOutbox);
      }
    } finally {
      // Dispose scope asynchronously to properly handle services that only implement IAsyncDisposable
      if (publishScope is IAsyncDisposable asyncDisposable) {
        await asyncDisposable.DisposeAsync();
      } else {
        publishScope.Dispose();
      }
    }
  }

  /// <summary>
  /// Publishes an event to the outbox for cross-service delivery using a scoped outbox instance.
  /// The OutboxPublisherWorker will poll the outbox and publish to the transport.
  /// Creates a complete MessageEnvelope with a hop indicating "stored to outbox".
  /// </summary>
  private async Task PublishToOutboxViaScopeAsync<TEvent>(TEvent @event, Type eventType, IOutbox outbox) {
    // Determine destination topic from event type name
    // TODO: Make this configurable via IEventRoutingConfiguration
    var destination = DetermineEventTopic(eventType);

    // Create MessageEnvelope wrapping the event
    var messageId = MessageId.New();
    var envelope = new MessageEnvelope<TEvent> {
      MessageId = messageId,
      Payload = @event,
      Hops = []
    };

    // Add hop indicating message is being stored to outbox
    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "Unknown",
      Topic = destination,
      Timestamp = DateTimeOffset.UtcNow
    };
    envelope.AddHop(hop);

    // Store complete envelope in outbox (will be serialized to JSONB)
    System.Diagnostics.Debug.WriteLine($"[Dispatcher] Storing event {eventType.Name} to outbox with destination '{destination}'");
    await outbox.StoreAsync<TEvent>(envelope, destination);
    System.Diagnostics.Debug.WriteLine($"[Dispatcher] Successfully stored event {eventType.Name} to outbox");
  }

  /// <summary>
  /// Determines the Service Bus topic for an event type.
  /// Convention: ProductCreatedEvent → "products", InventoryRestockedEvent → "inventory"
  /// </summary>
  private static string DetermineEventTopic(Type eventType) {
    var typeName = eventType.Name;

    // Convention-based routing: ProductXxxEvent → "products", InventoryXxxEvent → "inventory"
    if (typeName.StartsWith("Product")) {
      return "products";
    }

    if (typeName.StartsWith("Inventory")) {
      return "inventory";
    }

    if (typeName.StartsWith("Order")) {
      return "orders";
    }

    // Default: use lowercase type name without "Event" suffix
    return typeName.Replace("Event", "").ToLowerInvariant();
  }

  /// <summary>
  /// Determines the Service Bus topic for a command type.
  /// Convention: CreateProductCommand → "products", UpdateInventoryCommand → "inventory"
  /// </summary>
  private static string DetermineCommandDestination(Type messageType) {
    var typeName = messageType.Name;

    // Convention-based routing: ProductXxxCommand → "products", InventoryXxxCommand → "inventory"
    if (typeName.StartsWith("Product") || typeName.StartsWith("CreateProduct")) {
      return "products";
    }

    if (typeName.StartsWith("Inventory")) {
      return "inventory";
    }

    if (typeName.StartsWith("Order")) {
      return "orders";
    }

    // Default: use lowercase type name without "Command" suffix
    return typeName.Replace("Command", "").ToLowerInvariant();
  }

  /// <summary>
  /// Sends a message to the outbox for remote delivery using a scoped outbox instance.
  /// Creates a MessageEnvelope with proper type information and stores it in the outbox.
  /// The OutboxPublisherWorker will poll the outbox and publish to the transport.
  /// AOT-compatible - uses non-generic IOutbox.StoreAsync overload, no reflection.
  /// </summary>
  private async Task<IDeliveryReceipt> SendToOutboxViaScopeAsync(
    object message,
    Type messageType,
    IMessageContext context,
    IOutbox outbox,
    string callerMemberName,
    string callerFilePath,
    int callerLineNumber
  ) {
    // Determine destination topic from message type name
    var destination = DetermineCommandDestination(messageType);

    // Create envelope with hop for observability (returns IMessageEnvelope)
    var envelope = _createEnvelope(message, context, callerMemberName, callerFilePath, callerLineNumber);

    // Store in outbox using non-generic overload (AOT-compatible, no reflection)
    await outbox.StoreAsync(envelope, destination);

    // Return delivery receipt with Accepted status (message queued, not yet delivered)
    return DeliveryReceipt.Accepted(
      envelope.MessageId,
      destination,
      context.CorrelationId,
      context.CausationId
    );
  }

  // ========================================
  // BATCH OPERATIONS
  // ========================================

  /// <summary>
  /// Sends multiple messages and collects all delivery receipts.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public async Task<IEnumerable<IDeliveryReceipt>> SendManyAsync(IEnumerable<object> messages) {
    ArgumentNullException.ThrowIfNull(messages);

    var receipts = new List<IDeliveryReceipt>();
    foreach (var message in messages) {
      var receipt = await SendAsync(message);
      receipts.Add(receipt);
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

  /// <summary>
  /// Implemented by generated code - returns a strongly-typed delegate for invoking a receptor.
  /// The delegate encapsulates the receptor lookup and invocation with zero reflection.
  /// </summary>
  protected abstract ReceptorInvoker<TResult>? _getReceptorInvoker<TResult>(object message, Type messageType);

  /// <summary>
  /// Implemented by generated code - returns a strongly-typed delegate for invoking a void receptor.
  /// The delegate encapsulates the receptor lookup and invocation with zero reflection.
  /// Returns null if no void receptor is registered for the message type.
  /// </summary>
  protected abstract VoidReceptorInvoker? _getVoidReceptorInvoker(object message, Type messageType);

  /// <summary>
  /// Implemented by generated code - returns a strongly-typed delegate for publishing to receptors.
  /// The delegate encapsulates finding all receptors and invoking them with zero reflection.
  /// </summary>
  protected abstract ReceptorPublisher<TEvent> _getReceptorPublisher<TEvent>(TEvent @event, Type eventType);
}
