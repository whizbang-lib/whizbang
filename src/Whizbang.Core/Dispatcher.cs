#pragma warning disable S3604, S3928 // Primary constructor field/property initializers are intentional

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
using Microsoft.Extensions.Options;
using Whizbang.Core.AutoPopulate;
using Whizbang.Core.Configuration;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Tags;
using Whizbang.Core.Tracing;
using Whizbang.Core.Transports;
using Whizbang.Core.Validation;
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
/// <docs>fundamentals/receptors/receptors#synchronous-receptors</docs>
public delegate TResult SyncReceptorInvoker<out TResult>(object message);

/// <summary>
/// Delegate for invoking a void synchronous receptor's Handle method.
/// Generated code creates these delegates with proper type safety - zero reflection.
/// </summary>
/// <docs>fundamentals/receptors/receptors#synchronous-receptors</docs>
public delegate void VoidSyncReceptorInvoker(object message);

/// <summary>
/// Base dispatcher class with core logic. The source generator creates a derived class
/// that implements the abstract lookup methods, returning strongly-typed delegates.
/// This achieves zero-reflection while keeping functional logic in the base class.
/// </summary>
/// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherTests.cs</tests>
/// <tests>tests/Whizbang.Core.Integration.Tests/DispatcherReceptorIntegrationTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "Parameters 'jsonOptions' and 'receptorInvoker' retained for backward compatibility with generated code")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "S1172:Unused method parameters should be removed", Justification = "Parameters 'jsonOptions' and 'receptorInvoker' retained for backward compatibility with generated code")]
#pragma warning disable CS9113 // Primary constructor parameter is unread - retained for backward compatibility with generated code
public abstract partial class Dispatcher(
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
  IStreamIdExtractor? streamIdExtractor = null,
  IReceptorRegistry? receptorRegistry = null,
  IScopedEventTracker? scopedEventTracker = null,
  ISyncEventTracker? syncEventTracker = null,
  ITrackedEventTypeRegistry? trackedEventTypeRegistry = null,
  IOptionsMonitor<TracingOptions>? tracingOptions = null,
  CascadeContextFactory? cascadeContextFactory = null
  ) : IDispatcher {
#pragma warning restore CS9113
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
  // Scoped event tracker for perspective sync - tracks events cascaded within this scope
  // NOTE: For singleton Dispatcher, this field will be null. Instead, use ScopedEventTrackerAccessor
  // which provides access to the current scope's tracker via AsyncLocal.
  // When both the field and accessor are null, event tracking for cascade is disabled.
  private readonly IScopedEventTracker? _scopedEventTracker = scopedEventTracker;
  // Singleton event tracker for cross-scope perspective sync - tracks events for cross-request awaiting
  // CRITICAL: This enables Route.Local() events to be tracked for sync BEFORE they hit the database
  private readonly ISyncEventTracker? _syncEventTracker = syncEventTracker ?? serviceProvider.GetService<ISyncEventTracker>();
  // Registry of event types that should be tracked for perspective sync
  private readonly ITrackedEventTypeRegistry? _trackedEventTypeRegistry = trackedEventTypeRegistry ?? serviceProvider.GetService<ITrackedEventTypeRegistry>();
  // Resolve from service provider if not injected (for backwards compatibility with generated code)
  private readonly IEnvelopeSerializer? _envelopeSerializer = envelopeSerializer ?? serviceProvider.GetService<IEnvelopeSerializer>();
  // Resolve from service provider if not injected (for backwards compatibility with generated code)
  private readonly IEnvelopeRegistry? _envelopeRegistry = envelopeRegistry ?? serviceProvider.GetService<IEnvelopeRegistry>();
  // Outbox routing strategy for determining actual transport destinations (inbox for commands, namespace for events)
  private readonly IOutboxRoutingStrategy? _outboxRoutingStrategy = outboxRoutingStrategy ?? serviceProvider.GetService<IOutboxRoutingStrategy>();
  // Owned domains for routing decisions - resolved from RoutingOptions if available
  private readonly HashSet<string> _ownedDomains = _resolveOwnedDomains(serviceProvider);
  // Whizbang options for runtime configuration (auto-generate StreamIds, etc.)
#pragma warning disable S4487, S1144 // Pre-resolved for use by subclasses and future features
  private readonly WhizbangOptions _whizbangOptions = serviceProvider.GetService<Microsoft.Extensions.Options.IOptions<WhizbangOptions>>()?.Value ?? new WhizbangOptions();
#pragma warning restore S4487, S1144
  // Core options for tag processing configuration
  private readonly WhizbangCoreOptions _coreOptions = serviceProvider.GetService<WhizbangCoreOptions>() ?? new WhizbangCoreOptions();
  // Message tag processor - invoked after successful receptor completion
  private readonly IMessageTagProcessor? _messageTagProcessor = serviceProvider.GetService<IMessageTagProcessor>();
  // Auto-populate processor - populates message properties from envelope context
  private readonly IAutoPopulateProcessor _autoPopulateProcessor = serviceProvider.GetService<IAutoPopulateProcessor>() ?? new AutoPopulateProcessor();
  // Tracing options for component-level control (Lifecycle, Handlers, etc.)
#pragma warning disable S4487, S1144 // Pre-resolved for use by subclasses and future features
  private readonly IOptionsMonitor<TracingOptions>? _tracingOptions = tracingOptions ?? serviceProvider.GetService<IOptionsMonitor<TracingOptions>>();
#pragma warning restore S4487, S1144
  // Cascade context factory for unified context propagation
  private readonly CascadeContextFactory _cascadeContextFactory = cascadeContextFactory ?? serviceProvider.GetService<CascadeContextFactory>() ?? new CascadeContextFactory(null);
  // Event completion awaiter for waiting on all perspectives to process events (RPC waiting)
  private readonly IEventCompletionAwaiter? _eventCompletionAwaiter = serviceProvider.GetService<IEventCompletionAwaiter>();
  // Dispatcher metrics for observability (optional - null when not registered)
  private readonly DispatcherMetrics? _dispatcherMetrics = serviceProvider.GetService<DispatcherMetrics>();
  // Security context accessor is resolved lazily from scope - it's a scoped service
  // DO NOT resolve in constructor - will fail with "Cannot resolve scoped service from root provider"

  // Lazy-resolved logger for diagnostic tracing (avoids constructor changes)
  // Uses try-catch to handle ObjectDisposedException during shutdown gracefully
  private ILogger? _cascadeLogger;
#pragma warning disable IDE1006 // Naming rule - property follows internal naming convention
  private ILogger CascadeLogger {
    get {
      if (_cascadeLogger is not null) {
        return _cascadeLogger;
      }
      try {
        _cascadeLogger = _internalServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("Whizbang.Core.Dispatcher.Cascade")
          ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
      } catch (ObjectDisposedException) {
        // Service provider disposed during shutdown - use null logger
        _cascadeLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
      }
      return _cascadeLogger;
    }
  }
#pragma warning restore IDE1006

  // Lazy-resolved logger for dispatcher diagnostic tracing
  private ILogger? _dispatcherLogger;
#pragma warning disable IDE1006 // Naming rule - property follows internal naming convention
  private ILogger DispatcherLogger {
    get {
      if (_dispatcherLogger is not null) {
        return _dispatcherLogger;
      }
      try {
        _dispatcherLogger = _internalServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("Whizbang.Core.Dispatcher")
          ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
      } catch (ObjectDisposedException) {
        _dispatcherLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
      }
      return _dispatcherLogger;
    }
  }
#pragma warning restore IDE1006

  /// <summary>
  /// Resolves owned domains from RoutingOptions in DI container.
  /// </summary>
  private static HashSet<string> _resolveOwnedDomains(IServiceProvider sp) {
    var routingOptions = sp.GetService<Microsoft.Extensions.Options.IOptions<RoutingOptions>>()?.Value;
    return routingOptions?.OwnedDomains?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
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
  // PERSPECTIVE SYNC AWAIT HELPER
  // ========================================

  /// <summary>
  /// Awaits perspective sync if the receptor has [AwaitPerspectiveSync] attributes.
  /// Called before invoking receptors locally to ensure perspective has processed events.
  /// This enables cross-scope sync where one handler emits events and another handler
  /// waits for the perspective to process them before firing.
  /// </summary>
  /// <docs>fundamentals/perspectives/perspective-sync#dispatcher-integration</docs>
  private async ValueTask _awaitPerspectiveSyncIfNeededAsync(
      object message,
      Type messageType,
      CancellationToken ct = default) {
    // Perspectives only process events, not commands or other message types.
    // Waiting for perspective sync on a non-event would wait forever and timeout.
    if (message is not IEvent) {
      return;
    }

    // Short-circuit if no receptor registry available
    if (_receptorRegistry is null) {
      return;
    }

    // Get receptors for LocalImmediateInline stage (local dispatch stage)
    var receptors = _receptorRegistry.GetReceptorsFor(messageType, LifecycleStage.LocalImmediateInline);

    // Check if any receptor has sync attributes
    var syncReceptor = receptors.FirstOrDefault(r => r.SyncAttributes is { Count: > 0 });
    if (syncReceptor?.SyncAttributes is null) {
      return;
    }

    // Extract stream ID from message
    var streamId = _streamIdExtractor?.ExtractStreamId(message, messageType);
    if (streamId is null) {
      // No stream ID - can't do stream-based sync
      return;
    }

    // Create a scope to resolve scoped services (IPerspectiveSyncAwaiter)
    await using var scope = _scopeFactory.CreateAsyncScope();
    var syncAwaiter = scope.ServiceProvider.GetService<IPerspectiveSyncAwaiter>();
    if (syncAwaiter is null) {
      return;
    }

    // Await sync for each attribute
    foreach (var syncAttr in syncReceptor.SyncAttributes) {
      var timeout = TimeSpan.FromMilliseconds(syncAttr.EffectiveTimeoutMs);
      var eventTypes = syncAttr.EventTypes?.ToArray();

      // Note: No eventIdToAwait here because we're in the originating scope
      // The singleton tracker should have the events from this scope
      var syncResult = await syncAwaiter.WaitForStreamAsync(
          syncAttr.PerspectiveType,
          streamId.Value,
          eventTypes,
          timeout,
          eventIdToAwait: null,
          ct);

      // Create and set SyncContext for receptor access via AsyncLocal
      var syncContext = new SyncContext {
        StreamId = streamId.Value,
        PerspectiveType = syncAttr.PerspectiveType,
        Outcome = syncResult.Outcome,
        EventsAwaited = syncResult.EventsAwaited,
        ElapsedTime = syncResult.ElapsedTime,
        FailureReason = syncResult.Outcome == SyncOutcome.TimedOut ? "Timeout exceeded" : null
      };
      SyncContextAccessor.CurrentContext = syncContext;

      // If FireBehavior is FireOnSuccess and we timed out, throw an exception
      if (syncAttr.FireBehavior == SyncFireBehavior.FireOnSuccess && syncResult.Outcome == SyncOutcome.TimedOut) {
        throw new PerspectiveSyncTimeoutException(
            syncAttr.PerspectiveType,
            timeout,
            $"Perspective sync timed out waiting for {syncAttr.PerspectiveType.Name} before invoking receptor {syncReceptor.ReceptorId}");
      }
      // FireBehavior.FireAlways continues regardless of timeout
    }
  }

  /// <summary>
  /// Waits for all perspectives to process cascaded events when WaitForPerspectives is enabled.
  /// Called at the end of LocalInvokeAsync methods that accept DispatchOptions.
  /// </summary>
  /// <remarks>
  /// <para>
  /// This method uses <see cref="IEventCompletionAwaiter"/> to wait for ALL perspectives
  /// to process the events that were cascaded during the receptor invocation.
  /// </para>
  /// <para>
  /// Events are tracked via <see cref="IScopedEventTracker"/> or <see cref="ScopedEventTrackerAccessor"/>.
  /// </para>
  /// </remarks>
  /// <docs>fundamentals/perspectives/event-completion#dispatcher-integration</docs>
  private async ValueTask _waitForPerspectivesIfNeededAsync(DispatchOptions options) {
    // Short-circuit if not waiting for perspectives
    if (!options.WaitForPerspectives) {
      return;
    }

    // Short-circuit if no event completion awaiter available
    if (_eventCompletionAwaiter is null) {
      return;
    }

    // Get the scoped event tracker (field or from AsyncLocal accessor)
    var scopedTracker = _scopedEventTracker ?? ScopedEventTrackerAccessor.CurrentTracker;
    if (scopedTracker is null) {
      return;
    }

    // Get the event IDs that were emitted
    var emittedEvents = scopedTracker.GetEmittedEvents();
    if (emittedEvents.Count == 0) {
      return;
    }

    var eventIds = emittedEvents.Select(e => e.EventId).Distinct().ToList();

    // Wait for all perspectives to process these events
    var success = await _eventCompletionAwaiter.WaitForEventsAsync(
        eventIds,
        options.PerspectiveWaitTimeout,
        options.CancellationToken);

    if (!success) {
      throw new PerspectiveSyncTimeoutException(
          $"Timed out waiting for {eventIds.Count} events to be processed by all perspectives. " +
          $"Timeout: {options.PerspectiveWaitTimeout.TotalMilliseconds}ms");
    }
  }

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
    var cascade = _cascadeContextFactory.NewRoot();
    var context = MessageContext.Create(cascade);
    return _sendAsyncInternalAsync<TMessage>(message, context);
  }

  /// <summary>
  /// Sends a message and returns a delivery receipt (not the business result).
  /// Creates a new message context automatically using CascadeContextFactory for proper security propagation.
  /// For AOT compatibility, use the generic overload SendAsync&lt;TMessage&gt;.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public Task<IDeliveryReceipt> SendAsync(object message) {
    var cascade = _cascadeContextFactory.NewRoot();
    var context = MessageContext.Create(cascade);
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

    var sw = Stopwatch.StartNew();
    string? messageTypeName = null;
    try {
      // Unwrap Routed<T> if needed - users can call SendAsync(Route.Local(event))
      // We extract the inner message and use that for receptor dispatch
      if (message is IRouted routed) {
        // RoutedNone (Route.None()) has no inner value to dispatch
        if (routed.Mode == DispatchMode.None || routed.Value == null) {
          throw new ArgumentException("Cannot send a RoutedNone (Route.None()) - it has no inner message to dispatch.", nameof(message));
        }
        message = routed.Value;
      }

      var messageType = message.GetType();
      messageTypeName = messageType.Name;

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

        // Start dispatch activity to serve as parent for handler traces
        // Handler traces created via ITracer.BeginHandlerTrace will link to this activity
        using var dispatchActivity = WhizbangActivitySource.Execution.StartActivity($"Dispatch {messageType.Name}");
        dispatchActivity?.SetTag("whizbang.message.type", messageType.FullName);
        dispatchActivity?.SetTag("whizbang.message.id", envelope.MessageId.ToString());
        dispatchActivity?.SetTag("whizbang.correlation.id", envelope.GetCorrelationId()?.ToString());

        // Await perspective sync if receptor has [AwaitPerspectiveSync] attributes
        // This enables cross-scope sync where one handler emits events and another waits
        await _awaitPerspectiveSyncIfNeededAsync(message, messageType);

        // Invoke using delegate - zero reflection, strongly typed
        var result = await invoker(message);

        // Auto-cascade: Extract and publish any IEvent instances from result (tuples, arrays, etc.)
        // Pass messageType so we can look up receptor's [DefaultRouting] attribute
        await _cascadeEventsFromResultAsync(result, messageType, sourceEnvelope: envelope);

        // Process tags after successful receptor completion
        await _processTagsIfEnabledAsync(message, messageType);

        // Invoke ImmediateAsync lifecycle receptors after business receptor completes
        await _invokeImmediateAsyncReceptorsAsync(envelope, messageType);

      } finally {
        // Unregister envelope after receptor completes (or throws)
        _envelopeRegistry?.Unregister(envelope);
      }

      _dispatcherMetrics?.MessagesDispatched.Add(1,
        new KeyValuePair<string, object?>("message_type", messageTypeName),
        new KeyValuePair<string, object?>("pattern", "send"));

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
    } catch (Exception ex) {
      _dispatcherMetrics?.Errors.Add(1,
        new KeyValuePair<string, object?>("message_type", messageTypeName ?? "Unknown"),
        new KeyValuePair<string, object?>("error_type", ex.GetType().Name));
      throw;
    } finally {
      sw.Stop();
      _dispatcherMetrics?.SendDuration.Record(sw.Elapsed.TotalMilliseconds);
    }
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
    var cascade = _cascadeContextFactory.NewRoot();
    var context = MessageContext.Create(cascade);
    return _sendAsyncInternalWithOptionsAsync<TMessage>(message, context, options);
  }

  /// <summary>
  /// Sends a message with dispatch options.
  /// Uses CascadeContextFactory for proper security propagation.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public Task<IDeliveryReceipt> SendAsync(object message, DispatchOptions options) {
    options.CancellationToken.ThrowIfCancellationRequested();
    var cascade = _cascadeContextFactory.NewRoot();
    var context = MessageContext.Create(cascade);
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

    var sw = Stopwatch.StartNew();
    string? messageTypeName = null;
    try {
      // Unwrap Routed<T> if needed - users can call SendAsync(Route.Local(event))
      if (message is IRouted routed) {
        if (routed.Mode == DispatchMode.None || routed.Value == null) {
          throw new ArgumentException("Cannot send a RoutedNone (Route.None()) - it has no inner message to dispatch.", nameof(message));
        }
        message = routed.Value;
      }

      var messageType = message.GetType();
      messageTypeName = messageType.Name;
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

        // Start dispatch activity to serve as parent for handler traces
        using var dispatchActivity = WhizbangActivitySource.Execution.StartActivity($"Dispatch {messageType.Name}");
        dispatchActivity?.SetTag("whizbang.message.type", messageType.FullName);
        dispatchActivity?.SetTag("whizbang.message.id", envelope.MessageId.ToString());
        dispatchActivity?.SetTag("whizbang.correlation.id", envelope.GetCorrelationId()?.ToString());

        // Await perspective sync if receptor has [AwaitPerspectiveSync] attributes
        await _awaitPerspectiveSyncIfNeededAsync(message, messageType, options.CancellationToken);

        var result = await invoker(message);
        await _cascadeEventsFromResultAsync(result, messageType);

        // Process tags after successful receptor completion
        await _processTagsIfEnabledAsync(message, messageType);

        // Invoke ImmediateAsync lifecycle receptors after business receptor completes
        await _invokeImmediateAsyncReceptorsAsync(envelope, messageType);
      } finally {
        _envelopeRegistry?.Unregister(envelope);
      }

      _dispatcherMetrics?.MessagesDispatched.Add(1,
        new KeyValuePair<string, object?>("message_type", messageTypeName),
        new KeyValuePair<string, object?>("pattern", "send"));

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
    } catch (Exception ex) {
      _dispatcherMetrics?.Errors.Add(1,
        new KeyValuePair<string, object?>("message_type", messageTypeName ?? "Unknown"),
        new KeyValuePair<string, object?>("error_type", ex.GetType().Name));
      throw;
    } finally {
      sw.Stop();
      _dispatcherMetrics?.SendDuration.Record(sw.Elapsed.TotalMilliseconds);
    }
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

    var sw = Stopwatch.StartNew();
    var messageType = typeof(TMessage);
    var messageTypeName = messageType.Name;
    try {
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

        // Start dispatch activity to serve as parent for handler traces
        // Handler traces created via ITracer.BeginHandlerTrace will link to this activity
        var parentActivity = Activity.Current;
        using var dispatchActivity = WhizbangActivitySource.Execution.StartActivity($"Dispatch {messageType.Name}", ActivityKind.Internal);
        if (dispatchActivity != null) {
          dispatchActivity.SetTag("whizbang.message.type", messageType.FullName);
          dispatchActivity.SetTag("whizbang.message.id", envelope.MessageId.ToString());
          dispatchActivity.SetTag("whizbang.correlation.id", envelope.GetCorrelationId()?.ToString());
          dispatchActivity.SetTag("whizbang.debug.parent.id", parentActivity?.Id ?? "none");
          dispatchActivity.SetTag("whizbang.debug.parent.source", parentActivity?.Source?.Name ?? "none");
        }

        // Await perspective sync if receptor has [AwaitPerspectiveSync] attributes
        await _awaitPerspectiveSyncIfNeededAsync(message, messageType);

        // Invoke using delegate - zero reflection, strongly typed
        var result = await invoker(message);

        // Auto-cascade: Extract and publish any IEvent instances from result (tuples, arrays, etc.)
        await _cascadeEventsFromResultAsync(result, messageType, sourceEnvelope: envelope);

        // Process tags after successful receptor completion
        await _processTagsIfEnabledAsync(message, messageType);

        // Invoke ImmediateAsync lifecycle receptors after business receptor completes
        await _invokeImmediateAsyncReceptorsAsync(envelope, messageType);

      } finally {
        // Unregister envelope after receptor completes (or throws)
        _envelopeRegistry?.Unregister(envelope);
      }

      _dispatcherMetrics?.MessagesDispatched.Add(1,
        new KeyValuePair<string, object?>("message_type", messageTypeName),
        new KeyValuePair<string, object?>("pattern", "send"));

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
    } catch (Exception ex) {
      _dispatcherMetrics?.Errors.Add(1,
        new KeyValuePair<string, object?>("message_type", messageTypeName),
        new KeyValuePair<string, object?>("error_type", ex.GetType().Name));
      throw;
    } finally {
      sw.Stop();
      _dispatcherMetrics?.SendDuration.Record(sw.Elapsed.TotalMilliseconds);
    }
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

    var sw = Stopwatch.StartNew();
    var messageType = typeof(TMessage);
    var messageTypeName = messageType.Name;
    try {
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

        // Start dispatch activity to serve as parent for handler traces
        // Handler traces created via ITracer.BeginHandlerTrace will link to this activity
        using var dispatchActivity = WhizbangActivitySource.Execution.StartActivity($"Dispatch {messageType.Name}");
        dispatchActivity?.SetTag("whizbang.message.type", messageType.FullName);
        dispatchActivity?.SetTag("whizbang.message.id", envelope.MessageId.ToString());
        dispatchActivity?.SetTag("whizbang.correlation.id", envelope.GetCorrelationId()?.ToString());

        // Await perspective sync if receptor has [AwaitPerspectiveSync] attributes
        await _awaitPerspectiveSyncIfNeededAsync(message, messageType, options.CancellationToken);

        var result = await invoker(message);
        await _cascadeEventsFromResultAsync(result, messageType, sourceEnvelope: envelope);

        // Process tags after successful receptor completion
        await _processTagsIfEnabledAsync(message, messageType);

        // Invoke ImmediateAsync lifecycle receptors after business receptor completes
        await _invokeImmediateAsyncReceptorsAsync(envelope, messageType);

      } finally {
        _envelopeRegistry?.Unregister(envelope);
      }

      _dispatcherMetrics?.MessagesDispatched.Add(1,
        new KeyValuePair<string, object?>("message_type", messageTypeName),
        new KeyValuePair<string, object?>("pattern", "send"));

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
    } catch (Exception ex) {
      _dispatcherMetrics?.Errors.Add(1,
        new KeyValuePair<string, object?>("message_type", messageTypeName),
        new KeyValuePair<string, object?>("error_type", ex.GetType().Name));
      throw;
    } finally {
      sw.Stop();
      _dispatcherMetrics?.SendDuration.Record(sw.Elapsed.TotalMilliseconds);
    }
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
    var cascade = _cascadeContextFactory.NewRoot();
    var context = MessageContext.Create(cascade);
    return LocalInvokeAsync<TResult>((object)message, context);
  }

  /// <summary>
  /// Invokes a receptor in-process and returns the typed business result.
  /// Creates a new message context automatically using CascadeContextFactory for proper security propagation.
  /// PERFORMANCE: Zero allocation when trace store is null, target &lt; 20ns per invocation.
  /// For AOT compatibility, use the generic overload LocalInvokeAsync&lt;TMessage, TResult&gt;.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public ValueTask<TResult> LocalInvokeAsync<TResult>(object message) {
    var cascade = _cascadeContextFactory.NewRoot();
    var context = MessageContext.Create(cascade);
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
  /// Always creates envelope for full tracing and cascade context.
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

    // Set message context accessor so receptors can inject IMessageContext
    // This enables cascaded receptors to access UserId, TenantId, MessageId, etc.
    MessageContextAccessor.CurrentContext = context;

    // Unwrap Routed<T> if needed - users can call LocalInvokeAsync(Route.Local(event))
    if (message is IRouted routed) {
      if (routed.Mode == DispatchMode.None || routed.Value == null) {
        throw new ArgumentException("Cannot invoke a RoutedNone (Route.None()) - it has no inner message to dispatch.", nameof(message));
      }
      message = routed.Value;
    }

    var messageType = message.GetType();

    // Try async receptor first (async takes precedence)
    var asyncInvoker = GetReceptorInvoker<TResult>(message, messageType);
    if (asyncInvoker != null) {
      // Use wrapper that catches InvalidCastException and falls back to RPC extraction
      // This handles the case where receptor returns a complex type (tuple, etc.)
      // but caller requests a specific type from within that complex type
      return _localInvokeWithCastFallbackAsync(asyncInvoker, message, messageType, context, callerMemberName, callerFilePath, callerLineNumber);
    }

    // Fallback to sync receptor - wrap as async and route through tracing path
    var syncInvoker = GetSyncReceptorInvoker<TResult>(message, messageType);
    if (syncInvoker != null) {
      ReceptorInvoker<TResult> wrappedInvoker = (msg) => new ValueTask<TResult>(syncInvoker(msg));
      return _localInvokeWithCastFallbackAsync(wrappedInvoker, message, messageType, context, callerMemberName, callerFilePath, callerLineNumber);
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
  /// <docs>fundamentals/dispatcher/rpc-extraction</docs>
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
    var sw = Stopwatch.StartNew();
    try {
      // Await perspective sync if receptor has [AwaitPerspectiveSync] attributes
      await _awaitPerspectiveSyncIfNeededAsync(message, messageType);

      var result = await _localInvokeWithTracingAsync(message, messageType, context, asyncInvoker, callerMemberName, callerFilePath, callerLineNumber);

      _dispatcherMetrics?.MessagesDispatched.Add(1,
        new KeyValuePair<string, object?>("message_type", messageType.Name),
        new KeyValuePair<string, object?>("pattern", "local_invoke"));

      return result;
    } catch (InvalidCastException) {
      // The typed invoker failed because the receptor returns a complex type
      // containing TResult, not TResult directly. Fall back to RPC extraction.
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
      if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
        var msgTypeName = messageType.Name;
        var resultTypeName = typeof(TResult).Name;
        CascadeLogger.LogDebug("[RPC] InvalidCastException caught, falling back to RPC extraction for {MessageType} -> {ResultType}",
          msgTypeName, resultTypeName);
      }
#pragma warning restore CA1848

      var anyInvoker = GetReceptorInvokerAny(message, messageType);
      if (anyInvoker != null) {
        return await _localInvokeWithRpcExtractionAsync<TResult>(anyInvoker, message, messageType);
      }

      // If no invoker found at all, re-throw the original exception
      throw;
    } catch (Exception ex) {
      _dispatcherMetrics?.Errors.Add(1,
        new KeyValuePair<string, object?>("message_type", messageType.Name),
        new KeyValuePair<string, object?>("error_type", ex.GetType().Name));
      throw;
    } finally {
      sw.Stop();
      _dispatcherMetrics?.LocalInvokeDuration.Record(sw.Elapsed.TotalMilliseconds);
    }
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
  /// <docs>fundamentals/dispatcher/rpc-extraction</docs>
  /// <tests>Whizbang.Core.Tests/Dispatcher/DispatcherRpcExtractionTests.cs</tests>
  private async ValueTask<TResult> _localInvokeWithRpcExtractionAsync<TResult>(
    Func<object, ValueTask<object?>> invoker,
    object message,
    Type messageType
  ) {
    // Start dispatch activity to serve as parent for handler traces
    using var dispatchActivity = WhizbangActivitySource.Execution.StartActivity($"Dispatch {messageType.Name}");
    dispatchActivity?.SetTag("whizbang.message.type", messageType.FullName);

#pragma warning disable CA1848 // Diagnostic logging - performance not critical
    if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
      var msgTypeName = messageType.Name;
      var resultTypeName = typeof(TResult).Name;
      CascadeLogger.LogDebug("[RPC] RpcExtraction: Invoking receptor for {MessageType}, extracting {ResultType}",
        msgTypeName, resultTypeName);
    }

    // 1. Invoke receptor to get full result
    var fullResult = await invoker(message);
    if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
      var resultTypeName = fullResult?.GetType().Name ?? "null";
      var isNull = fullResult == null;
      CascadeLogger.LogDebug("[RPC] RpcExtraction: Receptor returned {ResultType}, IsNull={IsNull}",
        resultTypeName, isNull);
    }

    // 2. Extract the requested TResult from the result
    if (!Internal.ResponseExtractor.TryExtractResponse<TResult>(fullResult, out var response)) {
      throw new InvalidOperationException(
        $"Could not extract {typeof(TResult).Name} from receptor result of type {fullResult?.GetType().Name ?? "null"}. " +
        $"The receptor for {messageType.Name} does not return a value of type {typeof(TResult).Name}.");
    }
    if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
      var resultTypeName = typeof(TResult).Name;
      CascadeLogger.LogDebug("[RPC] RpcExtraction: Successfully extracted {ResultType}", resultTypeName);
    }

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
  /// <docs>fundamentals/dispatcher/rpc-extraction</docs>
  /// <tests>Whizbang.Core.Tests/Dispatcher/DispatcherRpcExtractionTests.cs</tests>
  private async Task _cascadeEventsExcludingResponseAsync<TResult>(
    object? result,
    TResult? extractedResponse,
    Type? originalMessageType = null
  ) {
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
    if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
      var resultTypeName = result?.GetType().Name ?? "null";
      var extractedTypeName = typeof(TResult).Name;
      CascadeLogger.LogDebug("[RPC] CascadeExcludingResponse: ResultType={ResultType}, ExtractedType={ExtractedType}",
        resultTypeName, extractedTypeName);
    }

    // Fast path: Skip if result is null
    if (result == null) {
      CascadeLogger.LogDebug("[RPC] CascadeExcludingResponse: Result is null, skipping cascade");
      return;
    }

    // Look up receptor default routing from [DefaultRouting] attribute on the receptor
    Dispatch.DispatchMode? receptorDefault = originalMessageType is not null
        ? GetReceptorDefaultRouting(originalMessageType)
        : null;
    if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
      CascadeLogger.LogDebug("[RPC] CascadeExcludingResponse: ReceptorDefaultRouting={ReceptorDefault}", receptorDefault);
    }

    // Use MessageExtractor to find all IMessage instances with routing info
    var extractedCount = 0;
    var skippedCount = 0;
    foreach (var (msg, mode) in Internal.MessageExtractor.ExtractMessagesWithRouting(result, receptorDefault)) {
      // Skip the extracted response - it goes to RPC caller, not cascade
      // Use !Equals(default) instead of != null to handle value types correctly
      if (!EqualityComparer<TResult>.Default.Equals(extractedResponse, default) && ReferenceEquals(msg, extractedResponse)) {
        skippedCount++;
        CascadeLogger.LogDebug("[RPC] CascadeExcludingResponse: Skipping extracted response (ReferenceEquals match)");
        continue;
      }

      extractedCount++;
      var msgType = msg.GetType();
      if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
        var msgTypeName = msgType.Name;
        CascadeLogger.LogDebug("[RPC] CascadeExcludingResponse: Cascading message {Count}: Type={MessageType}, Mode={Mode}",
          extractedCount, msgTypeName, mode);
      }

      // Local dispatch: Invoke in-process receptors
      if (mode.HasFlag(Dispatch.DispatchMode.Local)) {
        if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
          var msgTypeName = msgType.Name;
          CascadeLogger.LogDebug("[RPC] CascadeExcludingResponse: Dispatching locally for {MessageType}", msgTypeName);
        }
        var publisher = GetUntypedReceptorPublisher(msgType);
        if (publisher != null) {
          await publisher(msg, null, default);
        }
      }

      // Outbox dispatch: Write to outbox for cross-service delivery
      if (mode.HasFlag(Dispatch.DispatchMode.Outbox)) {
        if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
          var msgTypeName = msgType.Name;
          CascadeLogger.LogDebug("[RPC] CascadeExcludingResponse: Calling CascadeToOutboxAsync for {MessageType}", msgTypeName);
        }
        await CascadeToOutboxAsync(msg, msgType);
      }
    }

    if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
      CascadeLogger.LogDebug("[RPC] CascadeExcludingResponse: Cascaded {CascadeCount} messages, skipped {SkipCount} (RPC response)",
        extractedCount, skippedCount);
    }
#pragma warning restore CA1848
  }

  /// <summary>
  /// Void path with tracing support for non-void receptors.
  /// When void LocalInvokeAsync is called but a non-void receptor is found,
  /// invoke it with full envelope/tracing context and cascade any events from the result.
  /// </summary>
  private async ValueTask _localInvokeVoidWithAnyInvokerAndTracingAsync(
    Func<object, ValueTask<object?>> invoker,
    object message,
    Type messageType,
    IMessageContext context,
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

      // Start dispatch activity to serve as parent for handler traces
      // Handler traces created via ITracer.BeginHandlerTrace will link to this activity
      using var dispatchActivity = WhizbangActivitySource.Execution.StartActivity($"Dispatch {messageType.Name}");
      dispatchActivity?.SetTag("whizbang.message.type", messageType.FullName);
      dispatchActivity?.SetTag("whizbang.message.id", envelope.MessageId.ToString());
      dispatchActivity?.SetTag("whizbang.correlation.id", envelope.GetCorrelationId()?.ToString());

#pragma warning disable CA1848 // Diagnostic logging - performance not critical
      if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
        var msgTypeName = messageType.Name;
        CascadeLogger.LogDebug("[CASCADE] VoidWithAnyInvoker: Invoking receptor for {MessageType}", msgTypeName);
      }
      var result = await invoker(message);
      if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
        var resultTypeName = result?.GetType().Name ?? "null";
        var isNull = result == null;
        CascadeLogger.LogDebug("[CASCADE] VoidWithAnyInvoker: Receptor returned {ResultType}, IsNull={IsNull}", resultTypeName, isNull);
      }
      if (result != null) {
        await _cascadeEventsFromResultAsync(result, messageType, sourceEnvelope: envelope);
      } else {
        CascadeLogger.LogWarning("[CASCADE] VoidWithAnyInvoker: Receptor returned null, no cascade will occur");
      }

      // Process tags after successful receptor completion
      await _processTagsIfEnabledAsync(message, messageType);

      // Invoke PostLifecycle receptors (local events don't go through perspectives)
      await _invokePostLifecycleReceptorsAsync(envelope, message, messageType);

      // Invoke ImmediateAsync lifecycle receptors after business receptor completes
      await _invokeImmediateAsyncReceptorsAsync(envelope, messageType);
#pragma warning restore CA1848
    } finally {
      // Unregister envelope after receptor completes (or throws)
      _envelopeRegistry?.Unregister(envelope);
    }
  }

  /// <summary>
  /// LocalInvoke with envelope creation and tracing support.
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
    // Note: Sync check already done in _localInvokeWithCastFallbackAsync for non-options callers
    var envelope = _createEnvelope(message, context, callerMemberName, callerFilePath, callerLineNumber);

    // Register envelope so receptor can look it up via IEventStore.AppendAsync(message)
    _envelopeRegistry?.Register(envelope);
    try {
      if (_traceStore != null) {
        await _traceStore.StoreAsync(envelope);
      }

      // Start dispatch activity to serve as parent for handler traces
      // Handler traces created via ITracer.BeginHandlerTrace will link to this activity
      using var dispatchActivity = WhizbangActivitySource.Execution.StartActivity($"Dispatch {messageType.Name}");
      dispatchActivity?.SetTag("whizbang.message.type", messageType.FullName);
      dispatchActivity?.SetTag("whizbang.message.id", envelope.MessageId.ToString());
      dispatchActivity?.SetTag("whizbang.correlation.id", envelope.GetCorrelationId()?.ToString());

      // Invoke using delegate - zero reflection, strongly typed
      var result = await invoker(message);

      // Auto-cascade: Extract and publish any IEvent instances from receptor return value
      // Supports tuples like (Result, Event), arrays like IEvent[], and nested structures
      await _cascadeEventsFromResultAsync(result, messageType, sourceEnvelope: envelope);

      // Process tags after successful receptor completion
      await _processTagsIfEnabledAsync(message, messageType);

      // Invoke PostLifecycle receptors (local events don't go through perspectives)
      await _invokePostLifecycleReceptorsAsync(envelope, message, messageType);

      // Invoke ImmediateAsync lifecycle receptors after business receptor completes
      await _invokeImmediateAsyncReceptorsAsync(envelope, messageType);

      // Unwrap Routed<T> from result if receptor returned a wrapped value
      // This enables receptors to return Route.Local(event) for cascade control
      // while callers still receive the unwrapped event type
      if (result is IRouted routedResult && routedResult.Value is TResult unwrappedResult) {
        return unwrappedResult;
      }

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

    // Set message context accessor so receptors can inject IMessageContext
    // This enables cascaded receptors to access UserId, TenantId, MessageId, etc.
    MessageContextAccessor.CurrentContext = context;

    // Unwrap Routed<T> if needed - the generic TMessage may be Routed<T>
    // We need to use runtime type to get the actual inner message type
    object actualMessage = message;
    if (message is IRouted routed) {
      if (routed.Mode == DispatchMode.None || routed.Value == null) {
        throw new ArgumentException("Cannot invoke a RoutedNone (Route.None()) - it has no inner message to dispatch.", nameof(message));
      }
      actualMessage = routed.Value;
    }

    var messageType = actualMessage.GetType();

    // Get strongly-typed delegate from generated code
    var invoker = GetReceptorInvoker<TResult>(actualMessage, messageType) ?? throw new ReceptorNotFoundException(messageType);

    return _localInvokeWithTracingAsyncInternalAsync<TMessage, TResult>(message, actualMessage, messageType, context, invoker, callerMemberName, callerFilePath, callerLineNumber);
  }

  /// <summary>
  /// Internal generic tracing method for LocalInvoke when tracing is enabled.
  /// Uses async/await to store envelope before invoking receptor.
  /// Preserves type information to create correctly-typed MessageEnvelope.
  /// </summary>
  /// <param name="message">Original message for envelope creation (may be Routed&lt;T&gt;)</param>
  /// <param name="actualMessage">Unwrapped message for invoker (always the inner message type)</param>
  /// <param name="messageType">Type of actualMessage for cascading</param>
  private async ValueTask<TResult> _localInvokeWithTracingAsyncInternalAsync<TMessage, TResult>(
    TMessage message,
    object actualMessage,
    Type messageType,
    IMessageContext context,
    ReceptorInvoker<TResult> invoker,
    string callerMemberName,
    string callerFilePath,
    int callerLineNumber
  ) {
    var sw = Stopwatch.StartNew();
    try {
      // Await perspective sync if receptor has [AwaitPerspectiveSync] attributes
      await _awaitPerspectiveSyncIfNeededAsync(actualMessage, messageType);

      var envelope = _createEnvelope<TMessage>(message, context, callerMemberName, callerFilePath, callerLineNumber);

      // Register envelope so receptor can look it up via IEventStore.AppendAsync(message)
      _envelopeRegistry?.Register(envelope);
      try {
        if (_traceStore != null) {
          await _traceStore.StoreAsync(envelope);
        }

        // Start dispatch activity to serve as parent for handler traces
        // Handler traces created via ITracer.BeginHandlerTrace will link to this activity
        var parentActivity = Activity.Current;
        using var dispatchActivity = WhizbangActivitySource.Execution.StartActivity($"Dispatch {messageType.Name}", ActivityKind.Internal);
        if (dispatchActivity != null) {
          dispatchActivity.SetTag("whizbang.message.type", messageType.FullName);
          dispatchActivity.SetTag("whizbang.message.id", envelope.MessageId.ToString());
          dispatchActivity.SetTag("whizbang.correlation.id", envelope.GetCorrelationId()?.ToString());
          dispatchActivity.SetTag("whizbang.debug.parent.id", parentActivity?.Id ?? "none");
          dispatchActivity.SetTag("whizbang.debug.parent.source", parentActivity?.Source?.Name ?? "none");
        }

        // Invoke using delegate with unwrapped message - zero reflection, strongly typed
        var result = await invoker(actualMessage);

        // Auto-cascade: Extract and publish any IEvent instances from receptor return value
        // Supports tuples like (Result, Event), arrays like IEvent[], and nested structures
        await _cascadeEventsFromResultAsync(result, messageType);

        // Process tags after successful receptor completion
        await _processTagsIfEnabledAsync(actualMessage, messageType);

        // Invoke PostLifecycle receptors (local events don't go through perspectives)
        await _invokePostLifecycleReceptorsAsync(envelope, actualMessage, messageType);

        // Invoke ImmediateAsync lifecycle receptors after business receptor completes
        await _invokeImmediateAsyncReceptorsAsync(envelope, messageType);

        _dispatcherMetrics?.MessagesDispatched.Add(1,
          new KeyValuePair<string, object?>("message_type", messageType.Name),
          new KeyValuePair<string, object?>("pattern", "local_invoke"));

        // Unwrap Routed<T> from result if receptor returned a wrapped value
        // This enables receptors to return Route.Local(event) for cascade control
        // while callers still receive the unwrapped event type
        if (result is IRouted routedResult && routedResult.Value is TResult unwrappedResult) {
          return unwrappedResult;
        }

        return result;
      } finally {
        // Unregister envelope after receptor completes (or throws)
        _envelopeRegistry?.Unregister(envelope);
      }
    } catch (Exception ex) {
      _dispatcherMetrics?.Errors.Add(1,
        new KeyValuePair<string, object?>("message_type", messageType.Name),
        new KeyValuePair<string, object?>("error_type", ex.GetType().Name));
      throw;
    } finally {
      sw.Stop();
      _dispatcherMetrics?.LocalInvokeDuration.Record(sw.Elapsed.TotalMilliseconds);
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
    var cascade = _cascadeContextFactory.NewRoot();
    var context = MessageContext.Create(cascade);
    return LocalInvokeAsync((object)message, context);
  }

  /// <summary>
  /// Invokes a void receptor in-process without returning a business result.
  /// Creates a new message context automatically using CascadeContextFactory for proper security propagation.
  /// PERFORMANCE: Zero allocation target for command/event patterns.
  /// For AOT compatibility, use the generic overload LocalInvokeAsync&lt;TMessage&gt;.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public ValueTask LocalInvokeAsync(object message) {
    var cascade = _cascadeContextFactory.NewRoot();
    var context = MessageContext.Create(cascade);
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
  /// Always creates envelope for full tracing and cascade context.
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

    // Set message context accessor so receptors can inject IMessageContext
    // This enables cascaded receptors to access UserId, TenantId, MessageId, etc.
    MessageContextAccessor.CurrentContext = context;

    // Unwrap Routed<T> if needed
    if (message is IRouted routed) {
      if (routed.Mode == DispatchMode.None || routed.Value == null) {
        throw new ArgumentException("Cannot invoke a RoutedNone (Route.None()) - it has no inner message to dispatch.", nameof(message));
      }
      message = routed.Value;
    }

    var messageType = message.GetType();

    // Check if receptor has [AwaitPerspectiveSync] attributes - requires async path
    var hasSyncAttributes = _receptorRegistry?.GetReceptorsFor(messageType, LifecycleStage.LocalImmediateInline)
      .Any(r => r.SyncAttributes is { Count: > 0 }) ?? false;

    // Check if there are ImmediateAsync or PostLifecycle receptors — if so, must go through async path
    var hasImmediateAsync = _hasImmediateAsyncReceptors(messageType);
    var hasPostLifecycle = _hasPostLifecycleReceptors(messageType);

    // Try async receptor first (async takes precedence)
    var asyncInvoker = GetVoidReceptorInvoker(message, messageType);
    if (asyncInvoker != null) {
      // If sync attributes, tracing, ImmediateAsync, or PostLifecycle exist, go through async path
      if (_traceStore != null || hasSyncAttributes || hasImmediateAsync || hasPostLifecycle) {
        return _localInvokeVoidWithSyncAndTracingAsync(message, messageType, context, asyncInvoker, callerMemberName, callerFilePath, callerLineNumber);
      }

      // FAST PATH: Zero allocation when no tracing, sync attributes, ImmediateAsync, or PostLifecycle
      // Invoke using delegate - zero reflection, strongly typed
      // Avoid async/await state machine allocation by returning task directly
      return asyncInvoker(message);
    }

    // Fallback to void sync receptor
    var syncInvoker = GetVoidSyncReceptorInvoker(message, messageType);
    if (syncInvoker != null) {
      // If sync attributes, ImmediateAsync, or PostLifecycle exist, must go through async path
      if (hasSyncAttributes || hasImmediateAsync || hasPostLifecycle) {
        return _localInvokeVoidSyncWithSyncCheckAsync(syncInvoker, message, messageType);
      }
      // Invoke synchronously - returns pre-completed ValueTask
      syncInvoker(message);
      return ValueTask.CompletedTask;
    }

    // Fallback to any receptor (void or non-void) for cascade support
    // This enables void LocalInvokeAsync to cascade events from non-void receptors
    var anyInvoker = GetReceptorInvokerAny(message, messageType);
    if (anyInvoker != null) {
      return _localInvokeVoidWithAnyInvokerAndTracingAsync(anyInvoker, message, messageType, context, callerMemberName, callerFilePath, callerLineNumber);
    }

    throw new ReceptorNotFoundException(messageType);
  }

  /// <summary>
  /// Async path for void LocalInvoke with sync check and optional tracing.
  /// Called when receptor has [AwaitPerspectiveSync] attributes or tracing is enabled.
  /// </summary>
  private async ValueTask _localInvokeVoidWithSyncAndTracingAsync(
    object message,
    Type messageType,
    IMessageContext context,
    VoidReceptorInvoker invoker,
    string callerMemberName,
    string callerFilePath,
    int callerLineNumber
  ) {
    var sw = Stopwatch.StartNew();
    try {
      // Await perspective sync if receptor has [AwaitPerspectiveSync] attributes
      await _awaitPerspectiveSyncIfNeededAsync(message, messageType);

      var envelope = _createEnvelope(message, context, callerMemberName, callerFilePath, callerLineNumber);

      // Register envelope so receptor can look it up via IEventStore.AppendAsync(message)
      _envelopeRegistry?.Register(envelope);
      try {
        if (_traceStore != null) {
          await _traceStore.StoreAsync(envelope);
        }

        // Start dispatch activity to serve as parent for handler traces
        // Handler traces created via ITracer.BeginHandlerTrace will link to this activity
        using var dispatchActivity = WhizbangActivitySource.Execution.StartActivity($"Dispatch {messageType.Name}");
        dispatchActivity?.SetTag("whizbang.message.type", messageType.FullName);
        dispatchActivity?.SetTag("whizbang.message.id", envelope.MessageId.ToString());
        dispatchActivity?.SetTag("whizbang.correlation.id", envelope.GetCorrelationId()?.ToString());

        // Invoke using delegate - zero reflection, strongly typed
        await invoker(message);

        // Invoke PostLifecycle receptors (local events don't go through perspectives)
        await _invokePostLifecycleReceptorsAsync(envelope, message, messageType);

        // Invoke ImmediateAsync lifecycle receptors after business receptor completes
        await _invokeImmediateAsyncReceptorsAsync(envelope, messageType);
      } finally {
        // Unregister envelope after receptor completes (or throws)
        _envelopeRegistry?.Unregister(envelope);
      }

      _dispatcherMetrics?.MessagesDispatched.Add(1,
        new KeyValuePair<string, object?>("message_type", messageType.Name),
        new KeyValuePair<string, object?>("pattern", "local_invoke"));
    } catch (Exception ex) {
      _dispatcherMetrics?.Errors.Add(1,
        new KeyValuePair<string, object?>("message_type", messageType.Name),
        new KeyValuePair<string, object?>("error_type", ex.GetType().Name));
      throw;
    } finally {
      sw.Stop();
      _dispatcherMetrics?.LocalInvokeDuration.Record(sw.Elapsed.TotalMilliseconds);
    }
  }

  /// <summary>
  /// Async path for void sync LocalInvoke with sync check or ImmediateAsync.
  /// Called when sync receptor has [AwaitPerspectiveSync] attributes or ImmediateAsync receptors.
  /// </summary>
  private async ValueTask _localInvokeVoidSyncWithSyncCheckAsync(
    VoidSyncReceptorInvoker invoker,
    object message,
    Type messageType
  ) {
    // Await perspective sync if receptor has [AwaitPerspectiveSync] attributes
    await _awaitPerspectiveSyncIfNeededAsync(message, messageType);

    // Start dispatch activity to serve as parent for handler traces
    // Handler traces created via ITracer.BeginHandlerTrace will link to this activity
    using var dispatchActivity = WhizbangActivitySource.Execution.StartActivity($"Dispatch {messageType.Name}");
    dispatchActivity?.SetTag("whizbang.message.type", messageType.FullName);

    // Invoke synchronously
    invoker(message);

    // Invoke PostLifecycle and ImmediateAsync lifecycle receptors after business receptor completes
    // Create a minimal envelope for lifecycle invocation
    if (_hasPostLifecycleReceptors(messageType) || _hasImmediateAsyncReceptors(messageType)) {
      var envelope = _createEnvelope(message, MessageContext.Create(_cascadeContextFactory.NewRoot()), "", "", 0);
      await _invokePostLifecycleReceptorsAsync(envelope, message, messageType);
      await _invokeImmediateAsyncReceptorsAsync(envelope, messageType);
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

    // Set message context accessor so receptors can inject IMessageContext
    // This enables cascaded receptors to access UserId, TenantId, MessageId, etc.
    MessageContextAccessor.CurrentContext = context;

    // Unwrap Routed<T> if needed - the generic TMessage may be Routed<T>
    // We need to use runtime type to get the actual inner message type
    object actualMessage = message;
    if (message is IRouted routed) {
      if (routed.Mode == DispatchMode.None || routed.Value == null) {
        throw new ArgumentException("Cannot invoke a RoutedNone (Route.None()) - it has no inner message to dispatch.", nameof(message));
      }
      actualMessage = routed.Value;
    }

    var messageType = actualMessage.GetType();

    // Check if receptor has [AwaitPerspectiveSync] attributes - requires async path
    var hasSyncAttributes = _receptorRegistry?.GetReceptorsFor(messageType, LifecycleStage.LocalImmediateInline)
      .Any(r => r.SyncAttributes is { Count: > 0 }) ?? false;

    // Try async receptor first (async takes precedence)
    var asyncInvoker = GetVoidReceptorInvoker(actualMessage, messageType);
    if (asyncInvoker != null) {
      return _localInvokeVoidWithTracingAsyncInternalAsync<TMessage>(message, actualMessage, messageType, context, asyncInvoker, callerMemberName, callerFilePath, callerLineNumber);
    }

    // Check if there are ImmediateAsync or PostLifecycle receptors — if so, must go through async path
    var hasImmediateAsync = _hasImmediateAsyncReceptors(messageType);
    var hasPostLifecycle = _hasPostLifecycleReceptors(messageType);

    // Fallback to void sync receptor
    var syncInvoker = GetVoidSyncReceptorInvoker(actualMessage, messageType);
    if (syncInvoker != null) {
      // If sync attributes, ImmediateAsync, or PostLifecycle exist, must go through async path
      if (hasSyncAttributes || hasImmediateAsync || hasPostLifecycle) {
        return _localInvokeVoidSyncWithSyncCheckAsync(syncInvoker, actualMessage, messageType);
      }
      // Invoke synchronously - returns pre-completed ValueTask
      syncInvoker(actualMessage);
      return ValueTask.CompletedTask;
    }

    // Fallback to any receptor (void or non-void) for cascade support
    // This enables void LocalInvokeAsync to cascade events from non-void receptors
    var anyInvoker = GetReceptorInvokerAny(actualMessage, messageType);
    if (anyInvoker != null) {
      return _localInvokeVoidWithAnyInvokerAndTracingAsync(anyInvoker, actualMessage, messageType, context, callerMemberName, callerFilePath, callerLineNumber);
    }

    throw new ReceptorNotFoundException(messageType);
  }

  /// <summary>
  /// Internal generic tracing method for void LocalInvoke when tracing is enabled.
  /// Uses async/await to store envelope before invoking receptor.
  /// Preserves type information to create correctly-typed MessageEnvelope.
  /// </summary>
  /// <param name="message">Original message for envelope creation (may be Routed&lt;T&gt;)</param>
  /// <param name="actualMessage">Unwrapped message for invoker (always the inner message type)</param>
  private async ValueTask _localInvokeVoidWithTracingAsyncInternalAsync<TMessage>(
    TMessage message,
    object actualMessage,
    Type messageType,
    IMessageContext context,
    VoidReceptorInvoker invoker,
    string callerMemberName,
    string callerFilePath,
    int callerLineNumber
  ) {
    var sw = Stopwatch.StartNew();
    try {
      // Await perspective sync if receptor has [AwaitPerspectiveSync] attributes
      await _awaitPerspectiveSyncIfNeededAsync(actualMessage, messageType);

      var envelope = _createEnvelope<TMessage>(message, context, callerMemberName, callerFilePath, callerLineNumber);

      // Register envelope so receptor can look it up via IEventStore.AppendAsync(message)
      _envelopeRegistry?.Register(envelope);
      try {
        if (_traceStore != null) {
          await _traceStore.StoreAsync(envelope);
        }

        // Start dispatch activity to serve as parent for handler traces
        // Handler traces created via ITracer.BeginHandlerTrace will link to this activity
        using var dispatchActivity = WhizbangActivitySource.Execution.StartActivity($"Dispatch {messageType.Name}");
        dispatchActivity?.SetTag("whizbang.message.type", messageType.FullName);
        dispatchActivity?.SetTag("whizbang.message.id", envelope.MessageId.ToString());
        dispatchActivity?.SetTag("whizbang.correlation.id", envelope.GetCorrelationId()?.ToString());

        // Invoke using delegate with unwrapped message - zero reflection, strongly typed
        await invoker(actualMessage);

        // Invoke PostLifecycle receptors (local events don't go through perspectives)
        await _invokePostLifecycleReceptorsAsync(envelope, actualMessage, messageType);

        // Invoke ImmediateAsync lifecycle receptors after business receptor completes
        await _invokeImmediateAsyncReceptorsAsync(envelope, messageType);
      } finally {
        // Unregister envelope after receptor completes (or throws)
        _envelopeRegistry?.Unregister(envelope);
      }

      _dispatcherMetrics?.MessagesDispatched.Add(1,
        new KeyValuePair<string, object?>("message_type", messageType.Name),
        new KeyValuePair<string, object?>("pattern", "local_invoke"));
    } catch (Exception ex) {
      _dispatcherMetrics?.Errors.Add(1,
        new KeyValuePair<string, object?>("message_type", messageType.Name),
        new KeyValuePair<string, object?>("error_type", ex.GetType().Name));
      throw;
    } finally {
      sw.Stop();
      _dispatcherMetrics?.LocalInvokeDuration.Record(sw.Elapsed.TotalMilliseconds);
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
    var cascade = _cascadeContextFactory.NewRoot();
    var context = MessageContext.Create(cascade);
    return _localInvokeWithOptionsAsync<TResult>(message, context, options);
  }

  /// <summary>
  /// Invokes a void receptor in-process with dispatch options.
  /// Uses CascadeContextFactory for proper security propagation.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public ValueTask LocalInvokeAsync(object message, DispatchOptions options) {
    options.CancellationToken.ThrowIfCancellationRequested();
    var cascade = _cascadeContextFactory.NewRoot();
    var context = MessageContext.Create(cascade);
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

    // Unwrap Routed<T> if needed
    if (message is IRouted routed) {
      if (routed.Mode == DispatchMode.None || routed.Value == null) {
        throw new ArgumentException("Cannot invoke a RoutedNone (Route.None()) - it has no inner message to dispatch.", nameof(message));
      }
      message = routed.Value;
    }

    var messageType = message.GetType();
    TResult result;

    var asyncInvoker = GetReceptorInvoker<TResult>(message, messageType);

    if (asyncInvoker != null) {
      result = await _localInvokeWithTracingAndOptionsAsync(message, messageType, context, asyncInvoker, options, callerMemberName, callerFilePath, callerLineNumber);
    } else {
      var syncInvoker = GetSyncReceptorInvoker<TResult>(message, messageType);
      if (syncInvoker != null) {
        // Wrap sync invoker as async and route through tracing path
        ReceptorInvoker<TResult> wrappedInvoker = (msg) => new ValueTask<TResult>(syncInvoker(msg));
        result = await _localInvokeWithTracingAndOptionsAsync(message, messageType, context, wrappedInvoker, options, callerMemberName, callerFilePath, callerLineNumber);
      } else {
        throw new ReceptorNotFoundException(messageType);
      }
    }

    // Wait for all perspectives to process cascaded events if requested
    await _waitForPerspectivesIfNeededAsync(options);

    return result;
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

    // Unwrap Routed<T> if needed
    if (message is IRouted routed) {
      if (routed.Mode == DispatchMode.None || routed.Value == null) {
        throw new ArgumentException("Cannot invoke a RoutedNone (Route.None()) - it has no inner message to dispatch.", nameof(message));
      }
      message = routed.Value;
    }

    var messageType = message.GetType();
    var asyncInvoker = GetVoidReceptorInvoker(message, messageType);

    if (asyncInvoker != null) {
      if (_traceStore != null) {
        await _localInvokeVoidWithTracingAndOptionsAsync(message, messageType, context, asyncInvoker, options, callerMemberName, callerFilePath, callerLineNumber);
      } else {
        options.CancellationToken.ThrowIfCancellationRequested();
        // Await perspective sync if receptor has [AwaitPerspectiveSync] attributes
        await _awaitPerspectiveSyncIfNeededAsync(message, messageType, options.CancellationToken);
        await asyncInvoker(message);
      }
    } else {
      var syncInvoker = GetVoidSyncReceptorInvoker(message, messageType);
      if (syncInvoker != null) {
        options.CancellationToken.ThrowIfCancellationRequested();
        // Await perspective sync if receptor has [AwaitPerspectiveSync] attributes
        await _awaitPerspectiveSyncIfNeededAsync(message, messageType, options.CancellationToken);
        syncInvoker(message);
      } else {
        // Fallback: Try to find any receptor (including those that return values)
        // This allows void LocalInvokeAsync to call receptors that return events for cascading
        var anyInvoker = GetReceptorInvokerAny(message, messageType);
        if (anyInvoker != null) {
          options.CancellationToken.ThrowIfCancellationRequested();
          // Await perspective sync if receptor has [AwaitPerspectiveSync] attributes
          await _awaitPerspectiveSyncIfNeededAsync(message, messageType, options.CancellationToken);
          await _localInvokeVoidWithAnyInvokerAndTracingAsync(anyInvoker, message, messageType, context, callerMemberName, callerFilePath, callerLineNumber);
        } else {
          throw new ReceptorNotFoundException(messageType);
        }
      }
    }

    // Wait for all perspectives to process cascaded events if requested
    await _waitForPerspectivesIfNeededAsync(options);
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
    // Await perspective sync if receptor has [AwaitPerspectiveSync] attributes
    await _awaitPerspectiveSyncIfNeededAsync(message, messageType, options.CancellationToken);

    var envelope = _createEnvelope(message, context, callerMemberName, callerFilePath, callerLineNumber);
    _envelopeRegistry?.Register(envelope);
    try {
      if (_traceStore != null) {
        await _traceStore.StoreAsync(envelope, options.CancellationToken);
      }

      // Start dispatch activity to serve as parent for handler traces
      // Handler traces created via ITracer.BeginHandlerTrace will link to this activity
      using var dispatchActivity = WhizbangActivitySource.Execution.StartActivity($"Dispatch {messageType.Name}");
      dispatchActivity?.SetTag("whizbang.message.type", messageType.FullName);
      dispatchActivity?.SetTag("whizbang.message.id", envelope.MessageId.ToString());
      dispatchActivity?.SetTag("whizbang.correlation.id", envelope.GetCorrelationId()?.ToString());

      options.CancellationToken.ThrowIfCancellationRequested();
      var result = await invoker(message);
      await _cascadeEventsFromResultAsync(result, messageType, sourceEnvelope: envelope);

      // Process tags after successful receptor completion
      await _processTagsIfEnabledAsync(message, messageType);

      // Invoke PostLifecycle receptors (local events don't go through perspectives)
      await _invokePostLifecycleReceptorsAsync(envelope, message, messageType);

      // Invoke ImmediateAsync lifecycle receptors after business receptor completes
      await _invokeImmediateAsyncReceptorsAsync(envelope, messageType);

      // Unwrap Routed<T> from result if receptor returned a wrapped value
      // This enables receptors to return Route.Local(event) for cascade control
      // while callers still receive the unwrapped event type
      if (result is IRouted routedResult && routedResult.Value is TResult unwrappedResult) {
        return unwrappedResult;
      }

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
    Type messageType,
    IMessageContext context,
    VoidReceptorInvoker invoker,
    DispatchOptions options,
    string callerMemberName,
    string callerFilePath,
    int callerLineNumber
  ) {
    // Await perspective sync if receptor has [AwaitPerspectiveSync] attributes
    await _awaitPerspectiveSyncIfNeededAsync(message, messageType, options.CancellationToken);

    var envelope = _createEnvelope(message, context, callerMemberName, callerFilePath, callerLineNumber);
    _envelopeRegistry?.Register(envelope);
    try {
      if (_traceStore != null) {
        await _traceStore.StoreAsync(envelope, options.CancellationToken);
      }

      // Start dispatch activity to serve as parent for handler traces
      // Handler traces created via ITracer.BeginHandlerTrace will link to this activity
      using var dispatchActivity = WhizbangActivitySource.Execution.StartActivity($"Dispatch {messageType.Name}");
      dispatchActivity?.SetTag("whizbang.message.type", messageType.FullName);
      dispatchActivity?.SetTag("whizbang.message.id", envelope.MessageId.ToString());
      dispatchActivity?.SetTag("whizbang.correlation.id", envelope.GetCorrelationId()?.ToString());

      options.CancellationToken.ThrowIfCancellationRequested();
      await invoker(message);

      // Invoke PostLifecycle receptors (local events don't go through perspectives)
      await _invokePostLifecycleReceptorsAsync(envelope, message, messageType);

      // Invoke ImmediateAsync lifecycle receptors after business receptor completes
      await _invokeImmediateAsyncReceptorsAsync(envelope, messageType);
    } finally {
      _envelopeRegistry?.Unregister(envelope);
    }
  }

  // ========================================
  // LOCAL INVOKE WITH RECEIPT — In-Process RPC with Dispatch Metadata
  // ========================================

  /// <summary>
  /// Invokes a receptor in-process with typed message and returns both the typed business result
  /// AND a delivery receipt with dispatch metadata (AOT-compatible).
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(TMessage message) where TMessage : notnull {
    var cascade = _cascadeContextFactory.NewRoot();
    var context = MessageContext.Create(cascade);
    return LocalInvokeWithReceiptAsync<TResult>((object)message, context);
  }

  /// <summary>
  /// Invokes a receptor in-process and returns both the typed business result AND a delivery receipt.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message) {
    var cascade = _cascadeContextFactory.NewRoot();
    var context = MessageContext.Create(cascade);
    return LocalInvokeWithReceiptAsync<TResult>(message, context);
  }

  /// <summary>
  /// Invokes a receptor in-process with typed message and explicit context, returning both
  /// the typed business result AND a delivery receipt (AOT-compatible).
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TMessage, TResult>(
    TMessage message,
    IMessageContext context,
    [CallerMemberName] string callerMemberName = "",
    [CallerFilePath] string callerFilePath = "",
    [CallerLineNumber] int callerLineNumber = 0
  ) where TMessage : notnull {
    return LocalInvokeWithReceiptAsync<TResult>((object)message, context, callerMemberName, callerFilePath, callerLineNumber);
  }

  /// <summary>
  /// Invokes a receptor in-process with explicit context and returns both the typed business result
  /// AND a delivery receipt.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(
    object message,
    IMessageContext context,
    [CallerMemberName] string callerMemberName = "",
    [CallerFilePath] string callerFilePath = "",
    [CallerLineNumber] int callerLineNumber = 0
  ) {
    ArgumentNullException.ThrowIfNull(message);
    ArgumentNullException.ThrowIfNull(context);

    // Set message context accessor so receptors can inject IMessageContext
    MessageContextAccessor.CurrentContext = context;

    // Unwrap Routed<T> if needed
    if (message is IRouted routed) {
      if (routed.Mode == DispatchMode.None || routed.Value == null) {
        throw new ArgumentException("Cannot invoke a RoutedNone (Route.None()) - it has no inner message to dispatch.", nameof(message));
      }
      message = routed.Value;
    }

    var messageType = message.GetType();

    // Try async receptor first
    var asyncInvoker = GetReceptorInvoker<TResult>(message, messageType);
    if (asyncInvoker != null) {
      return _localInvokeWithTracingAndReceiptAsync(message, messageType, context, asyncInvoker, callerMemberName, callerFilePath, callerLineNumber);
    }

    // Fallback to sync receptor
    var syncInvoker = GetSyncReceptorInvoker<TResult>(message, messageType);
    if (syncInvoker != null) {
      ReceptorInvoker<TResult> wrappedInvoker = (msg) => new ValueTask<TResult>(syncInvoker(msg));
      return _localInvokeWithTracingAndReceiptAsync(message, messageType, context, wrappedInvoker, callerMemberName, callerFilePath, callerLineNumber);
    }

    throw new ReceptorNotFoundException(messageType);
  }

  /// <summary>
  /// Invokes a receptor in-process with dispatch options and returns both the typed business result
  /// AND a delivery receipt.
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public async ValueTask<InvokeResult<TResult>> LocalInvokeWithReceiptAsync<TResult>(object message, DispatchOptions options) {
    options.CancellationToken.ThrowIfCancellationRequested();
    var cascade = _cascadeContextFactory.NewRoot();
    var context = MessageContext.Create(cascade);

    ArgumentNullException.ThrowIfNull(message);

    // Unwrap Routed<T> if needed
    if (message is IRouted routed) {
      if (routed.Mode == DispatchMode.None || routed.Value == null) {
        throw new ArgumentException("Cannot invoke a RoutedNone (Route.None()) - it has no inner message to dispatch.", nameof(message));
      }
      message = routed.Value;
    }

    var messageType = message.GetType();

    var asyncInvoker = GetReceptorInvoker<TResult>(message, messageType);
    if (asyncInvoker != null) {
      var invokeResult = await _localInvokeWithTracingAndReceiptAndOptionsAsync(message, messageType, context, asyncInvoker, options);
      await _waitForPerspectivesIfNeededAsync(options);
      return invokeResult;
    }

    var syncInvoker = GetSyncReceptorInvoker<TResult>(message, messageType);
    if (syncInvoker != null) {
      ReceptorInvoker<TResult> wrappedInvoker = (msg) => new ValueTask<TResult>(syncInvoker(msg));
      var invokeResult = await _localInvokeWithTracingAndReceiptAndOptionsAsync(message, messageType, context, wrappedInvoker, options);
      await _waitForPerspectivesIfNeededAsync(options);
      return invokeResult;
    }

    throw new ReceptorNotFoundException(messageType);
  }

  /// <summary>
  /// Internal tracing method for LocalInvokeWithReceipt. Always creates envelope and builds receipt.
  /// </summary>
  private async ValueTask<InvokeResult<TResult>> _localInvokeWithTracingAndReceiptAsync<TResult>(
    object message,
    Type messageType,
    IMessageContext context,
    ReceptorInvoker<TResult> invoker,
    string callerMemberName,
    string callerFilePath,
    int callerLineNumber
  ) {
    // Await perspective sync if receptor has [AwaitPerspectiveSync] attributes
    await _awaitPerspectiveSyncIfNeededAsync(message, messageType);

    var envelope = _createEnvelope(message, context, callerMemberName, callerFilePath, callerLineNumber);
    _envelopeRegistry?.Register(envelope);
    try {
      if (_traceStore != null) {
        await _traceStore.StoreAsync(envelope);
      }

      using var dispatchActivity = WhizbangActivitySource.Execution.StartActivity($"Dispatch {messageType.Name}");
      dispatchActivity?.SetTag("whizbang.message.type", messageType.FullName);
      dispatchActivity?.SetTag("whizbang.message.id", envelope.MessageId.ToString());
      dispatchActivity?.SetTag("whizbang.correlation.id", envelope.GetCorrelationId()?.ToString());

      var result = await invoker(message);

      await _cascadeEventsFromResultAsync(result, messageType, sourceEnvelope: envelope);
      await _processTagsIfEnabledAsync(message, messageType);

      // Invoke PostLifecycle receptors (local events don't go through perspectives)
      await _invokePostLifecycleReceptorsAsync(envelope, message, messageType);

      // Invoke ImmediateAsync lifecycle receptors after business receptor completes
      await _invokeImmediateAsyncReceptorsAsync(envelope, messageType);

      // Unwrap Routed<T> from result if receptor returned a wrapped value
      TResult unwrapped;
      if (result is IRouted routedResult && routedResult.Value is TResult unwrappedResult) {
        unwrapped = unwrappedResult;
      } else {
        unwrapped = result;
      }

      // Build delivery receipt from envelope metadata
      var streamId = _streamIdExtractor?.ExtractStreamId(message, messageType);
      var destination = messageType.Name;
      var receipt = DeliveryReceipt.Delivered(
        envelope.MessageId,
        destination,
        context.CorrelationId,
        context.CausationId,
        streamId
      );

      return new InvokeResult<TResult>(unwrapped, receipt);
    } finally {
      _envelopeRegistry?.Unregister(envelope);
    }
  }

  /// <summary>
  /// Internal tracing method for LocalInvokeWithReceipt with DispatchOptions.
  /// </summary>
  private async ValueTask<InvokeResult<TResult>> _localInvokeWithTracingAndReceiptAndOptionsAsync<TResult>(
    object message,
    Type messageType,
    MessageContext context,
    ReceptorInvoker<TResult> invoker,
    DispatchOptions options
  ) {
    // Await perspective sync if receptor has [AwaitPerspectiveSync] attributes
    await _awaitPerspectiveSyncIfNeededAsync(message, messageType, options.CancellationToken);

    var envelope = _createEnvelope(message, context, "", "", 0);
    _envelopeRegistry?.Register(envelope);
    try {
      if (_traceStore != null) {
        await _traceStore.StoreAsync(envelope, options.CancellationToken);
      }

      using var dispatchActivity = WhizbangActivitySource.Execution.StartActivity($"Dispatch {messageType.Name}");
      dispatchActivity?.SetTag("whizbang.message.type", messageType.FullName);
      dispatchActivity?.SetTag("whizbang.message.id", envelope.MessageId.ToString());
      dispatchActivity?.SetTag("whizbang.correlation.id", envelope.GetCorrelationId()?.ToString());

      options.CancellationToken.ThrowIfCancellationRequested();
      var result = await invoker(message);

      await _cascadeEventsFromResultAsync(result, messageType, sourceEnvelope: envelope);
      await _processTagsIfEnabledAsync(message, messageType);

      // Invoke PostLifecycle receptors (local events don't go through perspectives)
      await _invokePostLifecycleReceptorsAsync(envelope, message, messageType);

      // Invoke ImmediateAsync lifecycle receptors after business receptor completes
      await _invokeImmediateAsyncReceptorsAsync(envelope, messageType);

      // Unwrap Routed<T> from result
      TResult unwrapped;
      if (result is IRouted routedResult && routedResult.Value is TResult unwrappedResult) {
        unwrapped = unwrappedResult;
      } else {
        unwrapped = result;
      }

      // Build delivery receipt from envelope metadata
      var streamId = _streamIdExtractor?.ExtractStreamId(message, messageType);
      var destination = messageType.Name;
      var receipt = DeliveryReceipt.Delivered(
        envelope.MessageId,
        destination,
        context.CorrelationId,
        context.CausationId,
        streamId
      );

      return new InvokeResult<TResult>(unwrapped, receipt);
    } finally {
      _envelopeRegistry?.Unregister(envelope);
    }
  }

  /// <summary>
  /// Gets ScopeDelta for hop propagation.
  /// Priority: IMessageContext (UserId/TenantId) first, then ambient AsyncLocal.
  /// This ensures context flows correctly even when AsyncLocal scope has ended.
  /// </summary>
  private static ScopeDelta? _getScopeDeltaForHop(IMessageContext context) {
    // Priority 1: IMessageContext (set via CascadeContextFactory or explicit)
    if (!string.IsNullOrEmpty(context.UserId) || !string.IsNullOrEmpty(context.TenantId)) {
      return ScopeDelta.FromSecurityContext(new SecurityContext {
        UserId = context.UserId,
        TenantId = context.TenantId
      });
    }

    // Priority 2: Ambient AsyncLocal scope
    return ScopeDelta.FromSecurityContext(CascadeContext.GetSecurityFromAmbient());
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
    var messageId = MessageId.New();

    // Auto-generate StreamId for messages with [GenerateStreamId] attribute during envelope creation
    // Must run BEFORE _createHopMetadata so the hop captures the generated StreamId
    _autoGenerateStreamIdIfNeeded(message!, typeof(TMessage));

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
      Scope = _getScopeDeltaForHop(context),
      TraceParent = System.Diagnostics.Activity.Current?.Id
    };

    // Populate SentAt-phase properties directly on the message record
    var populatedMessage = (TMessage)AutoPopulatePopulatorRegistry.PopulateSent(message!, hop, messageId);

    var envelope = new MessageEnvelope<TMessage> {
      MessageId = messageId,
      Payload = populatedMessage,
      Hops = []
    };

    envelope.AddHop(hop);

    // Also store in metadata for backwards compatibility
    _autoPopulateProcessor.ProcessAutoPopulate(envelope, typeof(TMessage));

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
    var messageId = MessageId.New();

    // Auto-generate StreamId for messages with [GenerateStreamId] attribute during envelope creation
    // Must run BEFORE _createHopMetadata so the hop captures the generated StreamId
    var messageType = message.GetType();
    _autoGenerateStreamIdIfNeeded(message, messageType);

    // Extract aggregate ID and add to hop metadata (for streamId extraction)
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
      Scope = _getScopeDeltaForHop(context),
      TraceParent = System.Diagnostics.Activity.Current?.Id
    };

    // Populate SentAt-phase properties directly on the message record
    var populatedMessage = AutoPopulatePopulatorRegistry.PopulateSent(message, hop, messageId);

    var envelope = new MessageEnvelope<object> {
      MessageId = messageId,
      Payload = populatedMessage,
      Hops = []
    };

    envelope.AddHop(hop);

    // Also store in metadata for backwards compatibility
    _autoPopulateProcessor.ProcessAutoPopulate(envelope, messageType);

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
  /// <docs>fundamentals/dispatcher/dispatcher#routed-message-cascading</docs>
  /// <tests>Whizbang.Core.Tests/Dispatcher/DispatcherCascadeTests.cs:LocalInvokeAsync_TupleWithEvent_AutoPublishesEventAsync</tests>
  /// <tests>Whizbang.Core.Tests/Dispatcher/DispatcherRoutedCascadeTests.cs:CascadeFromResult_WithRouteLocal_InvokesLocalReceptorAsync</tests>
  private async Task _cascadeEventsFromResultAsync<TResult>(TResult result, Type? originalMessageType = null, IMessageEnvelope? sourceEnvelope = null) {
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
    if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
      var resultTypeName = result?.GetType().Name ?? "null";
      var origMsgTypeName = originalMessageType?.Name ?? "null";
      CascadeLogger.LogDebug("[CASCADE] CascadeEventsFromResult: ResultType={ResultType}, OriginalMessageType={OriginalMessageType}",
        resultTypeName, origMsgTypeName);
    }

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
    if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
      CascadeLogger.LogDebug("[CASCADE] CascadeEventsFromResult: ReceptorDefaultRouting={ReceptorDefault}", receptorDefault);
    }

    // Use MessageExtractor to find all IMessage instances with routing info
    // This handles tuples, arrays, nested structures, Routed<T> wrappers, etc. using ITuple interface (AOT-safe)
    var extractedCount = 0;
    foreach (var (msg, mode) in Internal.MessageExtractor.ExtractMessagesWithRouting(result, receptorDefault)) {
      extractedCount++;
      var messageType = msg.GetType();
      if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
        var msgTypeName = messageType.Name;
        CascadeLogger.LogDebug("[CASCADE] CascadeEventsFromResult: Extracted message {Count}: Type={MessageType}, Mode={Mode}",
          extractedCount, msgTypeName, mode);
      }

      // Generate eventId for tracking and storage consistency
      // CRITICAL: This SAME eventId must be used for both tracking (singleton tracker)
      // AND storage (outbox/event store) so that MarkProcessed can find the tracked event
      Guid? eventId = null;
      if (msg is IEvent) {
        eventId = ValueObjects.TrackedGuid.NewMedo(); // Generate tracking ID for cascaded events (UUIDv7)
        var streamId = _streamIdExtractor?.ExtractStreamId(msg, messageType) ?? Guid.Empty;

        // Auto-generate StreamId based on [GenerateStreamId] attribute policy
        if (_streamIdExtractor is not null && msg is IHasStreamId hasStreamId) {
          var (shouldGenerate, onlyIfEmpty) = _streamIdExtractor.GetGenerationPolicy(msg);
          if (shouldGenerate && (!onlyIfEmpty || streamId == Guid.Empty)) {
            streamId = ValueObjects.TrackedGuid.NewMedo();
            hasStreamId.StreamId = streamId;
            if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
              CascadeLogger.LogDebug("[STREAM_ID] Auto-generated StreamId={StreamId} for EventType={EventType} (OnlyIfEmpty={OnlyIfEmpty})",
                streamId, messageType.Name, onlyIfEmpty);
            }
          }
        }

        // Cascade StreamId propagation: inherit from source command when event's StreamId is still empty
        if (streamId == Guid.Empty && sourceEnvelope is not null && _streamIdExtractor is not null) {
          var sourceStreamId = _streamIdExtractor.ExtractStreamId(sourceEnvelope.Payload!, sourceEnvelope.Payload!.GetType());
          if (sourceStreamId.HasValue && sourceStreamId.Value != Guid.Empty) {
            streamId = sourceStreamId.Value;
            if (msg is IHasStreamId hasStreamIdForCascade) {
              hasStreamIdForCascade.StreamId = streamId;
            } else {
              _streamIdExtractor.SetStreamId(msg, streamId);
            }
            Log.StreamIdPropagatedFromSource(CascadeLogger, streamId, sourceEnvelope.Payload!.GetType().Name, messageType.Name);
          }
        }

        // Track in scoped tracker (same request scope)
        // Use accessor to get current scope's tracker (works with singleton Dispatcher)
        var scopedTracker = _scopedEventTracker ?? ScopedEventTrackerAccessor.CurrentTracker;
        if (scopedTracker is not null) {
          scopedTracker.TrackEmittedEvent(streamId, messageType, eventId.Value);
          if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
            CascadeLogger.LogDebug("[SYNC_DEBUG] Tracked in SCOPED tracker: StreamId={StreamId}, EventType={EventType}, EventId={EventId}",
              streamId, messageType.Name, eventId.Value);
          }
        } else if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
          CascadeLogger.LogDebug("[SYNC_DEBUG] SCOPED tracker is NULL - event not tracked for same-scope sync: EventType={EventType}, EventId={EventId}",
            messageType.Name, eventId.Value);
        }

        // CRITICAL: Also track in singleton tracker for cross-scope sync
        // This enables Request 2 to wait for events emitted by Request 1
        if (_syncEventTracker is not null && _trackedEventTypeRegistry is not null) {
          var perspectiveNames = _trackedEventTypeRegistry.GetPerspectiveNames(messageType);
          if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
            CascadeLogger.LogDebug("[SYNC_DEBUG] SINGLETON tracker check: EventType={EventType}, PerspectiveCount={Count}, Perspectives=[{Perspectives}]",
              messageType.FullName, perspectiveNames.Count, string.Join(", ", perspectiveNames));
          }
          foreach (var perspectiveName in perspectiveNames) {
            _syncEventTracker.TrackEvent(messageType, eventId.Value, streamId, perspectiveName);
            if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
              CascadeLogger.LogDebug("[SYNC_DEBUG] Tracked in SINGLETON tracker: StreamId={StreamId}, EventType={EventType}, EventId={EventId}, Perspective={Perspective}",
                streamId, messageType.Name, eventId.Value, perspectiveName);
            }
          }
        } else if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
          CascadeLogger.LogDebug("[SYNC_DEBUG] SINGLETON tracker DISABLED - _syncEventTracker={HasTracker}, _trackedEventTypeRegistry={HasRegistry}",
            _syncEventTracker is not null, _trackedEventTypeRegistry is not null);
        }

        if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
          var eventTypeName = messageType.Name;
          CascadeLogger.LogDebug("[CASCADE] CascadeEventsFromResult: Tracked event for sync - StreamId={StreamId}, EventType={EventType}, EventId={EventId}",
            streamId, eventTypeName, eventId.Value);
        }
      }

      // Local dispatch: Invoke in-process receptors (for Local, LocalNoPersist, Both)
      // Check for LocalDispatch flag specifically, not the composite Local mode
      if (mode.HasFlag(Dispatch.DispatchMode.LocalDispatch)) {
        if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
          var msgTypeName = messageType.Name;
          CascadeLogger.LogDebug("[CASCADE] CascadeEventsFromResult: Dispatching locally for {MessageType}", msgTypeName);
        }
        var publisher = GetUntypedReceptorPublisher(messageType);
        if (publisher != null) {
          // Security context is now established inside publisher via EstablishFullContextAsync
          // No need for EstablishMessageContextForCascade here
          await publisher(msg, null, default);
        }
      }

      // Event store only: Store to event store without transport (for Local, EventStoreOnly)
      // When EventStore is set but Outbox is NOT set, store with null destination
      // CRITICAL: Pass eventId to ensure storage uses same ID as tracking
      if (mode.HasFlag(Dispatch.DispatchMode.EventStore) && !mode.HasFlag(Dispatch.DispatchMode.Outbox) && msg is IEvent) {
        if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
          var msgTypeName = messageType.Name;
          CascadeLogger.LogDebug("[CASCADE] CascadeEventsFromResult: Calling CascadeToEventStoreOnlyAsync for {MessageType}", msgTypeName);
        }
        await CascadeToEventStoreOnlyAsync(msg, messageType, sourceEnvelope: sourceEnvelope, eventId: eventId);
      }

      // Outbox dispatch: Write to outbox for cross-service delivery (for Outbox, Both)
      // CRITICAL: Pass eventId to ensure storage uses same ID as tracking
      if (mode.HasFlag(Dispatch.DispatchMode.Outbox)) {
        if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
          var msgTypeName = messageType.Name;
          CascadeLogger.LogDebug("[CASCADE] CascadeEventsFromResult: Calling CascadeToOutboxAsync for {MessageType}", msgTypeName);
        }
        await CascadeToOutboxAsync(msg, messageType, sourceEnvelope: sourceEnvelope, eventId: eventId);
      }
    }

    if (extractedCount == 0) {
      if (CascadeLogger.IsEnabled(LogLevel.Warning)) {
        var resultTypeName = result.GetType().Name;
        CascadeLogger.LogWarning("[CASCADE] CascadeEventsFromResult: No messages extracted from result type {ResultType}. " +
          "This may indicate the result does not implement IMessage or is not wrapped in a supported collection/tuple.",
          resultTypeName);
      }
    } else if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
      CascadeLogger.LogDebug("[CASCADE] CascadeEventsFromResult: Extracted {Count} messages total", extractedCount);
    }
#pragma warning restore CA1848
  }

  /// <summary>
  /// Processes tags for a successfully handled message if tag processing is enabled.
  /// Called after cascade to invoke registered tag hooks.
  /// </summary>
  /// <returns>A task representing the asynchronous operation.</returns>
  /// <remarks>
  /// <para>
  /// Tag processing is skipped if:
  /// - EnableTagProcessing is false in WhizbangCoreOptions
  /// - TagProcessingMode is set to AsLifecycleStage (processed during lifecycle instead)
  /// - No IMessageTagProcessor is registered
  /// </para>
  /// </remarks>
  /// <docs>fundamentals/messages/message-tags#processing</docs>
  /// <tests>tests/Whizbang.Core.Tests/Tags/DispatcherTagProcessingTests.cs</tests>
  /// <summary>
  /// Invokes lifecycle receptors at the specified stages via a scoped IReceptorInvoker.
  /// Creates a scope per invocation to ensure proper scoped dependency resolution,
  /// full security context propagation, and event cascading.
  /// </summary>
  private async ValueTask _processTagsIfEnabledAsync(object message, Type messageType, CancellationToken ct = default) {
#pragma warning disable CA1848, CA1873 // Diagnostic logging using lazy-resolved logger
    DispatcherLogger.LogDebug("_processTagsIfEnabledAsync called for {MessageType}", messageType.Name);

    // Skip if tag processing is disabled
    if (!_coreOptions.EnableTagProcessing) {
      DispatcherLogger.LogDebug("Tag processing is DISABLED - returning early");
      return;
    }

    // Skip immediate processing if using lifecycle stage mode
    if (_coreOptions.TagProcessingMode != TagProcessingMode.AfterReceptorCompletion) {
      DispatcherLogger.LogDebug("TagProcessingMode is {TagProcessingMode}, not AfterReceptorCompletion - returning early", _coreOptions.TagProcessingMode);
      return;
    }

    // Skip if no processor is registered
    if (_messageTagProcessor is null) {
      DispatcherLogger.LogDebug("No IMessageTagProcessor registered - returning early");
      return;
    }

    DispatcherLogger.LogDebug("Calling ProcessTagsAsync at AfterReceptorCompletion...");
    // Use static AsyncLocal accessor - designed for singleton services like Dispatcher
    var scope = ScopeContextAccessor.CurrentContext;
    await _messageTagProcessor.ProcessTagsAsync(message, messageType, LifecycleStage.AfterReceptorCompletion, scope, ct);
    DispatcherLogger.LogDebug("ProcessTagsAsync completed");
#pragma warning restore CA1848, CA1873
  }

  /// <summary>
  /// Invokes ImmediateAsync lifecycle receptors for the dispatched message.
  /// Called after the business receptor completes, cascade, and tag processing.
  /// Creates a scope to resolve scoped IReceptorInvoker (Dispatcher is a singleton).
  /// </summary>
  /// <docs>fundamentals/lifecycle/lifecycle-stages#immediate-async</docs>
  private async ValueTask _invokeImmediateAsyncReceptorsAsync(
      IMessageEnvelope envelope,
      Type messageType,
      CancellationToken ct = default) {
    // Short-circuit if no receptor registry available
    if (_receptorRegistry is null) {
      return;
    }

    // Check if there are any ImmediateAsync receptors registered for this message type
    var receptors = _receptorRegistry.GetReceptorsFor(messageType, LifecycleStage.ImmediateAsync);
    if (receptors.Count == 0) {
      return;
    }

    // Create a scope to resolve scoped IReceptorInvoker
    // Dispatcher is a singleton — cannot inject scoped services directly
    await using var scope = _scopeFactory.CreateAsyncScope();
    var scopedInvoker = scope.ServiceProvider.GetService<IReceptorInvoker>();
    if (scopedInvoker is null) {
      return;
    }

    // Invoke ImmediateAsync receptors for the dispatched message
    var context = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.ImmediateAsync,
      MessageSource = MessageSource.Local
    };

    await scopedInvoker.InvokeAsync(envelope, LifecycleStage.ImmediateAsync, context, ct).ConfigureAwait(false);
  }

  /// <summary>
  /// Invokes PostLifecycle lifecycle receptors for the dispatched message.
  /// Called after the business receptor completes in the local dispatch path.
  /// Local events don't go through perspectives, so PostLifecycle fires immediately.
  /// Creates a scope to resolve scoped IReceptorInvoker (Dispatcher is a singleton).
  /// </summary>
  /// <docs>fundamentals/lifecycle/lifecycle-stages#post-lifecycle</docs>
  private async ValueTask _invokePostLifecycleReceptorsAsync(
      IMessageEnvelope envelope,
      object message,
      Type messageType,
      CancellationToken ct = default) {
    // Short-circuit if no receptor registry available
    if (_receptorRegistry is null) {
      return;
    }

    // Check if there are any PostLifecycle receptors registered for this message type
    var asyncReceptors = _receptorRegistry.GetReceptorsFor(messageType, LifecycleStage.PostLifecycleAsync);
    var inlineReceptors = _receptorRegistry.GetReceptorsFor(messageType, LifecycleStage.PostLifecycleInline);
    if (asyncReceptors.Count == 0 && inlineReceptors.Count == 0) {
      return;
    }

    // Create a scope to resolve scoped IReceptorInvoker
    // Dispatcher is a singleton — cannot inject scoped services directly
    await using var scope = _scopeFactory.CreateAsyncScope();
    var scopedInvoker = scope.ServiceProvider.GetService<IReceptorInvoker>();
    if (scopedInvoker is null) {
      return;
    }

    var context = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostLifecycleAsync,
      MessageSource = MessageSource.Local
    };

    // PostLifecycleAsync (non-blocking)
    if (asyncReceptors.Count > 0) {
      await scopedInvoker.InvokeAsync(envelope, LifecycleStage.PostLifecycleAsync, context, ct).ConfigureAwait(false);
      await scopedInvoker.InvokeAsync(envelope, LifecycleStage.ImmediateAsync,
        context with { CurrentStage = LifecycleStage.ImmediateAsync }, ct).ConfigureAwait(false);
    }

    // PostLifecycleInline (blocking)
    if (inlineReceptors.Count > 0) {
      await scopedInvoker.InvokeAsync(envelope, LifecycleStage.PostLifecycleInline,
        context with { CurrentStage = LifecycleStage.PostLifecycleInline }, ct).ConfigureAwait(false);
      await scopedInvoker.InvokeAsync(envelope, LifecycleStage.ImmediateAsync,
        context with { CurrentStage = LifecycleStage.ImmediateAsync }, ct).ConfigureAwait(false);
    }

    // Process message tags at PostLifecycleInline
    if (_messageTagProcessor is not null) {
      var scopeContext = ScopeContextAccessor.CurrentContext;
      await _messageTagProcessor.ProcessTagsAsync(
        message, messageType,
        LifecycleStage.PostLifecycleInline,
        scopeContext, ct
      ).ConfigureAwait(false);
    }
  }

  /// <summary>
  /// Checks if there are any ImmediateAsync receptors registered for the given message type.
  /// Used to bypass fast paths when ImmediateAsync processing is needed.
  /// </summary>
  private bool _hasImmediateAsyncReceptors(Type messageType) {
    return _receptorRegistry?.GetReceptorsFor(messageType, LifecycleStage.ImmediateAsync).Count > 0;
  }

  /// <summary>
  /// Checks if there are any PostLifecycle receptors registered for the given message type.
  /// Used to bypass fast paths when PostLifecycle processing is needed.
  /// </summary>
  private bool _hasPostLifecycleReceptors(Type messageType) {
    if (_receptorRegistry is null) {
      return false;
    }

    return _receptorRegistry.GetReceptorsFor(messageType, LifecycleStage.PostLifecycleAsync).Count > 0
      || _receptorRegistry.GetReceptorsFor(messageType, LifecycleStage.PostLifecycleInline).Count > 0;
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
  /// <docs>fundamentals/dispatcher/dispatcher#auto-cascade-to-outbox</docs>
  /// <tests>Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:Generator_WithEventReturningReceptor_GeneratesCascadeToOutboxAsync</tests>
  protected virtual Task CascadeToOutboxAsync(IMessage message, Type messageType, IMessageEnvelope? sourceEnvelope = null, Guid? eventId = null) {
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
  /// <docs>fundamentals/dispatcher/dispatcher#event-store-only</docs>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherRoutedCascadeTests.cs:CascadeEventStoreOnly_*</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/LocalEventStorageTests.cs:RouteEventStoreOnly_*</tests>
  protected virtual Task CascadeToEventStoreOnlyAsync(IMessage message, Type messageType, IMessageEnvelope? sourceEnvelope = null, Guid? eventId = null) {
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

    var sw = Stopwatch.StartNew();
    var eventType = eventData.GetType();
    var eventTypeName = eventType.Name;
    try {

      // Auto-generate StreamId for events with [GenerateStreamId] attribute
      _autoGenerateStreamIdIfNeeded(eventData!, eventType);

      // Create MessageId once - used for outbox and will be used by process_work_batch for event storage
      var messageId = MessageId.New();

      // Get strongly-typed delegate from generated code
      var publisher = GetReceptorPublisher(eventData, eventType);

      // Invoke local handlers - zero reflection, strongly typed
      await publisher(eventData);

      // Process tags after successful receptor completion
      await _processTagsIfEnabledAsync(eventData, eventType);

      // Publish event for cross-service delivery if work coordinator strategy is available
      // process_work_batch will store events to wh_event_store and create perspective events atomically
      await PublishToOutboxAsync(eventData, eventType, messageId);

      _dispatcherMetrics?.MessagesDispatched.Add(1,
        new KeyValuePair<string, object?>("message_type", eventTypeName),
        new KeyValuePair<string, object?>("pattern", "publish"));

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
    } catch (Exception ex) {
      _dispatcherMetrics?.Errors.Add(1,
        new KeyValuePair<string, object?>("message_type", eventTypeName),
        new KeyValuePair<string, object?>("error_type", ex.GetType().Name));
      throw;
    } finally {
      sw.Stop();
      _dispatcherMetrics?.PublishDuration.Record(sw.Elapsed.TotalMilliseconds);
    }
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

    var sw = Stopwatch.StartNew();
    var eventType = eventData.GetType();
    var eventTypeName = eventType.Name;
    try {

      // Auto-generate StreamId for events with [GenerateStreamId] attribute
      _autoGenerateStreamIdIfNeeded(eventData!, eventType);

      var messageId = MessageId.New();
      var publisher = GetReceptorPublisher(eventData, eventType);

      options.CancellationToken.ThrowIfCancellationRequested();
      await publisher(eventData);

      // Process tags after successful receptor completion
      await _processTagsIfEnabledAsync(eventData, eventType);

      await PublishToOutboxAsync(eventData, eventType, messageId);

      _dispatcherMetrics?.MessagesDispatched.Add(1,
        new KeyValuePair<string, object?>("message_type", eventTypeName),
        new KeyValuePair<string, object?>("pattern", "publish"));

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
    } catch (Exception ex) {
      _dispatcherMetrics?.Errors.Add(1,
        new KeyValuePair<string, object?>("message_type", eventTypeName),
        new KeyValuePair<string, object?>("error_type", ex.GetType().Name));
      throw;
    } finally {
      sw.Stop();
      _dispatcherMetrics?.PublishDuration.Record(sw.Elapsed.TotalMilliseconds);
    }
  }

  /// <summary>
  /// Cascades a message with explicit routing mode.
  /// Called by IEventCascader after resolving routing from wrappers and attributes.
  /// </summary>
  /// <docs>fundamentals/dispatcher/dispatcher#cascade-to-outbox</docs>
  /// <docs>fundamentals/security/message-security#security-context-in-event-cascades</docs>
  public async Task CascadeMessageAsync(IMessage message, IMessageEnvelope? sourceEnvelope, Dispatch.DispatchMode mode, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(message);
    cancellationToken.ThrowIfCancellationRequested();

    var sw = Stopwatch.StartNew();
    var messageType = message.GetType();

#pragma warning disable CA1848 // Diagnostic logging - performance not critical
    if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
      var msgTypeName = messageType.Name;
      CascadeLogger.LogDebug("[CASCADE] CascadeMessageAsync: Message={MessageType}, Mode={Mode}", msgTypeName, mode);
    }
#pragma warning restore CA1848

    // Generate eventId for tracking and storage consistency
    // CRITICAL: This SAME eventId must be used for both tracking (singleton tracker)
    // AND storage (outbox/event store) so that MarkProcessed can find the tracked event
    Guid? eventId = null;

    // Track event for perspective sync - enables cross-scope sync via singleton tracker
    // CRITICAL: This is the primary path for receptor event cascading via DispatcherEventCascader
    if (message is IEvent) {
      eventId = ValueObjects.TrackedGuid.NewMedo(); // Generate tracking ID for cascaded events (UUIDv7)
      var streamId = _streamIdExtractor?.ExtractStreamId(message, messageType) ?? Guid.Empty;

      // Cascade StreamId propagation: inherit from source command when event's StreamId is empty
      // This enables the pattern where [GenerateStreamId] is on the command and events inherit it
      if (streamId == Guid.Empty && sourceEnvelope is not null && _streamIdExtractor is not null) {
        var sourceStreamId = _streamIdExtractor.ExtractStreamId(sourceEnvelope.Payload!, sourceEnvelope.Payload!.GetType());
        if (sourceStreamId.HasValue && sourceStreamId.Value != Guid.Empty) {
          streamId = sourceStreamId.Value;
          // Set the StreamId on the event object so hop metadata captures it
          if (message is IHasStreamId hasStreamIdForCascade) {
            hasStreamIdForCascade.StreamId = streamId;
          } else {
            _streamIdExtractor.SetStreamId(message, streamId);
          }
          Log.StreamIdPropagatedFromSource(CascadeLogger, streamId, sourceEnvelope.Payload!.GetType().Name, messageType.Name);
        }
      }

      // Auto-generate StreamId based on [GenerateStreamId] attribute policy
      if (streamId == Guid.Empty && _streamIdExtractor is not null && message is IHasStreamId hasStreamId) {
        var (shouldGenerate, onlyIfEmpty) = _streamIdExtractor.GetGenerationPolicy(message);
        if (shouldGenerate && (!onlyIfEmpty || streamId == Guid.Empty)) {
          streamId = ValueObjects.TrackedGuid.NewMedo();
          hasStreamId.StreamId = streamId;
          Log.StreamIdAutoGenerated(CascadeLogger, streamId, messageType.Name, onlyIfEmpty);
        }
      }

      // Track in scoped tracker (same request scope)
      _scopedEventTracker?.TrackEmittedEvent(streamId, messageType, eventId.Value);

      // CRITICAL: Also track in singleton tracker for cross-scope sync
      // This enables Request 2 to wait for events emitted by Request 1
      if (_syncEventTracker is not null && _trackedEventTypeRegistry is not null) {
        var perspectiveNames = _trackedEventTypeRegistry.GetPerspectiveNames(messageType);
#pragma warning disable CA1848
        if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
          CascadeLogger.LogDebug("[SYNC_DEBUG] CascadeMessageAsync SINGLETON tracker check: EventType={EventType}, PerspectiveCount={Count}, Perspectives=[{Perspectives}]",
            messageType.FullName, perspectiveNames.Count, string.Join(", ", perspectiveNames));
        }
#pragma warning restore CA1848
        foreach (var perspectiveName in perspectiveNames) {
          _syncEventTracker.TrackEvent(messageType, eventId.Value, streamId, perspectiveName);
#pragma warning disable CA1848
          if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
            CascadeLogger.LogDebug("[SYNC_DEBUG] CascadeMessageAsync tracked in SINGLETON: StreamId={StreamId}, EventType={EventType}, EventId={EventId}, Perspective={Perspective}",
              streamId, messageType.Name, eventId.Value, perspectiveName);
          }
#pragma warning restore CA1848
        }
      } else if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
#pragma warning disable CA1848
        CascadeLogger.LogDebug("[SYNC_DEBUG] CascadeMessageAsync SINGLETON tracker DISABLED - _syncEventTracker={HasTracker}, _trackedEventTypeRegistry={HasRegistry}",
          _syncEventTracker is not null, _trackedEventTypeRegistry is not null);
#pragma warning restore CA1848
      }

#pragma warning disable CA1848
      if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
        var eventTypeName = messageType.Name;
        CascadeLogger.LogDebug("[CASCADE] CascadeMessageAsync: Tracked event for sync - StreamId={StreamId}, EventType={EventType}, EventId={EventId}",
          streamId, eventTypeName, eventId.Value);
      }
#pragma warning restore CA1848
    }

    // Local dispatch: Invoke in-process receptors (for Local, LocalNoPersist, Both)
    if (mode.HasFlag(Dispatch.DispatchMode.LocalDispatch)) {
#pragma warning disable CA1848
      if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
        var msgTypeName = messageType.Name;
        CascadeLogger.LogDebug("[CASCADE] CascadeMessageAsync: Dispatching locally for {MessageType}", msgTypeName);
      }
#pragma warning restore CA1848
      var publisher = GetUntypedReceptorPublisher(messageType);
      if (publisher != null) {
        await publisher(message, sourceEnvelope, cancellationToken);
      }
    }

    // Event store only: Store to event store without transport (for Local, EventStoreOnly)
    // Uses destination=null to store event and create perspective events, but skip transport.
    // This path is NOT taken if Outbox flag is also set (Outbox handles event storage via transport).
    // CRITICAL: Pass eventId to ensure storage uses same ID as tracking
    // Only events are stored (commands are silently skipped, consistent with current behavior)
    if (mode.HasFlag(Dispatch.DispatchMode.EventStore) && !mode.HasFlag(Dispatch.DispatchMode.Outbox) && message is IEvent) {
#pragma warning disable CA1848
      if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
        var msgTypeName = messageType.Name;
        CascadeLogger.LogDebug("[CASCADE] CascadeMessageAsync: Calling CascadeToEventStoreOnlyAsync for {MessageType}", msgTypeName);
      }
#pragma warning restore CA1848
      await CascadeToEventStoreOnlyAsync(message, messageType, sourceEnvelope, eventId);
    }

    // Outbox dispatch: Write to outbox for cross-service delivery (for Outbox, Both)
    // CRITICAL: Pass eventId to ensure storage uses same ID as tracking
    if (mode.HasFlag(Dispatch.DispatchMode.Outbox)) {
#pragma warning disable CA1848
      if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
        var msgTypeName = messageType.Name;
        CascadeLogger.LogDebug("[CASCADE] CascadeMessageAsync: Calling CascadeToOutboxAsync for {MessageType}", msgTypeName);
      }
#pragma warning restore CA1848
      await CascadeToOutboxAsync(message, messageType, sourceEnvelope, eventId);
    }

    sw.Stop();
    _dispatcherMetrics?.CascadeDuration.Record(sw.Elapsed.TotalMilliseconds);
    _dispatcherMetrics?.EventsCascaded.Add(1,
      new KeyValuePair<string, object?>("event_type", messageType.Name),
      new KeyValuePair<string, object?>("destination", mode.ToString()));
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
  /// <docs>fundamentals/dispatcher/dispatcher#auto-cascade-to-outbox</docs>
  /// <tests>Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:Generator_CascadeToOutbox_CallsPublishToOutboxWithMessageIdAsync</tests>
  protected async Task PublishToOutboxAsync<TEvent>(TEvent eventData, Type eventType, MessageId messageId, IMessageEnvelope? sourceEnvelope = null, bool eventStoreOnly = false) {
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
    if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
      var eventTypeName = eventType.Name;
      CascadeLogger.LogDebug("[CASCADE] PublishToOutboxAsync: Called for {EventType}, MessageId={MessageId}", eventTypeName, messageId);
    }
#pragma warning restore CA1848

    // Create scope to resolve scoped IWorkCoordinatorStrategy
    // Guard against ObjectDisposedException during application shutdown or hot reload
    IServiceScope scope;
    try {
      scope = _scopeFactory.CreateScope();
    } catch (ObjectDisposedException) {
      // Service provider is disposed - application is shutting down
      // Dropping events during shutdown is acceptable behavior
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
      if (CascadeLogger.IsEnabled(LogLevel.Warning)) {
        var eventTypeName = eventType.Name;
        CascadeLogger.LogWarning(
          "[CASCADE] PublishToOutboxAsync: Service provider disposed during shutdown - event {EventType} will not be published to outbox. MessageId={MessageId}",
          eventTypeName, messageId);
      }
#pragma warning restore CA1848
      return;
    }

    try {
      var strategy = scope.ServiceProvider.GetService<IWorkCoordinatorStrategy>();
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
      if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
        var strategyTypeName = strategy?.GetType().Name ?? "null";
        CascadeLogger.LogDebug("[CASCADE] PublishToOutboxAsync: Strategy resolved: {StrategyType}", strategyTypeName);
      }
#pragma warning restore CA1848

      // If no strategy is registered, check for deferred channel
      if (strategy == null) {
        // Try to get deferred channel for resilient event publishing
        var deferredChannel = scope.ServiceProvider.GetService<IDeferredOutboxChannel>();

        if (deferredChannel != null) {
          // Queue to deferred channel for next lifecycle loop pickup
          await _deferEventToChannelAsync(eventData, eventType, messageId, sourceEnvelope, eventStoreOnly, deferredChannel);
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
          if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
            CascadeLogger.LogDebug("[CASCADE] PublishToOutboxAsync: Event {EventType} queued to deferred channel (no active strategy)",
              eventType.Name);
          }
#pragma warning restore CA1848
          return;
        }

        // No strategy AND no deferred channel - log warning for backward compatibility
#pragma warning disable CA1848 // Use LoggerMessage delegates for performance - acceptable in error path
        var logger = scope.ServiceProvider.GetService<ILogger<Dispatcher>>();
        if (logger != null && logger.IsEnabled(LogLevel.Warning)) {
          var eventTypeName = eventType.Name;
          logger.LogWarning(
            "IWorkCoordinatorStrategy not registered and IDeferredOutboxChannel not available - " +
            "event will not be published to outbox for cross-service delivery. " +
            "Register IWorkCoordinatorStrategy or IDeferredOutboxChannel to enable outbox pattern. EventType: {EventType}",
            eventTypeName);
        }
#pragma warning restore CA1848
        return;
      }

      // Resolve destination topic using registry and routing strategy
      // When eventStoreOnly is true, use null destination to bypass transport
      string? destination = eventStoreOnly ? null : _resolveEventTopic(eventType);

      // Cascade StreamId propagation: inherit from source command when event's StreamId is empty
      // This enables the pattern where [GenerateStreamId] is on the command and events inherit it
      if (sourceEnvelope is not null && _streamIdExtractor is not null) {
        var eventStreamId = _streamIdExtractor.ExtractStreamId(eventData!, eventType);
        if (!eventStreamId.HasValue || eventStreamId.Value == Guid.Empty) {
          var sourceStreamId = _streamIdExtractor.ExtractStreamId(sourceEnvelope.Payload!, sourceEnvelope.Payload!.GetType());
          if (sourceStreamId.HasValue && sourceStreamId.Value != Guid.Empty) {
            if (eventData is IHasStreamId hasStreamId) {
              hasStreamId.StreamId = sourceStreamId.Value;
            } else {
              _streamIdExtractor.SetStreamId(eventData!, sourceStreamId.Value);
            }
            Log.StreamIdPropagatedFromSource(CascadeLogger, sourceStreamId.Value, sourceEnvelope.Payload!.GetType().Name, eventType.Name);
          }
        }
      }

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
      // Scope: First try ambient context, then inherit from source envelope
      var propagatedScope = ScopeDelta.FromSecurityContext(CascadeContext.GetSecurityFromAmbient());
      var sourceScope = sourceEnvelope?.GetCurrentScope() is { } s
        ? ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = s.Scope?.TenantId, UserId = s.Scope?.UserId })
        : null;
      var finalScope = propagatedScope ?? sourceScope;

      var hop = new MessageHop {
        Type = HopType.Current,
        ServiceInstance = _instanceProvider.ToInfo(),
        Topic = destination ?? "(event-store)",
        Timestamp = DateTimeOffset.UtcNow,
        Metadata = hopMetadata,
        Scope = finalScope,
        TraceParent = System.Diagnostics.Activity.Current?.Id
      };
      envelope.AddHop(hop);

      System.Diagnostics.Debug.WriteLine($"[Dispatcher] Queueing event {eventType.Name} to work coordinator with destination '{destination}'");
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
      if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
        CascadeLogger.LogDebug("[CASCADE] PublishToOutboxAsync: Destination={Destination}", destination);
        CascadeLogger.LogDebug("[TRACE] PublishToOutboxAsync: TraceParent={TraceParent}, HasActivity={HasActivity}",
          hop.TraceParent ?? "(null)", System.Diagnostics.Activity.Current is not null);
      }
#pragma warning restore CA1848

      // Serialize envelope to OutboxMessage
      var newOutboxMessage = _serializeToNewOutboxMessage(envelope, eventData!, eventType, destination);
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
      if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
        var newMsgId = newOutboxMessage.MessageId;
        var newMsgType = newOutboxMessage.MessageType;
        CascadeLogger.LogDebug("[CASCADE] PublishToOutboxAsync: Created NewOutboxMessage, MessageId={MessageId}, Type={Type}",
          newMsgId, newMsgType);
      }
#pragma warning restore CA1848

      // Queue event for batched processing
      // Guard against ObjectDisposedException — the singleton IntervalWorkCoordinatorStrategy
      // may be disposed during host shutdown while in-flight receptors are still publishing.
      try {
        strategy.QueueOutboxMessage(newOutboxMessage);
      } catch (ObjectDisposedException) {
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
        if (CascadeLogger.IsEnabled(LogLevel.Warning)) {
          CascadeLogger.LogWarning(
            "[CASCADE] PublishToOutboxAsync: Strategy disposed during shutdown - event {EventType} will not be published to outbox. MessageId={MessageId}",
            eventType.Name, messageId);
        }
#pragma warning restore CA1848
        return;
      }
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
      if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
        CascadeLogger.LogDebug("[CASCADE] PublishToOutboxAsync: Called QueueOutboxMessage");
      }
#pragma warning restore CA1848

      // Flush strategy to execute the batch
      var workBatch = await strategy.FlushAsync(WorkBatchFlags.None);
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
      if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
        var outboxCount = workBatch.OutboxWork.Count;
        var inboxCount = workBatch.InboxWork.Count;
        CascadeLogger.LogDebug("[CASCADE] PublishToOutboxAsync: FlushAsync returned OutboxWork={OutboxCount}, InboxWork={InboxCount}",
          outboxCount, inboxCount);
      }
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
  /// Publishes an event to the outbox using its runtime type for serialization.
  /// This non-generic overload is used when the compile-time type is an interface (IEvent, ICommand)
  /// but the runtime type is a concrete class. Using the runtime type ensures proper JSON serialization.
  /// </summary>
  /// <param name="eventData">The event to publish (runtime type used for serialization)</param>
  /// <param name="eventType">The runtime type of the event</param>
  /// <param name="messageId">The message ID for tracking</param>
  /// <param name="sourceEnvelope">Optional source envelope for context propagation</param>
  /// <param name="eventStoreOnly">If true, stores event without transport delivery</param>
  /// <docs>fundamentals/dispatcher/dispatcher#auto-cascade-to-outbox</docs>
  protected async Task PublishToOutboxDynamicAsync(IMessage eventData, Type eventType, MessageId messageId, IMessageEnvelope? sourceEnvelope = null, bool eventStoreOnly = false) {
    // Create scope to resolve scoped IWorkCoordinatorStrategy
    var scope = _scopeFactory.CreateScope();
    try {
      var strategy = scope.ServiceProvider.GetService<IWorkCoordinatorStrategy>();

      // If no strategy is registered, skip outbox routing (local-only event)
      if (strategy == null) {
        return;
      }

      // Resolve destination topic using registry and routing strategy
      string? destination = eventStoreOnly ? null : _resolveEventTopic(eventType);

      // Cascade StreamId propagation: inherit from source command when event's StreamId is empty
      if (sourceEnvelope is not null && _streamIdExtractor is not null) {
        var eventStreamId = _streamIdExtractor.ExtractStreamId(eventData, eventType);
        if (!eventStreamId.HasValue || eventStreamId.Value == Guid.Empty) {
          var sourceStreamId = _streamIdExtractor.ExtractStreamId(sourceEnvelope.Payload!, sourceEnvelope.Payload!.GetType());
          if (sourceStreamId.HasValue && sourceStreamId.Value != Guid.Empty) {
            if (eventData is IHasStreamId hasStreamId) {
              hasStreamId.StreamId = sourceStreamId.Value;
            } else {
              _streamIdExtractor.SetStreamId(eventData, sourceStreamId.Value);
            }
            Log.StreamIdPropagatedFromSource(CascadeLogger, sourceStreamId.Value, sourceEnvelope.Payload!.GetType().Name, eventType.Name);
          }
        }
      }

      // Serialize the message directly using the runtime type via JsonContextRegistry
      // This avoids creating MessageEnvelope<IEvent> which can't be serialized
      var typeNameForLookup = eventType.AssemblyQualifiedName ?? eventType.FullName ?? eventType.Name;
      var combinedOptions = Serialization.JsonContextRegistry.CreateCombinedOptions();
      var jsonTypeInfo = Serialization.JsonContextRegistry.GetTypeInfoByName(typeNameForLookup, combinedOptions);
      if (jsonTypeInfo == null) {
        throw new InvalidOperationException(
          $"No JSON type info found for {eventType.FullName}. Ensure the type is registered in a JsonSerializerContext.");
      }

      var payloadJson = JsonSerializer.SerializeToElement(eventData, jsonTypeInfo);

      // Create the JsonElement envelope directly
      var jsonEnvelope = new MessageEnvelope<JsonElement> {
        MessageId = messageId,
        Payload = payloadJson,
        Hops = []
      };

      // Extract aggregate ID and add to hop metadata
      var hopMetadata = _createHopMetadata(eventData, eventType);

      // Add hop indicating message is being stored to outbox
      var propagatedScope2 = ScopeDelta.FromSecurityContext(CascadeContext.GetSecurityFromAmbient());
      var sourceScope2 = sourceEnvelope?.GetCurrentScope() is { } sc
        ? ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = sc.Scope?.TenantId, UserId = sc.Scope?.UserId })
        : null;
      var hop = new MessageHop {
        Type = HopType.Current,
        ServiceInstance = _instanceProvider.ToInfo(),
        Topic = destination ?? "(event-store)",
        Timestamp = DateTimeOffset.UtcNow,
        Metadata = hopMetadata,
        Scope = propagatedScope2 ?? sourceScope2,
        TraceParent = System.Diagnostics.Activity.Current?.Id
      };
      jsonEnvelope.AddHop(hop);

      // Extract stream ID
      var streamId = _streamIdExtractor?.ExtractStreamId(eventData, eventType)
        ?? _extractStreamIdFromMetadata(hopMetadata)
        ?? messageId.Value;

      // Create OutboxMessage with all required fields
      var newOutboxMessage = new OutboxMessage {
        MessageId = jsonEnvelope.MessageId.Value,
        Destination = destination,
        Envelope = jsonEnvelope,
        Metadata = new EnvelopeMetadata {
          MessageId = jsonEnvelope.MessageId,
          Hops = jsonEnvelope.Hops?.ToList() ?? []
        },
        EnvelopeType = $"Whizbang.Core.Observability.MessageEnvelope`1[[{eventType.AssemblyQualifiedName}]], Whizbang.Core",
        StreamId = streamId,
        IsEvent = eventData is IEvent,
        Scope = _extractScope(jsonEnvelope),
        MessageType = eventType.AssemblyQualifiedName ?? eventType.FullName ?? eventType.Name
      };

      // Queue event for batched processing
      strategy.QueueOutboxMessage(newOutboxMessage);

      // Flush strategy to execute the batch
      await strategy.FlushAsync(WorkBatchFlags.None, mode: FlushMode.BestEffort);
    } finally {
      if (scope is IAsyncDisposable asyncDisposable) {
        await asyncDisposable.DisposeAsync();
      } else {
        scope.Dispose();
      }
    }
  }

  /// <summary>
  /// Extracts PerspectiveScope from an envelope's current scope context.
  /// Returns null if no scope is available.
  /// </summary>
  private static PerspectiveScope? _extractScope(IMessageEnvelope envelope) {
    var scopeContext = envelope.GetCurrentScope();
    return scopeContext?.Scope;
  }

  /// <summary>
  /// Extracts stream ID from hop metadata (aggregate ID stored as JsonElement).
  /// </summary>
  private static Guid? _extractStreamIdFromMetadata(Dictionary<string, JsonElement>? metadata) {
    if (metadata == null) {
      return null;
    }

    // Try both AggregateId (generated key) and aggregateId (legacy key)
    if ((metadata.TryGetValue("AggregateId", out var aggIdElement) || metadata.TryGetValue("aggregateId", out aggIdElement))
        && aggIdElement.ValueKind == JsonValueKind.String
        && Guid.TryParse(aggIdElement.GetString(), out var guidValue)) {
      return guidValue;
    }

    return null;
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

      // Start dispatch activity to serve as parent for handler traces (on receiving end)
      // The activity context will be propagated through the outbox message
      var parentActivity = Activity.Current;
      using var dispatchActivity = WhizbangActivitySource.Execution.StartActivity($"Dispatch {messageType.Name} (Outbox)", ActivityKind.Internal);
      if (dispatchActivity != null) {
        dispatchActivity.SetTag("whizbang.message.type", messageType.FullName);
        dispatchActivity.SetTag("whizbang.message.id", envelope.MessageId.ToString());
        dispatchActivity.SetTag("whizbang.correlation.id", envelope.GetCorrelationId()?.ToString());
        dispatchActivity.SetTag("whizbang.dispatch.destination", destination);
        dispatchActivity.SetTag("whizbang.debug.parent.id", parentActivity?.Id ?? "none");
        dispatchActivity.SetTag("whizbang.debug.parent.source", parentActivity?.Source?.Name ?? "none");
      }

      // Serialize envelope to OutboxMessage
      var newOutboxMessage = _serializeToNewOutboxMessage(envelope, message!, messageType, destination);

      // Queue message for batched processing
      strategy.QueueOutboxMessage(newOutboxMessage);

      // Flush strategy to execute the batch (strategy determines when to actually flush)
      await strategy.FlushAsync(WorkBatchFlags.None, mode: FlushMode.BestEffort);

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

      // Start dispatch activity to serve as parent for handler traces (on receiving end)
      // The activity context will be propagated through the outbox message
      var parentActivity = Activity.Current;
      using var dispatchActivity = WhizbangActivitySource.Execution.StartActivity($"Dispatch {messageType.Name} (Outbox)", ActivityKind.Internal);
      if (dispatchActivity != null) {
        dispatchActivity.SetTag("whizbang.message.type", messageType.FullName);
        dispatchActivity.SetTag("whizbang.message.id", envelope.MessageId.ToString());
        dispatchActivity.SetTag("whizbang.correlation.id", envelope.GetCorrelationId()?.ToString());
        dispatchActivity.SetTag("whizbang.dispatch.destination", destination);
        dispatchActivity.SetTag("whizbang.debug.parent.id", parentActivity?.Id ?? "none");
        dispatchActivity.SetTag("whizbang.debug.parent.source", parentActivity?.Source?.Name ?? "none");
      }

      // Serialize envelope to OutboxMessage
      var newOutboxMessage = _serializeToNewOutboxMessage(envelope, message, messageType, destination);

      // Queue message for batched processing
      strategy.QueueOutboxMessage(newOutboxMessage);

      // Flush strategy to execute the batch (strategy determines when to actually flush)
      // Flush strategy to execute the batch (strategy determines when to actually flush)
      await strategy.FlushAsync(WorkBatchFlags.None, mode: FlushMode.BestEffort);

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
        var destination = message is IEvent
            ? _resolveEventTopic(messageType)
            : _resolveCommandDestination(messageType);
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
      await strategy.FlushAsync(WorkBatchFlags.None, mode: FlushMode.BestEffort);

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

  /// <summary>
  /// Flushes outbox batch and adds Accepted receipts only for messages without a local receptor.
  /// Silently skips outbox if no strategy is registered and all messages were handled locally.
  /// </summary>
  private async Task _flushOutboxBatchAndCollectReceiptsAsync(
    List<(object message, Type messageType, IMessageContext context)> outboxMessages,
    List<bool> hasLocalReceptor,
    List<IDeliveryReceipt> receipts
  ) {
    if (outboxMessages.Count == 0) {
      return;
    }

    try {
      var outboxReceipts = await _sendManyToOutboxAsync(outboxMessages);
      // Only add Accepted receipts for messages without a local receptor
      // (locally-handled messages already have Delivered receipts)
      for (var i = 0; i < outboxReceipts.Count; i++) {
        if (!hasLocalReceptor[i]) {
          receipts.Add(outboxReceipts[i]);
        }
      }
    } catch (InvalidOperationException) when (hasLocalReceptor.TrueForAll(static x => x)) {
      // No outbox strategy — all messages handled locally only.
      // Cross-service delivery will not occur without IWorkCoordinatorStrategy.
      if (CascadeLogger.IsEnabled(LogLevel.Warning)) {
        Log.OutboxDeliverySkipped(CascadeLogger, outboxMessages.Count);
      }
    }
  }

  // ========================================
  // LOCAL INVOKE AND SYNC - Wait for All Perspectives
  // ========================================

  private static readonly TimeSpan _defaultSyncTimeout = TimeSpan.FromSeconds(30);

  /// <inheritdoc />
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public async Task<TResult> LocalInvokeAndSyncAsync<TMessage, TResult>(
      TMessage message,
      TimeSpan? timeout = null,
      Action<SyncWaitingContext>? onWaiting = null,
      Action<SyncDecisionContext>? onDecisionMade = null,
      CancellationToken cancellationToken = default)
      where TMessage : notnull {
    var sw = Stopwatch.StartNew();
    try {
      // Execute the handler
      var result = await LocalInvokeAsync<TMessage, TResult>(message);

      // Wait for all perspectives to process emitted events
      var syncResult = await _waitForAllPerspectivesAsync(timeout ?? _defaultSyncTimeout, onWaiting, onDecisionMade, cancellationToken);

      if (syncResult.Outcome == SyncOutcome.TimedOut) {
        throw new TimeoutException(
            $"Perspectives did not complete processing within {timeout ?? _defaultSyncTimeout}. " +
            $"Handler completed successfully but {syncResult.EventsAwaited} event(s) are still being processed.");
      }

      return result;
    } finally {
      sw.Stop();
      _dispatcherMetrics?.LocalInvokeAndSyncDuration.Record(sw.Elapsed.TotalMilliseconds);
    }
  }

  /// <inheritdoc />
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public async Task<SyncResult> LocalInvokeAndSyncAsync<TMessage>(
      TMessage message,
      TimeSpan? timeout = null,
      Action<SyncWaitingContext>? onWaiting = null,
      Action<SyncDecisionContext>? onDecisionMade = null,
      CancellationToken cancellationToken = default)
      where TMessage : notnull {
    var sw = Stopwatch.StartNew();
    try {
      // Execute the handler
      await LocalInvokeAsync(message);

      // Wait for all perspectives to process emitted events
      return await _waitForAllPerspectivesAsync(timeout ?? _defaultSyncTimeout, onWaiting, onDecisionMade, cancellationToken);
    } finally {
      sw.Stop();
      _dispatcherMetrics?.LocalInvokeAndSyncDuration.Record(sw.Elapsed.TotalMilliseconds);
    }
  }

  /// <inheritdoc />
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public async Task<TResult> LocalInvokeAndSyncAsync<TMessage, TResult, TPerspective>(
      TMessage message,
      TimeSpan? timeout = null,
      Action<SyncWaitingContext>? onWaiting = null,
      Action<SyncDecisionContext>? onDecisionMade = null,
      CancellationToken cancellationToken = default)
      where TMessage : notnull
      where TPerspective : class {
    var sw = Stopwatch.StartNew();
    try {
      // Execute the handler
      var result = await LocalInvokeAsync<TMessage, TResult>(message);

      // Wait for the specific perspective to process emitted events
      var syncResult = await _waitForSpecificPerspectiveAsync<TMessage, TPerspective>(
          message, timeout ?? _defaultSyncTimeout, onWaiting, onDecisionMade, cancellationToken);

      if (syncResult.Outcome == SyncOutcome.TimedOut) {
        throw new TimeoutException(
            $"Perspective {typeof(TPerspective).Name} did not complete processing within {timeout ?? _defaultSyncTimeout}. " +
            $"Handler completed successfully but {syncResult.EventsAwaited} event(s) are still being processed.");
      }

      return result;
    } finally {
      sw.Stop();
      _dispatcherMetrics?.LocalInvokeAndSyncDuration.Record(sw.Elapsed.TotalMilliseconds);
    }
  }

  /// <inheritdoc />
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public async Task<SyncResult> LocalInvokeAndSyncForPerspectiveAsync<TMessage, TPerspective>(
      TMessage message,
      TimeSpan? timeout = null,
      Action<SyncWaitingContext>? onWaiting = null,
      Action<SyncDecisionContext>? onDecisionMade = null,
      CancellationToken cancellationToken = default)
      where TMessage : notnull
      where TPerspective : class {
    var sw = Stopwatch.StartNew();
    try {
      // Execute the handler
      await LocalInvokeAsync(message);

      // Wait for the specific perspective to process emitted events
      return await _waitForSpecificPerspectiveAsync<TMessage, TPerspective>(
          message, timeout ?? _defaultSyncTimeout, onWaiting, onDecisionMade, cancellationToken);
    } finally {
      sw.Stop();
      _dispatcherMetrics?.LocalInvokeAndSyncDuration.Record(sw.Elapsed.TotalMilliseconds);
    }
  }

  /// <summary>
  /// Waits for all perspectives to process events emitted in the current scope.
  /// </summary>
  private async Task<SyncResult> _waitForAllPerspectivesAsync(
      TimeSpan timeout,
      Action<SyncWaitingContext>? onWaiting,
      Action<SyncDecisionContext>? onDecisionMade,
      CancellationToken cancellationToken) {
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var startedAt = DateTimeOffset.UtcNow;

    // Get tracked events from the scoped tracker
    var scopedTracker = _scopedEventTracker ?? ScopedEventTrackerAccessor.CurrentTracker;
    if (scopedTracker is null) {
      // No tracker available - no events to wait for
      var noPendingResult = new SyncResult(SyncOutcome.NoPendingEvents, 0, stopwatch.Elapsed);
      _invokeOnDecisionMade(onDecisionMade, perspectiveType: null, noPendingResult, didWait: false);
      return noPendingResult;
    }

    var trackedEvents = scopedTracker.GetEmittedEvents();
    if (trackedEvents.Count == 0) {
      // No events were emitted
      var noPendingResult = new SyncResult(SyncOutcome.NoPendingEvents, 0, stopwatch.Elapsed);
      _invokeOnDecisionMade(onDecisionMade, perspectiveType: null, noPendingResult, didWait: false);
      return noPendingResult;
    }

    // Extract event IDs and stream IDs
    var eventIds = trackedEvents.Select(e => e.EventId).ToList();
    var streamIds = trackedEvents.Select(e => e.StreamId).Distinct().ToList();

    // Wait for all perspectives to process
    if (_eventCompletionAwaiter is null) {
      // No awaiter registered - can't wait for perspectives
      // Return synced since we can't verify either way
      var syncedResult = new SyncResult(SyncOutcome.Synced, eventIds.Count, stopwatch.Elapsed);
      _invokeOnDecisionMade(onDecisionMade, perspectiveType: null, syncedResult, didWait: false);
      return syncedResult;
    }

    // Invoke onWaiting before starting the wait
    _invokeOnWaiting(onWaiting, perspectiveType: null, eventIds.Count, streamIds, timeout, startedAt);

    var completed = await _eventCompletionAwaiter.WaitForEventsAsync(eventIds, timeout, cancellationToken);

    stopwatch.Stop();
    var result = new SyncResult(
        completed ? SyncOutcome.Synced : SyncOutcome.TimedOut,
        eventIds.Count,
        stopwatch.Elapsed);

    _invokeOnDecisionMade(onDecisionMade, perspectiveType: null, result, didWait: true);
    return result;
  }

  /// <summary>
  /// Waits for a specific perspective to process events on the stream identified from the message.
  /// </summary>
  private async Task<SyncResult> _waitForSpecificPerspectiveAsync<TMessage, TPerspective>(
      TMessage message,
      TimeSpan timeout,
      Action<SyncWaitingContext>? onWaiting,
      Action<SyncDecisionContext>? onDecisionMade,
      CancellationToken cancellationToken)
      where TMessage : notnull
      where TPerspective : class {
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var startedAt = DateTimeOffset.UtcNow;
    var perspectiveType = typeof(TPerspective);

    // Extract stream ID from message
    var streamId = _streamIdExtractor?.ExtractStreamId(message, typeof(TMessage));
    if (streamId is null) {
      // No stream ID on message - no stream-specific events to wait for
      var noPendingResult = new SyncResult(SyncOutcome.NoPendingEvents, 0, stopwatch.Elapsed);
      _invokeOnDecisionMade(onDecisionMade, perspectiveType, noPendingResult, didWait: false);
      return noPendingResult;
    }

    // Create a scope to resolve scoped services
    await using var scope = _internalServiceProvider.CreateAsyncScope();
    var syncAwaiter = scope.ServiceProvider.GetService<IPerspectiveSyncAwaiter>();

    if (syncAwaiter is null) {
      // No perspective sync awaiter registered - can't wait for perspective
      // Return synced since we can't verify either way
      var syncedResult = new SyncResult(SyncOutcome.Synced, 1, stopwatch.Elapsed);
      _invokeOnDecisionMade(onDecisionMade, perspectiveType, syncedResult, didWait: false);
      return syncedResult;
    }

    // Invoke onWaiting before starting the wait
    _invokeOnWaiting(onWaiting, perspectiveType, eventCount: 1, [streamId.Value], timeout, startedAt);

    // Wait for the specific perspective to process events on this stream
    var result = await syncAwaiter.WaitForStreamAsync(perspectiveType, streamId.Value, eventTypes: null, timeout, ct: cancellationToken);

    stopwatch.Stop();
    var finalResult = new SyncResult(result.Outcome, result.EventsAwaited, stopwatch.Elapsed);
    _invokeOnDecisionMade(onDecisionMade, perspectiveType, finalResult, didWait: true);
    return finalResult;
  }

  /// <summary>
  /// Invokes the onWaiting callback safely, swallowing any exceptions.
  /// </summary>
  private static void _invokeOnWaiting(
      Action<SyncWaitingContext>? onWaiting,
      Type? perspectiveType,
      int eventCount,
      IReadOnlyList<Guid> streamIds,
      TimeSpan timeout,
      DateTimeOffset startedAt) {
    if (onWaiting is null) {
      return;
    }

    try {
      var context = new SyncWaitingContext {
        PerspectiveType = perspectiveType,
        EventCount = eventCount,
        StreamIds = streamIds,
        Timeout = timeout,
        StartedAt = startedAt
      };
      onWaiting(context);
    } catch {
      // Swallow exceptions - one bad callback shouldn't break sync
    }
  }

  /// <summary>
  /// Invokes the onDecisionMade callback safely, swallowing any exceptions.
  /// </summary>
  private static void _invokeOnDecisionMade(
      Action<SyncDecisionContext>? onDecisionMade,
      Type? perspectiveType,
      SyncResult result,
      bool didWait) {
    if (onDecisionMade is null) {
      return;
    }

    try {
      var context = new SyncDecisionContext {
        PerspectiveType = perspectiveType,
        Outcome = result.Outcome,
        EventsAwaited = result.EventsAwaited,
        ElapsedTime = result.ElapsedTime,
        DidWait = didWait
      };
      onDecisionMade(context);
    } catch {
      // Swallow exceptions - one bad callback shouldn't break sync
    }
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

    var sw = Stopwatch.StartNew();
    try {
      var messageList = messages.ToList();
      _dispatcherMetrics?.SendManyBatchSize.Record(messageList.Count);
      var receipts = new List<IDeliveryReceipt>();

      // All messages go to outbox for cross-service delivery.
      // Events route via _resolveEventTopic, commands via _resolveCommandDestination.
      // Messages with local receptors are ALSO processed locally.
      var outboxMessages = new List<(object message, Type messageType, IMessageContext context)>();
      var hasLocalReceptor = new List<bool>();

      var messageType = typeof(TMessage);
      foreach (var message in messageList) {
        // Use <object> to find ANY receptor (result-returning or void)
        var invoker = GetReceptorInvoker<object>(message, messageType);
        var isLocal = invoker != null;
        hasLocalReceptor.Add(isLocal);

        if (isLocal) {
          // Has local receptor - process locally (Delivered receipt)
          var cascade = _cascadeContextFactory.NewRoot();
          var receipt = await _sendAsyncInternalAsync<TMessage>(message, MessageContext.Create(cascade));
          receipts.Add(receipt);
        }

        // ALWAYS queue for outbox delivery (cross-service propagation)
        var outboxCascade = _cascadeContextFactory.NewRoot();
        outboxMessages.Add((message, messageType, MessageContext.Create(outboxCascade)));
      }

      // Flush outbox batch and collect receipts for non-local messages
      await _flushOutboxBatchAndCollectReceiptsAsync(outboxMessages, hasLocalReceptor, receipts);

      return receipts;
    } finally {
      sw.Stop();
      _dispatcherMetrics?.SendManyDuration.Record(sw.Elapsed.TotalMilliseconds);
    }
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

    var sw = Stopwatch.StartNew();
    try {
      var messageList = messages.ToList();
      _dispatcherMetrics?.SendManyBatchSize.Record(messageList.Count);
      var receipts = new List<IDeliveryReceipt>();

      // All messages go to outbox for cross-service delivery.
      // Events route via _resolveEventTopic, commands via _resolveCommandDestination.
      // Messages with local receptors are ALSO processed locally.
      var outboxMessages = new List<(object message, Type messageType, IMessageContext context)>();
      var hasLocalReceptor = new List<bool>();

      foreach (var message in messageList) {
        var messageType = message.GetType();
        var invoker = GetReceptorInvoker<object>(message, messageType);
        var isLocal = invoker != null;
        hasLocalReceptor.Add(isLocal);

        if (isLocal) {
          // Has local receptor - process locally (Delivered receipt)
          var receipt = await SendAsync(message);
          receipts.Add(receipt);
        }

        // ALWAYS queue for outbox delivery (cross-service propagation)
        var cascade = _cascadeContextFactory.NewRoot();
        outboxMessages.Add((message, messageType, MessageContext.Create(cascade)));
      }

      // Flush outbox batch and collect receipts for non-local messages
      await _flushOutboxBatchAndCollectReceiptsAsync(outboxMessages, hasLocalReceptor, receipts);

      return receipts;
    } finally {
      sw.Stop();
      _dispatcherMetrics?.SendManyDuration.Record(sw.Elapsed.TotalMilliseconds);
    }
  }

  /// <summary>
  /// Publishes multiple typed events with event routing (namespace-specific topics).
  /// Each event is processed locally (if handlers exist) and queued to the outbox.
  /// Optimized for batch operations - creates a single scope and flushes once.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherOutboxTests.cs:PublishManyAsync_Generic_QueuesAllEventsWithEventRoutingAsync</tests>
  /// <docs>fundamentals/dispatcher/dispatcher#publishmanyasync</docs>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public async Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync<TEvent>(IEnumerable<TEvent> events) where TEvent : notnull {
    ArgumentNullException.ThrowIfNull(events);

    var sw = Stopwatch.StartNew();
    try {
      var eventList = events.ToList();
      _dispatcherMetrics?.SendManyBatchSize.Record(eventList.Count);
      var receipts = new List<IDeliveryReceipt>();

      // All events go to outbox for cross-service delivery.
      // Events with local receptors are ALSO processed locally.
      var outboxMessages = new List<(object message, Type messageType, IMessageContext context)>();
      var hasLocalReceptor = new List<bool>();

      var eventType = typeof(TEvent);
      foreach (var eventData in eventList) {
        // Use <object> to find ANY receptor (result-returning or void)
        var invoker = GetReceptorInvoker<object>(eventData, eventType);
        var isLocal = invoker != null;
        hasLocalReceptor.Add(isLocal);

        if (isLocal) {
          // Auto-generate StreamId for events with [GenerateStreamId] attribute
          _autoGenerateStreamIdIfNeeded(eventData!, eventType);

          // Has local receptor - process locally via publish semantics
          var cascade = _cascadeContextFactory.NewRoot();
          var receipt = await _sendAsyncInternalAsync<TEvent>(eventData, MessageContext.Create(cascade));
          receipts.Add(receipt);
        }

        // ALWAYS queue for outbox delivery (cross-service propagation)
        var outboxCascade = _cascadeContextFactory.NewRoot();
        outboxMessages.Add((eventData, eventType, MessageContext.Create(outboxCascade)));
      }

      // Flush outbox batch and collect receipts for non-local messages
      await _flushOutboxBatchAndCollectReceiptsAsync(outboxMessages, hasLocalReceptor, receipts);

      return receipts;
    } finally {
      sw.Stop();
      _dispatcherMetrics?.SendManyDuration.Record(sw.Elapsed.TotalMilliseconds);
    }
  }

  /// <summary>
  /// Publishes multiple events with event routing.
  /// For AOT compatibility, use the generic overload PublishManyAsync&lt;TEvent&gt;.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherOutboxTests.cs:PublishManyAsync_NonGeneric_QueuesAllEventsWithEventRoutingAsync</tests>
  /// <docs>fundamentals/dispatcher/dispatcher#publishmanyasync</docs>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public async Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync(IEnumerable<object> events) {
    ArgumentNullException.ThrowIfNull(events);

    var sw = Stopwatch.StartNew();
    try {
      var eventList = events.ToList();
      _dispatcherMetrics?.SendManyBatchSize.Record(eventList.Count);
      var receipts = new List<IDeliveryReceipt>();

      // All events go to outbox for cross-service delivery.
      // Events with local receptors are ALSO processed locally.
      var outboxMessages = new List<(object message, Type messageType, IMessageContext context)>();
      var hasLocalReceptor = new List<bool>();

      foreach (var eventData in eventList) {
        var eventType = eventData.GetType();
        var invoker = GetReceptorInvoker<object>(eventData, eventType);
        var isLocal = invoker != null;
        hasLocalReceptor.Add(isLocal);

        if (isLocal) {
          // Has local receptor - process locally (Delivered receipt)
          var receipt = await SendAsync(eventData);
          receipts.Add(receipt);
        }

        // ALWAYS queue for outbox delivery (cross-service propagation)
        var cascade = _cascadeContextFactory.NewRoot();
        outboxMessages.Add((eventData, eventType, MessageContext.Create(cascade)));
      }

      // Flush outbox batch and collect receipts for non-local messages
      await _flushOutboxBatchAndCollectReceiptsAsync(outboxMessages, hasLocalReceptor, receipts);

      return receipts;
    } finally {
      sw.Stop();
      _dispatcherMetrics?.SendManyDuration.Record(sw.Elapsed.TotalMilliseconds);
    }
  }

  /// <summary>
  /// Sends multiple typed messages to local receptors ONLY (no outbox delivery).
  /// Messages are processed in-process via strongly-typed delegates (AOT-compatible).
  /// Throws <see cref="ReceptorNotFoundException"/> if any message has no local receptor.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherOutboxTests.cs:LocalSendManyAsync_Generic_WithLocalReceptor_DoesNotPublishToOutboxAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherOutboxTests.cs:LocalSendManyAsync_Generic_ProcessesAllMessagesLocallyAsync</tests>
  /// <docs>fundamentals/dispatcher/dispatcher#localsendmanyasync</docs>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public async ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull {
    ArgumentNullException.ThrowIfNull(messages);

    var messageList = messages.ToList();
    var receipts = new List<IDeliveryReceipt>();
    var messageType = typeof(TMessage);

    foreach (var message in messageList) {
      _ensureReceptorExists(message, messageType);
      var cascade = _cascadeContextFactory.NewRoot();
      var receipt = await _sendAsyncInternalAsync<TMessage>(message, MessageContext.Create(cascade));
      receipts.Add(receipt);
    }

    return receipts;
  }

  /// <summary>
  /// Sends multiple messages to local receptors ONLY (no outbox delivery).
  /// For AOT compatibility, use the generic overload LocalSendManyAsync&lt;TMessage&gt;.
  /// Throws <see cref="ReceptorNotFoundException"/> if any message has no local receptor.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherOutboxTests.cs:LocalSendManyAsync_NonGeneric_WithLocalReceptor_DoesNotPublishToOutboxAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherOutboxTests.cs:LocalSendManyAsync_NonGeneric_ProcessesAllMessagesLocallyAsync</tests>
  /// <docs>fundamentals/dispatcher/dispatcher#localsendmanyasync</docs>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public async ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync(IEnumerable<object> messages) {
    ArgumentNullException.ThrowIfNull(messages);

    var messageList = messages.ToList();
    var receipts = new List<IDeliveryReceipt>();

    foreach (var message in messageList) {
      _ensureReceptorExists(message, message.GetType());
      var receipt = await SendAsync(message);
      receipts.Add(receipt);
    }

    return receipts;
  }

  private void _ensureReceptorExists(object message, Type messageType) {
    var invoker = GetReceptorInvoker<object>(message, messageType);
    if (invoker == null) {
      throw new ReceptorNotFoundException(messageType);
    }
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
  // DEFERRED EVENT CHANNEL
  // ========================================

  /// <summary>
  /// Defers an event to the in-memory channel for next lifecycle loop.
  /// The work coordinator will drain and write to outbox in that transaction.
  /// </summary>
  /// <docs>fundamentals/dispatcher/dispatcher#deferred-publishing</docs>
  /// <tests>Whizbang.Core.Tests/Messaging/DeferredDispatchTests.cs</tests>
  private async Task _deferEventToChannelAsync<TEvent>(
    TEvent eventData,
    Type eventType,
    MessageId messageId,
    IMessageEnvelope? sourceEnvelope,
    bool eventStoreOnly,
    IDeferredOutboxChannel deferredChannel) {

    // 1. Resolve destination topic
    string? destination = eventStoreOnly ? null : _resolveEventTopic(eventType);

    // 2. Create MessageEnvelope wrapping the event
    var envelope = new MessageEnvelope<TEvent> {
      MessageId = messageId,
      Payload = eventData,
      Hops = []
    };

    // 3. Extract aggregate ID and add to hop metadata
    var hopMetadata = _createHopMetadata(eventData!, eventType);

    // 4. Add hop indicating message is being deferred
    var propagatedScope3 = ScopeDelta.FromSecurityContext(CascadeContext.GetSecurityFromAmbient());
    var sourceScope3 = sourceEnvelope?.GetCurrentScope() is { } sc3
      ? ScopeDelta.FromSecurityContext(new SecurityContext { TenantId = sc3.Scope?.TenantId, UserId = sc3.Scope?.UserId })
      : null;
    var finalScope3 = propagatedScope3 ?? sourceScope3;

    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _instanceProvider.ToInfo(),
      Topic = destination ?? "(event-store)",
      Timestamp = DateTimeOffset.UtcNow,
      Metadata = hopMetadata,
      Scope = finalScope3,
      TraceParent = System.Diagnostics.Activity.Current?.Id
    };
    envelope.AddHop(hop);

    // 5. Serialize to OutboxMessage
    var outboxMessage = _serializeToNewOutboxMessage(envelope, eventData!, eventType, destination);

    // 6. Queue to deferred channel (NOT direct DB write)
    await deferredChannel.QueueAsync(outboxMessage);
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
        "BUG IN DISPATCHER: _serializeToNewOutboxMessage called with TMessage=JsonElement. " +
        $"MessageId: {envelope.MessageId}. " +
        $"Envelope type: {envelope.GetType().FullName}. " +
        $"Payload type: {(payload?.GetType().FullName ?? "null")}. " +
        $"PayloadType parameter: {payloadType.FullName}. " +
        "This indicates Dispatcher is being passed a MessageEnvelope<JsonElement> instead of a strongly-typed envelope.");
    }

    // Extract stream_id: try aggregate ID from first hop, fall back to message ID
    var streamId = _extractStreamId(envelope);

    // Guard: fail-fast if StreamId is Guid.Empty (indicates missing [GenerateStreamId] or unpopulated StreamId)
    if (payload is IEvent) {
      StreamIdGuard.ThrowIfEmpty(streamId, envelope.MessageId.Value, "Dispatcher.Outbox", payload.GetType().FullName ?? payload.GetType().Name);
    }

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
        "The serializer defensive checks should have caught this!");
    }

    var outboxMessage = new OutboxMessage {
      MessageId = envelope.MessageId.Value,
      Destination = destination,
      Envelope = serialized.JsonEnvelope,
      Metadata = new EnvelopeMetadata {
        MessageId = envelope.MessageId,
        Hops = envelope.Hops?.ToList() ?? []
      },
      EnvelopeType = serialized.EnvelopeType,
      StreamId = streamId,
      IsEvent = payload is IEvent,
      Scope = _extractScope(envelope),
      MessageType = serialized.MessageType
    };

    // FINAL CHECK: Throw if ANY type string contains JsonElement
    if (outboxMessage.MessageType.Contains("JsonElement", StringComparison.OrdinalIgnoreCase) ||
        outboxMessage.EnvelopeType.Contains("JsonElement", StringComparison.OrdinalIgnoreCase)) {
      throw new InvalidOperationException(
        "FINAL CHECK FAILED: OutboxMessage contains JsonElement in type metadata. " +
        $"MessageId={outboxMessage.MessageId}, " +
        $"MessageType={outboxMessage.MessageType}, " +
        $"EnvelopeType={outboxMessage.EnvelopeType}, " +
        $"TMessage={typeof(TMessage).FullName}, " +
        $"PayloadType={payloadType.FullName}, " +
        $"Payload runtime type={payload?.GetType().FullName ?? "null"}. " +
        "This means either: (1) Envelope parameter was MessageEnvelope<JsonElement>, " +
        "(2) Payload was JsonElement, or (3) PayloadType parameter was typeof(JsonElement). " +
        "All these cases should have been caught by earlier checks!");
    }

    return outboxMessage;
  }

  /// <summary>
  /// Extracts stream_id from envelope for stream-based ordering.
  /// Tries to get stream ID from first hop metadata, falls back to message ID.
  /// </summary>
  private static Guid _extractStreamId(IMessageEnvelope envelope) {
    // Check first hop for stream ID (stored as "AggregateId" for backward compatibility)
    // Defensive: Handle null Hops gracefully
    var firstHop = envelope.Hops?.FirstOrDefault();
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
  /// Auto-generates StreamId for messages with [GenerateStreamId] attribute during envelope creation.
  /// This ensures commands (and any IMessage) get their StreamId populated before dispatch,
  /// using the same plumbing as [Populate*] attributes.
  /// Events cascaded from receptors are handled separately in the cascade path.
  /// </summary>
  private void _autoGenerateStreamIdIfNeeded(object message, Type messageType) {
    if (_streamIdExtractor is null) {
      return;
    }

    var (shouldGenerate, onlyIfEmpty) = _streamIdExtractor.GetGenerationPolicy(message);
    if (!shouldGenerate) {
      return;
    }

    var streamId = _streamIdExtractor.ExtractStreamId(message, messageType) ?? Guid.Empty;
    if (!onlyIfEmpty || streamId == Guid.Empty) {
      var newStreamId = ValueObjects.TrackedGuid.NewMedo();

      // Try IHasStreamId first (direct interface), then fall back to generated setter
      if (message is IHasStreamId hasStreamId) {
        hasStreamId.StreamId = newStreamId;
      } else {
        _streamIdExtractor.SetStreamId(message, newStreamId);
      }

      Log.StreamIdAutoGenerated(CascadeLogger, newStreamId, messageType.Name, onlyIfEmpty);
    }
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
    var jsonString = "\"" + streamId.Value + "\"";
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
  /// The delegate accepts an object, source envelope, and cancellation token, and internally casts to the correct event type.
  /// AOT-compatible because the generated code knows all event types at compile time.
  /// </summary>
  /// <param name="eventType">The runtime type of the event (e.g., typeof(OrderCreatedEvent))</param>
  /// <returns>
  /// A delegate that publishes the event to all registered receptors with proper security context propagation.
  /// The delegate signature: Func&lt;object, IMessageEnvelope?, CancellationToken, Task&gt;.
  /// Returns null if no receptors registered for this event type.
  /// </returns>
  /// <remarks>
  /// The generated delegate:
  /// <list type="number">
  /// <item>Creates a new DI scope for receptor resolution</item>
  /// <item>Establishes security context from source envelope (if provided)</item>
  /// <item>Resolves all receptors for the event type</item>
  /// <item>Invokes each receptor with the event</item>
  /// <item>Disposes the scope</item>
  /// </list>
  /// </remarks>
  /// <docs>fundamentals/dispatcher/dispatcher#cascade-security-context</docs>
  /// <docs>fundamentals/security/message-security#security-context-in-event-cascades</docs>
  protected abstract Func<object, IMessageEnvelope?, CancellationToken, Task>? GetUntypedReceptorPublisher(Type eventType);

  /// <summary>
  /// Implemented by generated code - returns a sync delegate for invoking a sync receptor.
  /// The delegate encapsulates the receptor lookup and invocation with zero reflection.
  /// Returns null if no sync receptor found (falls back to async).
  /// </summary>
  /// <docs>fundamentals/dispatcher/dispatcher#synchronous-invocation</docs>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherSyncTests.cs:LocalInvokeAsync_SyncReceptor_InvokesSynchronouslyAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherSyncTests.cs:LocalInvokeAsync_SyncReceptor_ReturnsCompletedValueTaskAsync</tests>
  protected abstract SyncReceptorInvoker<TResult>? GetSyncReceptorInvoker<TResult>(object message, Type messageType);

  /// <summary>
  /// Implemented by generated code - returns a void sync delegate for invoking a sync receptor.
  /// The delegate encapsulates the receptor lookup and invocation with zero reflection.
  /// Returns null if no void sync receptor found.
  /// </summary>
  /// <docs>fundamentals/dispatcher/dispatcher#synchronous-invocation</docs>
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
  /// <docs>fundamentals/dispatcher/dispatcher#void-cascade</docs>
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
  /// <docs>fundamentals/dispatcher/dispatcher#routed-message-cascading</docs>
  /// <tests>tests/Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs</tests>
  protected abstract Dispatch.DispatchMode? GetReceptorDefaultRouting(Type messageType);

  /// <summary>
  /// AOT-compatible logging for security-related dispatcher events.
  /// Uses compile-time LoggerMessage source generator for zero-allocation, high-performance logging.
  /// </summary>
  private static partial class Log {
    [LoggerMessage(
      EventId = 1,
      Level = LogLevel.Warning,
      Message = "Security context is being propagated with empty GUID (00000000-0000-0000-0000-000000000000) as UserId. " +
                "This may indicate the original request didn't capture the user's identity correctly. TenantId: {TenantId}",
      SkipEnabledCheck = true)]
    public static partial void EmptyGuidUserIdPropagated(ILogger logger, string? tenantId);

    [LoggerMessage(
      EventId = 2,
      Level = LogLevel.Warning,
      Message = "Explicit security context (AsSystem/RunAs) created with empty GUID (00000000-0000-0000-0000-000000000000) as UserId. " +
                "This may indicate the originating request didn't have proper user context. ContextType: {ContextType}, TenantId: {TenantId}",
      SkipEnabledCheck = true)]
    public static partial void EmptyGuidUserIdInExplicitContext(ILogger logger, string? contextType, string? tenantId);

    [LoggerMessage(
      EventId = 3,
      Level = LogLevel.Debug,
      Message = "[STREAM_ID] Auto-generated StreamId={StreamId} for MessageType={MessageType} (OnlyIfEmpty={OnlyIfEmpty})")]
    public static partial void StreamIdAutoGenerated(ILogger logger, Guid streamId, string messageType, bool onlyIfEmpty);

    [LoggerMessage(
      EventId = 4,
      Level = LogLevel.Debug,
      Message = "[STREAM_ID] Propagated StreamId={StreamId} from source {SourceType} to cascaded {EventType}")]
    public static partial void StreamIdPropagatedFromSource(ILogger logger, Guid streamId, string sourceType, string eventType);

    [LoggerMessage(
      EventId = 5,
      Level = LogLevel.Warning,
      Message = "[DISPATCHER] SendManyAsync: No IWorkCoordinatorStrategy registered — " +
                "outbox delivery skipped. All {MessageCount} messages were handled by local receptors.")]
    public static partial void OutboxDeliverySkipped(ILogger logger, int messageCount);
  }
}
