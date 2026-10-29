// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
using Whizbang.Core.DependencyInjection;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Dispatch.Policy;
using Whizbang.Core.Lenses;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Minting;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Perspectives.Sync;
using Whizbang.Core.Routing;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Exceptions;
using Whizbang.Core.Tags;
using Whizbang.Core.Tracing;
using Whizbang.Core.Transports;
using Whizbang.Core.Validation;
using Whizbang.Core.ValueObjects;
using Whizbang.Core.Workers;

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
/// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherSyncTests.cs:LocalInvokeAsync_SyncReceptor_InvokesSynchronouslyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherSyncTests.cs:LocalInvokeAsync_VoidSyncReceptor_ExecutesSynchronouslyAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherCoverageSweepSyncRpcTests.cs:LocalInvokeAsync_GenericVoid_SyncAttributeWithSyncInvoker_AwaitsPerspectiveAsync</tests>
public delegate void VoidSyncReceptorInvoker(object message);

/// <summary>
/// Base dispatcher class with core logic. The source generator creates a derived class
/// that implements the abstract lookup methods, returning strongly-typed delegates.
/// This achieves zero-reflection while keeping functional logic in the base class.
/// </summary>
/// <tests>tests/Whizbang.Core.Component.Tests/Dispatcher/DispatcherTests.cs</tests>
/// <tests>tests/Whizbang.Core.Integration.Tests/DispatcherReceptorIntegrationTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/ColdBootJourneyE2ETests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "Parameters 'jsonOptions' and 'receptorInvoker' retained for backward compatibility with generated code")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "S1172:Unused method parameters should be removed", Justification = "Parameters 'jsonOptions' and 'receptorInvoker' retained for backward compatibility with generated code")]
#pragma warning disable CS9113, S107 // CS9113: primary ctor param unread (generated-code backcompat); S107: DI-injection ctor is idiomatic with many params
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
#pragma warning restore S107, CS9113

  /// <summary>
  /// Groups caller location parameters that are always passed together from [CallerMemberName/FilePath/LineNumber].
  /// </summary>
  private readonly record struct CallerLocation(
    string MemberName,
    string FilePath,
    int LineNumber);

  // Increment 6, the write seam: a dispatch needs the event store and the outbox, which Migrate
  // provides — so dispatch refuses while the schema gate is closed. Resolved lazily from the
  // service provider (never a ctor param: generated dispatcher subclasses chain base(...)
  // positionally, and test fixtures without a gate stay ungated). Volatile-cached after first look.
  private ISchemaReadyGate? _schemaReadyGateCache;
  private bool _schemaReadyGateResolved;

  /// <summary>
  /// Refuses a dispatch whose caller has no security context, before any handler runs or anything is
  /// written, when <see cref="Security.MessageSecurityOptions.RequireContextForLocalDispatch"/> is set
  /// (#1419). Off by default: an in-process dispatch is trusted like a mediator call. Control-plane
  /// messages, system events and the types the application exempts are never refused, as on receive.
  /// </summary>
  /// <param name="message">The message being dispatched; a null message passes, for its entry to refuse next.</param>
  /// <exception cref="SecurityContextRequiredException">The caller has no context and one is required.</exception>
  private void _throwIfLocalDispatchNeedsContext<TMessage>(TMessage message) {
    if (!_localDispatchRequiresContext || message is null || ScopeContextAccessor.CurrentContext is not null) {
      return;
    }
    _throwIfLocalDispatchTypeNeedsContext(message.GetType());
  }

  /// <summary>The check of <see cref="_throwIfLocalDispatchNeedsContext{TMessage}"/>, for a caller that holds only the type.</summary>
  private void _throwIfLocalDispatchTypeNeedsContext(Type messageType) {
    if (!_localDispatchRequiresContext || ScopeContextAccessor.CurrentContext is not null) {
      return;
    }
    if (typeof(IControlPlaneMessage).IsAssignableFrom(messageType)
        || typeof(SystemEvents.ISystemEvent).IsAssignableFrom(messageType)
        || _localDispatchExemptTypes.Contains(messageType)) {
      return;
    }
    throw new SecurityContextRequiredException(messageType);
  }

  private void _throwIfSchemaNotReady() {
    if (!_schemaReadyGateResolved) {
      try {
        _schemaReadyGateCache = (ISchemaReadyGate?)serviceProvider.GetService(typeof(ISchemaReadyGate));
        _schemaReadyGateResolved = true;
      } catch (ObjectDisposedException) {
        // Shutdown teardown: the dispatcher promises graceful handling of a disposed provider,
        // and refusing a dispatch that is about to fail on disposal anyway adds nothing.
        return;
      }
    }
    if (_schemaReadyGateCache is { IsReady: false }) {
      throw new WhizbangNotReadyException(
        "dispatch refused: the schema is not ready (Migrate has not completed). Writes need the "
        + "event store and outbox the migration provides; they resume when it does.");
    }
  }

  // Issue #491: receptor discovery is source-only, so this dispatcher's generated tables cover
  // ONLY the assembly that generated it — and every Whizbang-compiled assembly registers a
  // dispatcher, of which DI resolves the last. Each assembly's tables are therefore ALSO
  // registered as an IReceptorLookup; when the own tables answer null, the FOREIGN lookups are
  // consulted — first match for send-style invocations, fan-out to all for publishes, because an
  // event's receptors may live in several assemblies at once. Lazily resolved for the same
  // reasons as the schema gate above. Instances of this dispatcher's own generated type are
  // excluded so a publish never double-delivers to its own receptors.
  // Null until resolved. One reference, written once the array is complete, so a reader on another
  // thread sees either nothing (and resolves again, harmlessly) or the finished array.
  private IReceptorLookup[]? _foreignLookupsCache;

  private IReceptorLookup[] _foreignLookups() {
    if (_foreignLookupsCache is { } cached) {
      return cached;
    }
    try {
      var own = GetType();
      var all = (System.Collections.Generic.IEnumerable<IReceptorLookup>?)
        serviceProvider.GetService(typeof(System.Collections.Generic.IEnumerable<IReceptorLookup>));
      IReceptorLookup[] resolved = all is null
        ? []
        : [.. all.Where(lookup => lookup.GetType() != own)];
      _foreignLookupsCache = resolved;
      return resolved;
    } catch (ObjectDisposedException) {
      return [];
    }
  }

  private ReceptorInvoker<TResult>? _lookupReceptorInvoker<TResult>(object message, Type messageType) {
    var own = GetReceptorInvoker<TResult>(message, messageType);
    if (own is not null) {
      return own;
    }
    foreach (var lookup in _foreignLookups()) {
      if (lookup.LookupReceptorInvoker<TResult>(message, messageType) is { } foreign) {
        return foreign;
      }
    }
    return null;
  }

  private VoidReceptorInvoker? _lookupVoidReceptorInvoker(object message, Type messageType) {
    // Two or more local receptors: every one runs, in priority order (#1412)
    if (_runnerAsVoid(messageType) is { } everyReceptor) {
      return everyReceptor;
    }
    var own = GetVoidReceptorInvoker(message, messageType);
    if (own is not null) {
      return own;
    }
    foreach (var lookup in _foreignLookups()) {
      if (lookup.LookupVoidReceptorInvoker(message, messageType) is { } foreign) {
        return foreign;
      }
    }
    return null;
  }

  private SyncReceptorInvoker<TResult>? _lookupSyncReceptorInvoker<TResult>(object message, Type messageType) {
    var own = GetSyncReceptorInvoker<TResult>(message, messageType);
    if (own is not null) {
      return own;
    }
    foreach (var lookup in _foreignLookups()) {
      if (lookup.LookupSyncReceptorInvoker<TResult>(message, messageType) is { } foreign) {
        return foreign;
      }
    }
    return null;
  }

  private VoidSyncReceptorInvoker? _lookupVoidSyncReceptorInvoker(object message, Type messageType) {
    var own = GetVoidSyncReceptorInvoker(message, messageType);
    if (own is not null) {
      return own;
    }
    foreach (var lookup in _foreignLookups()) {
      if (lookup.LookupVoidSyncReceptorInvoker(message, messageType) is { } foreign) {
        return foreign;
      }
    }
    return null;
  }

  /// <summary>
  /// Resolves the local receptor SendAsync and SendManyAsync invoke, whatever its shape: async or
  /// sync, with or without a result. A send only runs the receptor and cascades its result, so a
  /// void or sync receptor is adapted to the async invoker shape (a void receptor yields a null
  /// result, which the cascade skips). Consulting only the result-returning lookup made a send
  /// treat a void-receptor command as unhandled: dropped when owned, sent through the outbox when not.
  /// </summary>
  /// <docs>fundamentals/dispatcher/routing#owned-domain-routing</docs>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherCoverageSweepRoutedCascadeTests.cs:SendAsync_VoidReceptor_InvokesLocally_AndSendsOnUnlessOwnedAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherCoverageSweepRoutedCascadeTests.cs:SendAsync_SyncReceptor_InvokesLocally_AndSendsOnUnlessOwnedAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherCoverageSweepRoutedCascadeTests.cs:SendAsync_VoidSyncReceptor_InvokesLocally_AndSendsOnUnlessOwnedAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherCoverageSweepRoutedCascadeTests.cs:SendManyAsync_VoidReceptor_InvokesLocallyAsync</tests>
  private ReceptorInvoker<object>? _lookupSendInvoker(object message, Type messageType) {
    // Two or more local receptors: every one runs, in priority order (#1412)
    if (_runnerAsResult<object>(messageType) is { } everyReceptor) {
      return everyReceptor;
    }
    if (_lookupReceptorInvoker<object>(message, messageType) is { } invoker) {
      return invoker;
    }
    if (_lookupVoidReceptorInvoker(message, messageType) is { } voidInvoker) {
      return async msg => {
        await voidInvoker(msg);
        return null!;
      };
    }
    if (_lookupSyncReceptorInvoker<object>(message, messageType) is { } syncInvoker) {
      return msg => ValueTask.FromResult(syncInvoker(msg));
    }
    if (_lookupVoidSyncReceptorInvoker(message, messageType) is { } voidSyncInvoker) {
      return msg => {
        voidSyncInvoker(msg);
        return ValueTask.FromResult<object>(null!);
      };
    }
    if (_lookupRuntimeLocalInvoker(messageType) is { } runtimeInvoker) {
      return async msg => (await runtimeInvoker(msg))!;
    }
    return null;
  }

  /// <summary>
  /// The local handler a RUNTIME registration provides: a receptor registered into
  /// <see cref="IReceptorRegistry"/> at <see cref="LifecycleStage.LocalImmediateInline"/>, the stage a generated
  /// receptor without <c>[FireAt]</c> handles local dispatch at (#1392). The framework registers its own receptors for its own
  /// message types this way, because the generator only sees the consuming assembly's syntax. Asked
  /// only after every generated lookup misses, so a compile-time receptor always wins; consulting the
  /// generated tables alone left these commands unhandled on every local path (#1385).
  /// </summary>
  /// <remarks>
  /// The registry's delegates take an envelope, which the local-dispatch entries never read (the
  /// runtime adapter ignores it, and so does the generated one); the dispatch path that calls this
  /// keeps the real envelope for tracing, tags and lifecycle stages. A fresh scope is created per
  /// invocation, as the generated invokers do, with the ambient message context established in it.
  /// </remarks>
  /// <docs>fundamentals/dispatcher/routing#runtime-registered-receptors</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Dispatcher/RuntimeReceptorLocalDispatchComponentTests.cs</tests>
  private Func<object, ValueTask<object?>>? _lookupRuntimeLocalInvoker(Type messageType) {
    if (_receptorRegistry is null) {
      return null;
    }
    // Only a receptor registered at runtime counts: the registry also lists source-generated receptors,
    // which the generated lookups already resolved (and which may not even be registered in the container)
    var receptor = _firstRuntimeReceptor(_receptorRegistry.GetReceptorsFor(messageType, LifecycleStage.LocalImmediateInline));
    if (receptor is null) {
      return null;
    }
    return async message => {
      await using var scope = _scopeFactory.CreateAsyncScope();
      SecurityContextHelper.EstablishMessageContextForCascade(scope.ServiceProvider);
      var envelope = new MessageEnvelope<object> {
        MessageId = MessageId.New(),
        Payload = message,
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
      };
      return await receptor.InvokeAsync(scope.ServiceProvider, message, envelope, receptor.CallerInfo, CancellationToken.None).ConfigureAwait(false);
    };
  }

  /// <summary>
  /// <see cref="_lookupRuntimeLocalInvoker"/> shaped for a typed local invoke. A runtime receptor
  /// registered without a response type answers null.
  /// </summary>
  private ReceptorInvoker<TResult>? _lookupRuntimeLocalInvoker<TResult>(Type messageType) =>
    _lookupRuntimeLocalInvoker(messageType) is { } runtimeInvoker
      ? async msg => await runtimeInvoker(msg) switch {
        null => default!,
        TResult typed => typed,
        var other => throw new ReceptorResultMismatchException(other, typeof(TResult))
      }
      : null;

  /// <summary>
  /// The invoker a result-returning local call runs, the same for every LocalInvoke overload: the async
  /// result receptor, else the sync one, else a runtime registration. Each hands back a result that is
  /// not the requested type whole, as a <see cref="ReceptorResultMismatchException"/>, for
  /// <see cref="_invokeAndCascadeAsync"/> to extract the response from (#1410 follow-up).
  /// </summary>
  private ReceptorInvoker<TResult>? _lookupResultInvoker<TResult>(object message, Type messageType) {
    // Two or more local receptors, one of which answers with TResult: every one runs, in priority order,
    // and the earliest answer is returned (#1412). Without an answering receptor the lookups below decide.
    if ((GetReceptorInvoker<TResult>(message, messageType) is not null || GetSyncReceptorInvoker<TResult>(message, messageType) is not null)
        && _runnerAsResult<TResult>(messageType) is { } everyReceptor) {
      return everyReceptor;
    }
    if (_lookupReceptorInvoker<TResult>(message, messageType) is { } asyncInvoker) {
      return asyncInvoker;
    }
    if (_lookupSyncReceptorInvoker<TResult>(message, messageType) is { } syncInvoker) {
      return msg => new ValueTask<TResult>(syncInvoker(msg));
    }
    if (_lookupRuntimeLocalInvoker<TResult>(messageType) is { } runtimeInvoker) {
      return runtimeInvoker;
    }
    // Last, any result-returning receptor (a lookup that offers only the type-erased invoker)
    if (_lookupReceptorInvokerAny(message, messageType) is { } anyInvoker) {
      return async msg => await anyInvoker(msg) switch {
        null => throw new InvalidOperationException(
          $"Could not extract {typeof(TResult).Name} from receptor result of type null. " +
          $"The receptor for {messageType.Name} does not return a value of type {typeof(TResult).Name}."),
        TResult typed => typed,
        var other => throw new ReceptorResultMismatchException(other, typeof(TResult))
      };
    }
    return null;
  }

  /// <summary>
  /// Runs a result-returning receptor once and returns the caller's response, cascading what else it
  /// returned. A result that is not the requested type (a tuple holding it) is not run again: the
  /// response is extracted from it and the rest cascades with the invoke's envelope and token.
  /// </summary>
  private async ValueTask<TResult> _invokeAndCascadeAsync<TResult>(ReceptorInvoker<TResult> invoker, object message, Type messageType,
      IMessageEnvelope envelope, CancellationToken cancellationToken) {
    TResult result;
    try {
      result = await invoker(message);
    } catch (ReceptorResultMismatchException mismatch) {
      if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
        Log.ExtractingResponse(CascadeLogger, messageType.Name, mismatch.Result.GetType().Name, typeof(TResult).Name);
      }
      if (!Internal.ResponseExtractor.TryExtractResponse<TResult>(mismatch.Result, out var response)) {
        throw new InvalidOperationException(
          $"Could not extract {typeof(TResult).Name} from receptor result of type {mismatch.Result.GetType().Name}. " +
          $"The receptor for {messageType.Name} does not return a value of type {typeof(TResult).Name}.", mismatch);
      }
      await _cascadeEventsFromResultAsync(mismatch.Result, messageType, envelope, excludedResponse: response, cancellationToken);
      return response!;
    }
    await _cascadeEventsFromResultAsync(result, messageType, sourceEnvelope: envelope, cancellationToken: cancellationToken);
    return result;
  }

  /// <summary>Whether a message of this type is a composite this service owns, so it is expanded here.</summary>
  private bool _expandsHere(Type messageType) =>
    typeof(ICompositeEvent).IsAssignableFrom(messageType) && _isOwnedNamespace(messageType.Namespace);

  /// <summary>Expands an owned composite here, as part of its local run; anything else is left alone.</summary>
  private Task _expandIfOwnedCompositeAsync(object message, Type messageType, MessageId? copyId) =>
    _expandsHere(messageType)
      ? FanOutCompositeLocallyAtPublishAsync(message, messageType, copyId ?? MessageId.New())
      : Task.CompletedTask;

  private static ReceptorInfo? _firstRuntimeReceptor(IReadOnlyList<ReceptorInfo> registered) =>
    registered.FirstOrDefault(receptor => receptor.RuntimeInstance is not null);

  /// <summary>
  /// True when publishing <paramref name="eventType"/> has any local receptor to run here: a generated
  /// one (this assembly or another) or one registered at runtime at a local stage. Exact for every kind:
  /// a generated publisher exists only for a message with a receptor firing on local dispatch, so an
  /// event nothing here handles is not treated as handled and its copy does not claim it was (#1410 follow-up).
  /// </summary>
  private bool _hasLocalPublishReceptors(Type eventType) {
    // An owned composite is expanded here, so it is handled here: the decision then runs its fan-out now, or at
    // its scheduled time through the copy that comes back, on every entry
    if (GetUntypedReceptorPublisher(eventType) is not null || _expandsHere(eventType)) {
      return true;
    }
    return _foreignLookups().Any(lookup => lookup.LookupUntypedReceptorPublisher(eventType) is not null)
      || _runtimeLocalReceptorsFor(eventType).Count > 0;
  }


  /// <summary>
  /// The receptors registered at runtime (<see cref="IReceptorRegistry.Register"/>) at a local stage,
  /// each once even when registered at both local stages.
  /// </summary>
  private List<ReceptorInfo> _runtimeLocalReceptorsFor(Type messageType) {
    var runtime = new List<ReceptorInfo>();
    if (_receptorRegistry is null) {
      return runtime;
    }
    foreach (var stage in _localStages) {
      foreach (var receptor in _receptorRegistry.GetReceptorsFor(messageType, stage)) {
        if (receptor.RuntimeInstance is { } instance && !runtime.Exists(r => ReferenceEquals(r.RuntimeInstance, instance))) {
          runtime.Add(receptor);
        }
      }
    }
    return runtime;
  }

  // The stage a local handler is registered at. LocalImmediateDetached is a background hook, started after the
  // handler, never a handler the caller waits for (#1392).
  private static readonly LifecycleStage[] _localStages = [LifecycleStage.LocalImmediateInline];

  /// <summary>
  /// Runs every receptor registered at runtime at a local stage for a published or cascaded message
  /// (#1399), each in its own scope with the cascade's security context.
  /// </summary>
  private Task _publishToRuntimeLocalReceptorsAsync(object message, Type messageType, CancellationToken cancellationToken) =>
    _invokeRuntimeLocalReceptorsAsync(_runtimeLocalReceptorsFor(messageType), message, messageType, cancellationToken);

  private async Task _invokeRuntimeLocalReceptorsAsync(List<ReceptorInfo> receptors, object message, Type messageType, CancellationToken cancellationToken) {
    foreach (var receptor in receptors) {
      await using var scope = _scopeFactory.CreateAsyncScope();
      SecurityContextHelper.EstablishMessageContextForCascade(scope.ServiceProvider);
      var envelope = new MessageEnvelope<object> {
        MessageId = MessageId.New(),
        Payload = message,
        Hops = [],
        DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
      };
      var result = await receptor.InvokeAsync(scope.ServiceProvider, message, envelope, receptor.CallerInfo, cancellationToken).ConfigureAwait(false);
      // A result-returning runtime receptor's result cascades, as it does on a send (#1410 follow-up)
      await CascadeLocalResultAsync(result, message, messageType, envelope, cancellationToken).ConfigureAwait(false);
    }
  }

  private static DispatchPolicyEngine _resolvePolicyEngine(IServiceProvider serviceProvider) {
    var registry = serviceProvider.GetService<IDispatchPolicyRegistry>();
    // GetService, not GetServices: a host's minimal provider may not resolve enumerables at all
    var hooks = serviceProvider.GetService<IEnumerable<IDispatchPolicyHook>>()?.ToArray() ?? [];
    return registry is null && hooks.Length == 0
      ? DispatchPolicyEngine.Default
      : new DispatchPolicyEngine(registry ?? new DispatchPolicyRegistry(), new DispatchPolicyHookChain(hooks));
  }

  /// <summary>
  /// What the policy knows about a message: its kind from the interfaces it implements (a type that
  /// is neither a command nor an event takes <paramref name="unmarkedKind"/>), and the cached
  /// namespace traits of its type.
  /// </summary>
  private MessageTraits _traitsOf(object message, Type messageType, MessageTraits unmarkedKind) {
    var kind = message switch {
      IEvent => MessageTraits.Event,
      ICommand => MessageTraits.Command,
      // A composite carries events, so every policy treats it as one, whatever the call's default
      ICompositeEvent => MessageTraits.Event,
      _ => unmarkedKind,
    };
    // Only an event is stored: a store-only route for anything else is refused rather than written nowhere
    if (message is not IEvent) {
      kind |= MessageTraits.NotStorable;
    }
    if (message is ICompositeEvent) {
      kind |= MessageTraits.Composite;
    }
    return kind | _namespaceTraits.GetOrAdd(messageType, static (type, self) => self._computeNamespaceTraits(type), this);
  }

  private MessageTraits _computeNamespaceTraits(Type messageType) {
    var traits = MessageTraits.None;
    if (MessageKindDetector.IsFrameworkSystemType(messageType)) {
      traits |= MessageTraits.SystemCommand;
    }
    if (_isOwnedNamespace(messageType.Namespace)) {
      traits |= MessageTraits.Owned;
    }
    return traits;
  }

  /// <summary>
  /// Asks the dispatch policy what to do with one message. The call site describes only its own
  /// situation; the policy (the call's own, or the one the caller selected) decides.
  /// </summary>
  /// <param name="callPolicy">The policy of the call, for example <see cref="DispatchPolicies.Send"/>.</param>
  /// <param name="message">The message (read for its kind only).</param>
  /// <param name="messageType">The message type.</param>
  /// <param name="hasHandler">Whether this service has a local handler for it.</param>
  /// <param name="options">The caller's options: schedule, overrides and selected policy.</param>
  /// <param name="route">The route the caller chose, or null.</param>
  /// <param name="unmarkedKind">The kind of a message that is neither a command nor an event.</param>
  /// <param name="resultRequired">Whether the caller needs the handler's result.</param>
  [SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "Builds the policy's situation from the facts each call site already holds. The last three are optional and most callers omit them, and a parameter object would be allocated per dispatch on the hot path the policy must not slow down.")]
  private DispatchDecision _decide(
      DispatchPolicy callPolicy,
      object message,
      Type messageType,
      bool hasHandler,
      DispatchOptions? options,
      DispatchModes? route = null,
      MessageTraits unmarkedKind = MessageTraits.Command,
      bool resultRequired = false) {
    var policy = _policyEngine.Resolve(callPolicy, options?.Policy);
    var scheduledFor = options?.ScheduledFor;
    var situation = new DispatchSituation(
      messageType,
      _traitsOf(message, messageType, unmarkedKind),
      hasHandler ? LocalHandlers.Generated : LocalHandlers.None,
      route,
      scheduledFor,
      IsScheduledInFuture: scheduledFor is DateTimeOffset at && at > _timeProvider.GetUtcNow(),
      OutboxAvailable: _outboxRegistered,
      options?.Behaviors ?? DispatchBehaviors.None,
      resultRequired);
    return _policyEngine.Decide(policy, in situation);
  }

  /// <summary>
  /// Throws the exception a refused dispatch has always surfaced as: a missing handler or outbox is a
  /// <see cref="ReceptorNotFoundException"/>, a route that goes nowhere or contradictory overrides an
  /// <see cref="ArgumentException"/>, and anything else a <see cref="DispatchRejectedException"/>.
  /// </summary>
  private void _throwIfRejected(in DispatchDecision decision, Type messageType) {
    switch (decision.Reject) {
      case DispatchRejection.None:
        return;
      case DispatchRejection.NoHandler:
        throw new ReceptorNotFoundException(messageType);
      case DispatchRejection.NoOutbox:
        _logMissingWorkCoordinator(messageType);
        throw new ReceptorNotFoundException(messageType);
      case DispatchRejection.NoRoute:
        throw new ArgumentException("Cannot send a RoutedNone (Route.None()) - it has no inner message to dispatch.");
      case DispatchRejection.ConflictingBehaviors:
        throw new ArgumentException(
          $"DispatchBehaviors.ThrowWhenNoHandler and DispatchBehaviors.ContinueWhenNoHandler cannot both be set (message type {messageType.Name}).");
      case DispatchRejection.CommandCannotBeStoreOnly:
        throw new DispatchRejectedException(decision.Reject, messageType,
          $"{messageType.Name} is a command routed to the event store only; commands are not stored. Route it Local, Outbox or Both.");
      case DispatchRejection.NotAnEventCannotBeStoreOnly:
        throw new DispatchRejectedException(decision.Reject, messageType,
          $"{messageType.Name} is not an event and is routed to the event store only; only events are stored. Route it Local, Outbox or Both.");
      case DispatchRejection.CannotScheduleInProcess:
        throw new DispatchRejectedException(decision.Reject, messageType,
          $"{messageType.Name} is scheduled for later but never leaves this service, so nothing can deliver it later. Send it through the outbox or drop the schedule.");
      default:
        throw new DispatchRejectedException(decision.Reject, messageType,
          $"{messageType.Name} needs its handler's result, but the selected dispatch policy does not run the handler during the call.");
    }
  }

  /// <summary>Says why a message that has to reach the outbox could not: no work coordinator is registered.</summary>
  private void _logMissingWorkCoordinator(Type messageType) {
    using var scope = _scopeFactory.CreateScope();
    var logger = scope.ServiceProvider.GetService<ILogger<Dispatcher>>();
#pragma warning disable CA1848 // Use LoggerMessage delegates for performance - acceptable in error path
    logger?.LogWarning(
      "IWorkCoordinatorStrategy not registered - cannot send command to outbox and no local handler found. " +
      "Register IWorkCoordinatorStrategy (ImmediateWorkCoordinatorStrategy, ScopedWorkCoordinatorStrategy, " +
      "or IntervalWorkCoordinatorStrategy) to enable outbox pattern. MessageType: {MessageType}",
      messageType.Name);
#pragma warning restore CA1848
  }

  /// <summary>Rethrows a held local failure with its original stack, once the outbox copy is queued.</summary>
  private static void _rethrowIfFailed(Exception? localFailure) {
    if (localFailure is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(localFailure); }
  }

  /// <summary>
  /// Applies <see cref="DispatchOptions.Timeout"/>: when set, returns a copy of the options whose token
  /// is the caller's linked with a timer on the dispatcher's clock, and the scope that owns both. The
  /// copy flows through the whole call, so the deadline reaches the local run, cascades, the outbox write
  /// and its flush. The token lives in this one place, so a future distributed cancellation links here.
  /// </summary>
  private DispatchOptions _withTimeout(DispatchOptions options, out DispatchTimeoutScope? scope) {
    if (options.Timeout is not TimeSpan timeout) {
      scope = null;
      return options;
    }
    scope = new DispatchTimeoutScope(timeout, _timeProvider, options.CancellationToken);
    return options.CloneWithCancellation(scope.Token);
  }

  /// <summary>The timer and linked token behind a dispatch's <see cref="DispatchOptions.Timeout"/>.</summary>
  private sealed class DispatchTimeoutScope : IDisposable {
    private readonly CancellationTokenSource _timer;
    private readonly CancellationTokenSource _linked;

    public DispatchTimeoutScope(TimeSpan timeout, TimeProvider timeProvider, CancellationToken callerToken) {
      _timer = new CancellationTokenSource(timeout, timeProvider);
      _linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, _timer.Token);
    }

    public CancellationToken Token => _linked.Token;

    public void Dispose() {
      _linked.Dispose();
      _timer.Dispose();
    }
  }

  /// <summary>The receipt of a dispatch the policy let through without running or sending anything.</summary>
  private static DeliveryReceipt _nothingToDoReceipt(Type messageType, IMessageContext context) =>
    DeliveryReceipt.Accepted(MessageId.New(), messageType.Name, context.CorrelationId, context.CausationId);


  /// <summary>
  /// The dispatch mode of an outbox copy. A copy of a message this service already handled locally
  /// carries <see cref="DispatchModes.LocalDispatch"/>, so the sender's PreOutbox stage does not run
  /// that handler a second time.
  /// </summary>
  private static DispatchModes _outboxModeFor(IDeliveryReceipt? handledLocallyReceipt) =>
    handledLocallyReceipt is null ? DispatchModes.Outbox : DispatchModes.Both;

  private Func<object, ValueTask<object?>>? _lookupReceptorInvokerAny(object message, Type messageType) {
    var own = GetReceptorInvokerAny(message, messageType);
    if (own is not null) {
      return own;
    }
    foreach (var lookup in _foreignLookups()) {
      if (lookup.LookupReceptorInvokerAny(message, messageType) is { } foreign) {
        return foreign;
      }
    }
    // A result-returning sync receptor, which the generated any-invoker leaves out: without it a void
    // local invoke of a message only a sync receptor handles found nothing (#1410 follow-up)
    if (_lookupSyncReceptorInvoker<object>(message, messageType) is { } syncInvoker) {
      return msg => ValueTask.FromResult<object?>(syncInvoker(msg));
    }
    return _lookupRuntimeLocalInvoker(messageType);
  }

  /// <summary>
  /// Cascades what a receptor returned when the generated publisher ran it, as a local invoke cascades
  /// its receptor's result (#1410 follow-up). Called by generated code on the publish and cascade paths.
  /// A cascade passes its source envelope, which anchors the result's ids to the cascaded message; a
  /// publish has none, so the handled message gets an envelope of its own here.
  /// </summary>
  /// <param name="result">What the receptor returned; null cascades nothing.</param>
  /// <param name="message">The message the receptor handled.</param>
  /// <param name="messageType">The handled message's type.</param>
  /// <param name="sourceEnvelope">The cascade's source envelope, or null on a publish.</param>
  /// <param name="cancellationToken">Cancels the cascade.</param>
  /// <docs>fundamentals/dispatcher/dispatch-policy#decision-table</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Dispatcher/DispatchExecutorShapeComponentTests.cs</tests>
  protected async Task CascadeLocalResultAsync(object? result, object message, Type messageType, IMessageEnvelope? sourceEnvelope,
      CancellationToken cancellationToken) {
    if (result is null) {
      return;
    }
    var envelope = sourceEnvelope ?? _createEnvelope(message, MessageContext.Create(_cascadeContextFactory.NewRoot()),
      new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      nameof(CascadeLocalResultAsync), string.Empty, 0);
    await _cascadeEventsFromResultAsync(result, messageType, envelope, cancellationToken: cancellationToken).ConfigureAwait(false);
  }

  private Dispatch.DispatchModes? _lookupReceptorDefaultRouting(Type messageType) {
    var own = GetReceptorDefaultRouting(messageType);
    if (own is not null) {
      return own;
    }
    foreach (var lookup in _foreignLookups()) {
      if (lookup.LookupReceptorDefaultRouting(messageType) is { } foreign) {
        return foreign;
      }
    }
    return null;
  }

  /// <summary>Publish fan-out to every FOREIGN assembly's receptors for the event type — a
  /// broadcast, not a first-match: receptors for one event may live in several assemblies.</summary>
  private async Task _publishToForeignLookupsAsync(
      object eventData, Type eventType, IMessageEnvelope? envelope, CancellationToken cancellationToken) {
    foreach (var lookup in _foreignLookups()) {
      if (lookup.LookupUntypedReceptorPublisher(eventType) is { } publisher) {
        await publisher(eventData, envelope, cancellationToken).ConfigureAwait(false);
      }
    }
  }

  private const string TAG_MESSAGE_TYPE = "whizbang.message.type";
  private const string TAG_MESSAGE_ID = "whizbang.message.id";
  private const string TAG_CORRELATION_ID = "whizbang.correlation.id";
  private const string TAG_DEBUG_PARENT_ID = "whizbang.debug.parent.id";
  private const string TAG_DEBUG_PARENT_SOURCE = "whizbang.debug.parent.source";
  private const string METRIC_MESSAGE_TYPE = "message_type";
  private const string METRIC_PATTERN = "pattern";
  private const string METRIC_ERROR_TYPE = "error_type";
  private const string PATTERN_LOCAL_INVOKE = "local_invoke";
  private const string ROUTED_NONE_ERROR = "Cannot invoke a RoutedNone (Route.None()) - it has no inner message to dispatch.";

  private readonly IServiceProvider _internalServiceProvider = ArgumentGuard.NotNull(serviceProvider);
  private Whizbang.Core.Observability.ReEmissionDiagnostic? _reEmissionDiagnostic;
  private bool _reEmissionResolved;

  private Whizbang.Core.Observability.ReEmissionDiagnostic? _resolveReEmissionDiagnostic() {
    if (!_reEmissionResolved) {
      try {
        _reEmissionDiagnostic = _internalServiceProvider.GetService<Whizbang.Core.Observability.ReEmissionDiagnostic>();
      } catch (ObjectDisposedException) {
        // Publish-after-dispose is tolerated elsewhere in this class; the diagnostic is
        // observability, never worth failing a publish over.
      }
      _reEmissionResolved = true;
    }
    return _reEmissionDiagnostic;
  }
  private readonly IServiceScopeFactory _scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
  // Where the detached stages this dispatcher starts are recorded, so a stopping host waits for them before it disposes
  // the provider they resolve their receptors from (#1721). A container that never ran TryAddWhizbangDefaults has no
  // host drain, so a private record keeps the call path uniform there.
  private readonly DetachedLifecycleWork _detachedWork = serviceProvider.GetService<DetachedLifecycleWork>() ?? new DetachedLifecycleWork();
  private readonly IServiceInstanceProvider _instanceProvider = ArgumentGuard.NotNull(instanceProvider);
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
  // Ephemeral-mode resolver: stamps EventFlags.Ephemeral for [Ephemeral] events so the emit chain
  // offloads their body. Optional — null in minimal hosts, where the IEphemeralEvent marker still works.
  private readonly IEphemeralModeResolver? _ephemeralModeResolver = serviceProvider.GetService<IEphemeralModeResolver>();
  // Priority step 1: the producer hooks that declare a message's priority at dispatch. Null when the host never
  // registered the chain, in which case envelopes go out undeclared exactly as before.
  private readonly Whizbang.Core.Priority.PriorityHookChain? _priorityHooks = serviceProvider.GetService<Whizbang.Core.Priority.PriorityHookChain>();
  // Outbox routing strategy for determining actual transport destinations (inbox for commands, namespace for events)
  private readonly IOutboxRoutingStrategy? _outboxRoutingStrategy = outboxRoutingStrategy ?? serviceProvider.GetService<IOutboxRoutingStrategy>();
  // Owned domains for routing decisions - resolved from RoutingOptions if available
  private readonly HashSet<string> _ownedDomains = _resolveOwnedDomains(serviceProvider);
  // Whizbang options for runtime configuration (auto-generate StreamIds, etc.)
#pragma warning disable S4487, S1144 // Pre-resolved for use by subclasses and future features
  private readonly WhizbangOptions _whizbangOptions = serviceProvider.GetService<Microsoft.Extensions.Options.IOptions<WhizbangOptions>>()?.Value ?? new WhizbangOptions();

  // The application author's own declaration that a message type carries no authority (a
  // pre-authentication event, a health check). Stamped so those stay distinguishable from an event
  // that simply LOST its scope — without it, the missing-scope invariant flags them as defects.
  private readonly HashSet<Type>? _declaredUnscopedTypes = _messageSecurityOptions(serviceProvider)?.ExemptMessageTypes;
  // Whether a dispatch handled here must have a caller context (#1419); false, the default, trusts it
  private readonly bool _localDispatchRequiresContext = _messageSecurityOptions(serviceProvider)?.RequireContextForLocalDispatch ?? false;
  // The types the application exempts from that requirement; empty when no security options are configured
  private readonly HashSet<Type> _localDispatchExemptTypes = _messageSecurityOptions(serviceProvider)?.ExemptMessageTypes ?? [];

  /// <summary>
  /// The message security options as the application configured them. <c>AddWhizbangMessageSecurity</c>
  /// registers them as a singleton, not through <c>IOptions</c>, so reading only <c>IOptions</c> saw an
  /// unconfigured instance and lost the exempt types and every other setting (#1419).
  /// </summary>
  private static Security.MessageSecurityOptions? _messageSecurityOptions(IServiceProvider serviceProvider) =>
    serviceProvider.GetService<Security.MessageSecurityOptions>() ?? serviceProvider.GetOptionsValue<Security.MessageSecurityOptions>();
#pragma warning restore S4487, S1144
  // Core options for tag processing configuration
  private readonly WhizbangCoreOptions _coreOptions = serviceProvider.GetService<WhizbangCoreOptions>() ?? new WhizbangCoreOptions();
  // The message payload limit, checked where each message is serialized for the outbox. Built from the host's
  // options when the host did not register it, so the limit holds either way.
  private readonly MessagePayloadLimits _payloadLimits = MessagePayloadLimits.Resolve(serviceProvider);
  // Message tag processor - invoked after successful receptor completion
  private readonly IMessageTagProcessor? _messageTagProcessor = serviceProvider.GetService<IMessageTagProcessor>();

  /// <summary>
  /// Whether a message's tags are processed right after its handler, so a void local invoke cannot take
  /// the fast path that returns the handler's task directly (#1410 follow-up).
  /// </summary>
  private bool _processesTagsAfterReceptor =>
    _messageTagProcessor is not null
    && _coreOptions.EnableTagProcessing
    && _coreOptions.TagProcessingMode == TagProcessingMode.AfterReceptorCompletion;
  // Lifecycle coordinator - centralized stage transitions
  private readonly ILifecycleCoordinator? _lifecycleCoordinator = serviceProvider.GetService<ILifecycleCoordinator>();
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
  // The central dispatch policy (#1395): every call asks it whether to run locally, write the outbox,
  // store only, defer, or refuse. Developer hooks and custom policies come from the container.
  private readonly DispatchPolicyEngine _policyEngine = _resolvePolicyEngine(serviceProvider);
  // The clock a ScheduledFor is compared against, so a test can drive it.
  private readonly TimeProvider _timeProvider = serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
  // Whether a work coordinator (the outbox) is registered at all. Asked once; the scoped strategy is
  // still resolved per write, and a registration that resolves to nothing is handled there.
  private readonly bool _outboxRegistered =
    serviceProvider.GetService<IServiceProviderIsService>()?.IsService(typeof(IWorkCoordinatorStrategy)) ?? true;
  // The namespace-derived traits of each message type (owned, system command), computed once per type.
  private readonly System.Collections.Concurrent.ConcurrentDictionary<Type, MessageTraits> _namespaceTraits = new();

  // How the next outbox copy built on this async flow is marked. Set by a caller around its outbox
  // write (publish, cascade) so the generated cascade code needs no new parameter.
  private static readonly AsyncLocal<OutboxCopyMark> _outboxCopyMark = new();

  // The caller's token for the copy write started on this async flow, set around the start the way the
  // mark is, so the generated cascade code needs no new parameter (#1410 follow-up).
  private static readonly AsyncLocal<CopyWriteToken> _copyWriteToken = new();

  /// <summary>Held in a wrapper so only the copy write reads it as a token.</summary>
  private readonly record struct CopyWriteToken(CancellationToken Token);

  private static CancellationToken _ambientCopyWriteToken() => _copyWriteToken.Value.Token;

  // The copy write of the message whose local run is in progress on this async flow (#1410 follow-up).
  // A copy written during that run, by a handler, waits for it: a cause reaches the outbox, and an event
  // the event store, before any effect its handlers emit, while the handlers still run alongside it.
  private static readonly AsyncLocal<Task?> _causeCopyWrite = new();

  /// <summary>
  /// Waits for the copy write of the cause this flow's local run is handling, when there is one still
  /// in flight. Its failure is the cause's caller's to see, so the effect's write goes ahead regardless.
  /// </summary>
  private static Task _afterCauseCopyWriteAsync() =>
    _causeCopyWrite.Value is { IsCompleted: false } cause ? Task.WhenAny(cause) : Task.CompletedTask;

  private enum OutboxCopyMark : byte {
    None = 0,
    HandledLocally = 1,
    LocalDeferred = 2,
  }

  /// <summary>Whether the decision writes the message's copy, to the outbox or only to the event store; writing it stamps the message.</summary>
  private static bool _writesCopy(in DispatchDecision decision) => decision.WriteOutbox || decision.WriteEventStoreOnly;

  private static OutboxCopyMark _markFor(in DispatchDecision decision) {
    if (decision.MarkHandledLocally) {
      return OutboxCopyMark.HandledLocally;
    }
    return decision.Local == LocalRun.AtScheduledTime ? OutboxCopyMark.LocalDeferred : OutboxCopyMark.None;
  }

  /// <summary>
  /// Registers an envelope for a message a publish or cascade is about to run locally, so a receptor that
  /// looks up the envelope of the message it handles finds one, as on a send or invoke (#1410 follow-up).
  /// Returns the registered envelope, or null when there is no registry, so there is nothing to unregister. <paramref name="stampMessage"/>
  /// is false when the message's copy already stamped it, so the receptors see what the copy stored (#1455);
  /// <paramref name="copyId"/> is that copy's id, so the envelope they look up is the stored message (#1464).
  /// </summary>
  private MessageEnvelope<object>? _registerLocalRunEnvelope(object message, IMessageEnvelope? sourceEnvelope, bool stampMessage, MessageId? copyId = null) {
    if (_envelopeRegistry is null) {
      return null;
    }
    var cascade = sourceEnvelope is null ? _cascadeContextFactory.NewRoot() : _cascadeContextFactory.FromEnvelope(sourceEnvelope);
    // A message whose copy already stamped it is handed to the receptors as the copy was stored, so its
    // envelope does not stamp it again (#1455); one with no copy is stamped here, once
    var envelope = _createLocalRunEnvelope(message, cascade, copyId, stampMessage);
    _envelopeRegistry.Register(envelope);
    return envelope;
  }

  private MessageEnvelope<object> _createLocalRunEnvelope(object message, CascadeContext cascade, MessageId? copyId, bool stampMessage) =>
    _createEnvelope(message, MessageContext.Create(cascade),
      new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
      new CallerLocation(nameof(_registerLocalRunEnvelope), string.Empty, 0), copyId ?? MessageId.New(), stampMessage);

  /// <summary>
  /// Fires PostLifecycle for a message this service just handled in-process, as a local invoke does, so a
  /// receptor at PostLifecycle runs whichever call handled the message (#1413). It fires once per handling
  /// service: a stored event that a perspective here applies reaches PostLifecycle through the perspective
  /// worker, after its perspectives, so the local run leaves it to the worker.
  /// </summary>
  /// <param name="message">The message the local run handled.</param>
  /// <param name="messageType">Its runtime type.</param>
  /// <param name="envelope">The local run's envelope, or null when none was built (no envelope registry).</param>
  /// <param name="sourceEnvelope">The envelope that caused it, for an envelope built here.</param>
  /// <param name="storedId">The id its copy carries, or null when the dispatch writes no copy.</param>
  /// <param name="cancellationToken">The dispatch's token.</param>
  private ValueTask _invokeLocalRunPostLifecycleAsync(object message, Type messageType, IMessageEnvelope? envelope,
      IMessageEnvelope? sourceEnvelope, MessageId? storedId, CancellationToken cancellationToken) {
    if (!_hasPostLifecycleReceptors(messageType) && !_processesTagsAfterReceptor) {
      return ValueTask.CompletedTask;
    }
    // Stored means a copy is written and something can write it: with no outbox registered the copy is
    // only deferred or logged, so it never reaches the perspective worker
    if (storedId is not null && _outboxRegistered && _perspectiveEventTypes.Contains(messageType)) {
      return ValueTask.CompletedTask;
    }
    return _invokePostLifecycleReceptorsAsync(_localRunEnvelopeFor(message, envelope, sourceEnvelope, storedId), message, messageType,
      cancellationToken);
  }

  /// <summary>
  /// The envelope a local run's later stages see: the one it registered, or, with no envelope registry, one built here.
  /// The message is not stamped again here: its local run, or its copy, already stamped it (#1455).
  /// </summary>
  private IMessageEnvelope _localRunEnvelopeFor(object message, IMessageEnvelope? envelope, IMessageEnvelope? sourceEnvelope,
      MessageId? storedId) =>
    envelope ?? _createLocalRunEnvelope(message,
      sourceEnvelope is null ? _cascadeContextFactory.NewRoot() : _cascadeContextFactory.FromEnvelope(sourceEnvelope),
      storedId, stampMessage: false);

  private HashSet<Type>? _perspectiveEventTypesCache;

  /// <summary>
  /// The event types a perspective on this service applies, read once from the perspective registry. The
  /// perspective worker fires PostLifecycle for these once they are stored.
  /// </summary>
  private HashSet<Type> _perspectiveEventTypes {
    get {
      if (_perspectiveEventTypesCache is not null) {
        return _perspectiveEventTypesCache;
      }
      try {
        var registry = _internalServiceProvider.GetService<IPerspectiveRunnerRegistry>();
        _perspectiveEventTypesCache = registry is { IsAvailable: true } ? [.. registry.GetEventTypes()] : [];
      } catch (ObjectDisposedException) {
        // Provider disposed during shutdown: no perspective worker is left to fire it either
        _perspectiveEventTypesCache = [];
      }
      return _perspectiveEventTypesCache;
    }
  }

  /// <summary>
  /// Runs a published event's local receptors: the generated ones, those found in other assemblies,
  /// then runtime registrations, and processes its tags once they all succeed. A local failure first
  /// observes the outbox copy's write, so that task never goes unobserved, then surfaces.
  /// </summary>
  private async Task _runPublishReceptorsAsync<TEvent>(ReceptorPublisher<TEvent> publisher, TEvent eventData, Type eventType,
      Task outboxTask, MessageId? copyId, CancellationToken cancellationToken) {
    // Both PublishAsync overloads refuse a null event before they get here
    object message = eventData!;
    // What the handlers emit is queued after this event's copy (#1410 follow-up). Scoped to this method's
    // flow: an async method's AsyncLocal writes do not reach its caller
    _causeCopyWrite.Value = outboxTask;
    var registered = _registerLocalRunEnvelope(message, sourceEnvelope: null, stampMessage: copyId is null, copyId);
    try {
      await _expandIfOwnedCompositeAsync(message, eventType, copyId).ConfigureAwait(false);
      await publisher(eventData);
      await _publishToForeignLookupsAsync(message, eventType, null, cancellationToken).ConfigureAwait(false);
      await _publishToRuntimeLocalReceptorsAsync(message, eventType, cancellationToken).ConfigureAwait(false);
    } catch {
      // Ensure outbox task is observed before re-throwing to avoid UnobservedTaskException
      try { await outboxTask; } catch { /* outbox exception is secondary */ }
      throw;
    } finally {
      if (registered is not null) {
        _envelopeRegistry!.Unregister(message);
      }
    }

    // Process tags after successful receptor completion
    await ProcessTagsIfEnabledAsync(message, eventType, cancellationToken);
    await _invokeLocalRunPostLifecycleAsync(message, eventType, registered, sourceEnvelope: null, copyId, cancellationToken);
    await _startLocalRunDetachedHooksAsync(message, eventType, registered, sourceEnvelope: null, copyId);
  }

  /// <summary>
  /// Writes a published event's copy as the policy decided: to the outbox (marked when handled here,
  /// marked deferred when its local handling waits for the schedule), to the event store only, or not
  /// at all. Starts the write and returns its task, so the caller's local run does not delay it.
  /// </summary>
#pragma warning disable RCS1229 // The mark must cover only the synchronous start of the write, which captures it; awaiting here would add a state machine to every publish.
  private Task _publishCopyAsync<TEvent>(TEvent eventData, Type eventType, MessageId messageId, IMessageEnvelope? sourceEnvelope,
      DispatchOptions? options, in DispatchDecision decision) {
    if (!_writesCopy(in decision)) {
      return Task.CompletedTask;
    }
    var mark = _markFor(in decision);
    if (mark == OutboxCopyMark.None) {
      return PublishToOutboxAsync(eventData, eventType, messageId, sourceEnvelope, eventStoreOnly: !decision.WriteOutbox, options, recordLocalDispatch: decision.Local == LocalRun.Now);
    }
    var previous = _outboxCopyMark.Value;
    _outboxCopyMark.Value = mark;
    try {
      return PublishToOutboxAsync(eventData, eventType, messageId, sourceEnvelope, eventStoreOnly: !decision.WriteOutbox, options, recordLocalDispatch: decision.Local == LocalRun.Now);
    } finally {
      _outboxCopyMark.Value = previous;
    }
  }
#pragma warning restore RCS1229

  /// <summary>
  /// The dispatch context of a published or cascaded outbox copy, carrying the caller's mark (#1398,
  /// #1401): a copy this service already handled says so, and a copy whose local handling was deferred
  /// is handled here when it comes back.
  /// </summary>
  private static MessageDispatchContext _outboxCopyDispatchContext() {
    var mark = _outboxCopyMark.Value;
    return new MessageDispatchContext {
      Mode = mark == OutboxCopyMark.HandledLocally ? DispatchModes.Both : DispatchModes.Outbox,
      Source = MessageSource.Outbox,
      LocalDeferred = mark == OutboxCopyMark.LocalDeferred,
    };
  }
  // Security context accessor is resolved lazily from scope - it's a scoped service
  // DO NOT resolve in constructor - will fail with "Cannot resolve scoped service from root provider"

  // Lazy-resolved logger for cascade diagnostic tracing (avoids constructor changes)
  // Uses try-catch to handle ObjectDisposedException during shutdown gracefully
#pragma warning disable S6669 // Two loggers needed: cascade vs dispatcher tracing use different categories
#pragma warning disable IDE1006 // Logger properties use PascalCase, not _camelCase
  private ILogger? _cascadeLogger;
  private ILogger CascadeLogger {
    get {
      if (_cascadeLogger is not null) {
        return _cascadeLogger;
      }
      try {
        _cascadeLogger = _internalServiceProvider.GetLoggerOrNullLogger("Whizbang.Core.Dispatcher.Cascade");
      } catch (ObjectDisposedException) {
        // Service provider disposed during shutdown - use null logger
        _cascadeLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
      }
      return _cascadeLogger;
    }
  }

  // Lazy-resolved logger for dispatcher diagnostic tracing
  private ILogger? _dispatcherLogger;
  private ILogger DispatcherLogger {
    get {
      if (_dispatcherLogger is not null) {
        return _dispatcherLogger;
      }
      try {
        _dispatcherLogger = _internalServiceProvider.GetLoggerOrNullLogger("Whizbang.Core.Dispatcher");
      } catch (ObjectDisposedException) {
        _dispatcherLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
      }
      return _dispatcherLogger;
    }
  }
#pragma warning restore S6669

  /// <summary>
  /// Resolves owned domains from RoutingOptions in DI container.
  /// </summary>
  private static HashSet<string> _resolveOwnedDomains(IServiceProvider sp) {
    var routingOptions = sp.GetService<Microsoft.Extensions.Options.IOptions<RoutingOptions>>()?.Value;
    return routingOptions?.OwnedDomains.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
  }

  /// <summary>
  /// Checks whether a namespace belongs to this service's owned domains.
  /// Uses the same hierarchical matching as <see cref="EventSubscriptionDiscovery"/>:
  /// exact match or child namespace (prefix with '.' separator).
  /// </summary>
  /// <param name="ns">The namespace to check (typically from a message type).</param>
  /// <returns>True if the namespace is owned by this service.</returns>
  /// <docs>fundamentals/dispatcher/routing#owned-domain-routing</docs>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherCoverageSweepRoutedCascadeTests.cs:SendAsync_OwnedChildNamespaceCommand_NoReceptor_ThrowsReceptorNotFoundAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherCoverageSweepRoutedCascadeTests.cs:SendAsync_NonMatchingOwnedDomains_CommandRoutesToOutboxAsync</tests>
  private bool _isOwnedNamespace(string? ns) {
    if (string.IsNullOrEmpty(ns) || _ownedDomains.Count == 0) {
      return false;
    }

    // Exact match
    if (_ownedDomains.Contains(ns)) {
      return true;
    }

    // Child namespace match: if owned is "Consumer.Contracts.Chat",
    // then "Consumer.Contracts.Chat.Common" is also owned
    foreach (var owned in _ownedDomains) {
      var prefix = owned.EndsWith('.') ? owned : owned + ".";
      if (ns.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
        return true;
      }
    }

    return false;
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
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherCoverageSweepSyncRpcTests.cs:LocalInvokeAsync_Void_SyncAttributeSynced_AwaitsPerspectiveThenInvokesReceptorAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherCoverageSweepSyncRpcTests.cs:LocalInvokeAsync_Void_SyncAttributeTimedOut_FireOnSuccess_ThrowsAndSkipsReceptorAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherCoverageSweepSyncRpcTests.cs:LocalInvokeAsync_Void_SyncAttribute_NoStreamId_SkipsAwaiterAsync</tests>
  private async ValueTask<SyncContext?> _awaitPerspectiveSyncIfNeededAsync(
      object message,
      Type messageType,
      CancellationToken ct = default) {
    // Perspectives only process events, not commands or other message types.
    // Waiting for perspective sync on a non-event would wait forever and timeout.
    if (message is not IEvent || _receptorRegistry is null) {
      return null;
    }

    // The local handler with [AwaitPerspectiveSync], at the local handler stage, where a receptor with no [FireAt]
    // is registered too (#1410 follow-up, #1392)
    var syncReceptor = _localReceptorsFor(messageType).FirstOrDefault(r => r.SyncAttributes is { Count: > 0 });
    return syncReceptor is null ? null : await _awaitPerspectiveSyncForAsync(message, messageType, syncReceptor, ct).ConfigureAwait(false);
  }

  /// <summary>
  /// Waits for one receptor's <c>[AwaitPerspectiveSync]</c> perspectives before it runs, from the
  /// generated publisher, which runs every local receptor and waits for each that asks (#1410 follow-up).
  /// Returns the sync context for <see cref="SetSyncContext"/> to hand to the receptor.
  /// </summary>
  /// <param name="message">The message the receptor is about to handle.</param>
  /// <param name="messageType">The message's type.</param>
  /// <param name="receptorId">The receptor's registry id.</param>
  /// <param name="cancellationToken">Cancels the wait.</param>
  /// <docs>fundamentals/perspectives/perspective-sync</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Dispatcher/DispatchPerspectiveSyncComponentTests.cs</tests>
  protected async Task<SyncContext?> AwaitPerspectiveSyncForReceptorAsync(object message, Type messageType, string receptorId,
      CancellationToken cancellationToken) {
    if (message is not IEvent || _receptorRegistry is null) {
      return null;
    }
    var receptor = _localReceptorsFor(messageType).FirstOrDefault(r => r.ReceptorId == receptorId && r.SyncAttributes is { Count: > 0 });
    return receptor is null ? null : await _awaitPerspectiveSyncForAsync(message, messageType, receptor, cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Hands a receptor the outcome of its perspective wait. Synchronous on purpose: an async-local value
  /// set inside an async method does not reach its caller, so the wait returns the context and the code
  /// that is about to invoke the receptor sets it here (#1410 follow-up).
  /// </summary>
  /// <param name="context">The wait's outcome, or null when there was no wait.</param>
  /// <docs>fundamentals/perspectives/perspective-sync</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Dispatcher/DispatchPerspectiveSyncComponentTests.cs</tests>
  protected static void SetSyncContext(SyncContext? context) {
    if (context is not null) {
      SyncContextAccessor.CurrentContext = context;
    }
  }

  /// <summary>Every receptor registered at a local stage for a message type, each once.</summary>
  private List<ReceptorInfo> _localReceptorsFor(Type messageType) {
    var receptors = new List<ReceptorInfo>();
    foreach (var stage in _localStages) {
      receptors.AddRange(_receptorRegistry!.GetReceptorsFor(messageType, stage)
        .Where(receptor => !receptors.Exists(r => r.ReceptorId == receptor.ReceptorId)));
    }
    return receptors;
  }

  private async ValueTask<SyncContext?> _awaitPerspectiveSyncForAsync(object message, Type messageType, ReceptorInfo syncReceptor,
      CancellationToken ct) {
    // Extract stream ID from message
    var streamId = _streamIdExtractor?.ExtractStreamId(message, messageType);
    if (streamId is null) {
      // No stream ID - can't do stream-based sync
      return null;
    }

    // Create a scope to resolve scoped services (IPerspectiveSyncAwaiter)
    await using var scope = _scopeFactory.CreateAsyncScope();
    var syncAwaiter = scope.ServiceProvider.GetService<IPerspectiveSyncAwaiter>();
    if (syncAwaiter is null) {
      return null;
    }

    SyncContext? syncContext = null;
    foreach (var syncAttr in syncReceptor.SyncAttributes!) {
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

      // The context the receptor reads through SyncContextAccessor, set by the caller
      syncContext = new SyncContext {
        StreamId = streamId.Value,
        PerspectiveType = syncAttr.PerspectiveType,
        Outcome = syncResult.Outcome,
        EventsAwaited = syncResult.EventsAwaited,
        ElapsedTime = syncResult.ElapsedTime,
        FailureReason = syncResult.Outcome == SyncOutcome.TimedOut ? "Timeout exceeded" : null
      };

      // If FireBehavior is FireOnSuccess and we timed out, throw an exception
      if (syncAttr.FireBehavior == SyncFireBehavior.FireOnSuccess && syncResult.Outcome == SyncOutcome.TimedOut) {
        throw new PerspectiveSyncTimeoutException(
            syncAttr.PerspectiveType,
            timeout,
            $"Perspective sync timed out waiting for {syncAttr.PerspectiveType.Name} before invoking receptor {syncReceptor.ReceptorId}");
      }
      // FireBehavior.FireAlways continues regardless of timeout
    }
    return syncContext;
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
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherPerspectiveSyncCoverageTests.cs:LocalInvokeAsync_WithWaitForPerspectives_NoEventCompletionAwaiter_ReturnsNormallyAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherPerspectiveSyncCoverageTests.cs:LocalInvokeAsync_WithWaitForPerspectives_NoScopedTracker_ReturnsNormallyAsync</tests>
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
  /// <tests>tests/Whizbang.Core.Component.Tests/Dispatcher/DispatcherTests.cs:Send_WithValidMessage_ShouldReturnDeliveryReceiptAsync</tests>
  /// <tests>tests/Whizbang.Core.Component.Tests/Dispatcher/DispatcherTests.cs:Send_WithContext_ShouldPreserveCorrelationIdInReceiptAsync</tests>
  /// <tests>tests/Whizbang.Core.Component.Tests/Dispatcher/DispatcherTests.cs:SendAsync_Generic_CreatesTypedEnvelopeForTracingAsync</tests>
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
  public Task<IDeliveryReceipt> SendAsync(
    object message,
    IMessageContext context,
    [CallerMemberName] string callerMemberName = "",
    [CallerFilePath] string callerFilePath = "",
    [CallerLineNumber] int callerLineNumber = 0
  ) => _sendObjectAsync(message, context, DispatchPolicies.Send, ownsOutboxWrite: true, callerMemberName, callerFilePath, callerLineNumber);

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
    var (unwrapped, route) = _unwrapRouted(message);
    return await _sendCoreAsync(unwrapped, unwrapped.GetType(), context, options, route, DispatchPolicies.Send, ownsOutboxWrite: true,
      callerMemberName, callerFilePath, callerLineNumber);
  }


  /// <summary>
  /// The untyped send. <paramref name="policy"/> is the calling method's dispatch policy;
  /// <paramref name="ownsOutboxWrite"/> is false only for callers that queue the outbox copy
  /// themselves (SendManyAsync).
  /// </summary>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  private async Task<IDeliveryReceipt> _sendObjectAsync(
    object message,
    IMessageContext context,
    DispatchPolicy policy,
    bool ownsOutboxWrite,
    string callerMemberName,
    string callerFilePath,
    int callerLineNumber
  ) {
    ArgumentNullException.ThrowIfNull(message);
    ArgumentNullException.ThrowIfNull(context);
    var (unwrapped, route) = _unwrapRouted(message);
    return await _sendCoreAsync(unwrapped, unwrapped.GetType(), context, options: null, route, policy, ownsOutboxWrite,
      callerMemberName, callerFilePath, callerLineNumber);
  }


  /// <summary>
  /// Unwraps a <c>Route.X(message)</c> passed to a send, keeping its route (#1395). A route that sends
  /// nothing fails before the message type is known, so the error is counted under the Unknown type.
  /// </summary>
  /// <summary>
  /// Whether a generic send must dispatch by the message's own type rather than the type argument: the
  /// caller declared an interface or base type, or passed a <see cref="IRouted"/> wrapper (#1395), which
  /// is honored on the generic overload as on the object one. Handler lookup, ownership and the copy's
  /// destination all follow the message's own type.
  /// </summary>
  private static bool _sendsAsAnotherType<TMessage>(TMessage message) where TMessage : notnull =>
    message is IRouted || message.GetType() != typeof(TMessage);

  private (object Message, DispatchModes? Route) _unwrapRouted(object message) {
    if (message is not IRouted routed) {
      return (message, null);
    }
    if (routed.Mode == DispatchModes.None || routed.Value == null) {
      var error = new ArgumentException("Cannot send a RoutedNone (Route.None()) - it has no inner message to dispatch.", nameof(message));
      _dispatcherMetrics?.Errors.Add(1,
        new KeyValuePair<string, object?>(METRIC_MESSAGE_TYPE, "Unknown"),
        new KeyValuePair<string, object?>(METRIC_ERROR_TYPE, error.GetType().Name));
      throw error;
    }
    return (routed.Value, routed.Mode);
  }

  /// <summary>
  /// The envelope for a send: typed when the caller's static type is the message's, otherwise built
  /// from the runtime type (a send of an <c>object</c>).
  /// </summary>
  private MessageEnvelope<TMessage> _createSendEnvelope<TMessage>(TMessage message, IMessageContext context, MessageDispatchContext dispatchContext,
      string callerMemberName, string callerFilePath, int callerLineNumber, MessageId? presetMessageId = null) where TMessage : notnull =>
    typeof(TMessage) == typeof(object)
      ? (MessageEnvelope<TMessage>)(object)_createEnvelope((object)message, context, dispatchContext,
          new CallerLocation(callerMemberName, callerFilePath, callerLineNumber), presetMessageId ?? MessageId.New())
      : _createEnvelope(message, context, dispatchContext, callerMemberName, callerFilePath, callerLineNumber, presetMessageId);

  /// <summary>
  /// A send's local run: the trace, the dispatch activity handler traces link to, any perspective wait,
  /// the handler, the cascade of what it returned, tags, PostLifecycle (#1413) and the ImmediateDetached stage.
  /// </summary>
  private async Task _runSendHandlerAsync<TMessage>(ReceptorInvoker<object> invoker, TMessage message, Type messageType,
      MessageEnvelope<TMessage> envelope, IMessageContext context, MessageId? storedId, CancellationToken cancellationToken) where TMessage : notnull {
    if (_traceStore != null) {
      await _traceStore.StoreAsync(envelope, cancellationToken);
    }
    cancellationToken.ThrowIfCancellationRequested();

    // Start dispatch activity to serve as parent for handler traces
    // Handler traces created via ITracer.BeginHandlerTrace will link to this activity
    var parentActivity = Activity.Current;
    using var dispatchActivity = WhizbangActivitySource.Execution.StartActivity($"Dispatch {messageType.Name}", ActivityKind.Internal);
    if (dispatchActivity != null) {
      dispatchActivity.SetTag(TAG_MESSAGE_TYPE, TypeNameFormatter.DisplayName(messageType));
      dispatchActivity.SetTag(TAG_MESSAGE_ID, envelope.MessageId.ToString());
      dispatchActivity.SetTag(TAG_CORRELATION_ID, context.CorrelationId.ToString());
      dispatchActivity.SetTag(TAG_DEBUG_PARENT_ID, parentActivity?.Id ?? "none");
      dispatchActivity.SetTag(TAG_DEBUG_PARENT_SOURCE, parentActivity?.Source.Name ?? "none");
    }

    // Await perspective sync if receptor has [AwaitPerspectiveSync] attributes
    SetSyncContext(await _awaitPerspectiveSyncIfNeededAsync(message, messageType, cancellationToken));

    // Invoke using delegate - zero reflection, strongly typed
    var result = await invoker(message);

    // Auto-cascade: Extract and publish any IEvent instances from result (tuples, arrays, etc.)
    await _cascadeEventsFromResultAsync(result, messageType, sourceEnvelope: envelope, cancellationToken: cancellationToken);
    await ProcessTagsIfEnabledAsync(message, messageType, cancellationToken);
    await _invokeLocalRunPostLifecycleAsync(message, messageType, envelope, sourceEnvelope: null, storedId, cancellationToken);
    await _invokeImmediateDetachedReceptorsAsync(envelope, messageType);
  }

  /// <summary>
  /// The one send path behind every <c>SendAsync</c> overload and the batch calls' single messages. The
  /// caller's policy decides (#1395): run the handler here, write the outbox (marked when handled here,
  /// scheduled when asked), both, nothing, or refuse. The outbox copy is written whatever the local
  /// outcome (owner decision): a local failure is held until the copy is queued, then surfaced.
  /// </summary>
  /// <param name="message">The message, unwrapped from any route wrapper.</param>
  /// <param name="messageType">The type the handler lookup uses: the caller's static type, or the runtime type for an <c>object</c>.</param>
  /// <param name="context">The message context.</param>
  /// <param name="options">The caller's options, or null.</param>
  /// <param name="route">The route the caller chose, or null.</param>
  /// <param name="policy">The calling method's policy.</param>
  /// <param name="ownsOutboxWrite">False for a batch call that queues the outbox copy itself.</param>
  /// <param name="callerMemberName">The calling member.</param>
  /// <param name="callerFilePath">The calling file.</param>
  /// <param name="callerLineNumber">The calling line.</param>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  [SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance", Justification = "Interface parameter required as this method is called from public API with IMessageContext")]
  [SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "The one executor behind every SendAsync overload: the message and its context, the caller's options, route and policy, the caller location the public overloads capture, and the id of a batch copy recorded first. A parameter object would be allocated on every send.")]
  private async Task<IDeliveryReceipt> _sendCoreAsync<TMessage>(
      TMessage message,
      Type messageType,
      IMessageContext context,
      DispatchOptions? options,
      DispatchModes? route,
      DispatchPolicy policy,
      bool ownsOutboxWrite,
      string callerMemberName,
      string callerFilePath,
      int callerLineNumber,
      MessageId? copyId = null) where TMessage : notnull {
    DispatchTimeoutScope? timeoutScope = null;
    if (options is not null) {
      options = _withTimeout(options, out timeoutScope);
    }
    using var timeoutDisposal = timeoutScope;
    var cancellationToken = options?.CancellationToken ?? CancellationToken.None;
    _throwIfSchemaNotReady();
    _throwIfLocalDispatchTypeNeedsContext(messageType);
    // The caller's context is the one the receptor sees, as on a local invoke (#1410 follow-up)
    MessageContextAccessor.CurrentContext = context;

    var sw = Stopwatch.StartNew();
    var messageTypeName = messageType.Name;
    try {
      var invoker = _lookupSendInvoker(message, messageType);
      var decision = _decide(policy, message, messageType, hasHandler: invoker != null, options, route);
      _throwIfRejected(in decision, messageType);
      var writeOutbox = decision.WriteOutbox && ownsOutboxWrite;
      // An event sent with a Local or EventStoreOnly route is stored without being sent (#1410 follow-up)
      var writeCopy = writeOutbox || (decision.WriteEventStoreOnly && ownsOutboxWrite);
      if (decision.Local != LocalRun.Now || invoker == null) {
        return writeCopy
          ? await _sendToOutboxViaScopeAsync(message, messageType, context, callerMemberName, callerFilePath, callerLineNumber, options,
              scheduledFor: decision.DeferUntil, localDeferred: decision.Local == LocalRun.AtScheduledTime, eventStoreOnly: !writeOutbox)
          : _nothingToDoReceipt(messageType, context);
      }

      // The id the message is stored under: a batch's recorded copy, or minted here for the copy this send
      // writes below. Minted before the local envelope, whose own id must sort after it (see below).
      var storedId = copyId ?? (writeCopy ? MessageId.New() : null);
      // What the handlers emit derives its ids from this envelope, and a derived id sorts just before the
      // id it derives from, so this envelope keeps an id of its own, minted after the copy's: an emitted
      // event then sorts after its cause when both are stored in one write (#1464)
      var envelope = _createSendEnvelope(message, context, new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
        callerMemberName, callerFilePath, callerLineNumber);
      if (options is not null) {
        _stampExplicitPriority(envelope, options.Priority);
      }
      var delivered = DeliveryReceipt.Delivered(
        envelope.MessageId,
        messageType.Name,
        context.CorrelationId,
        context.CausationId,
        _streamIdExtractor?.ExtractStreamId(message, messageType));
      // Handled here and also sent on (or stored): the copy is marked as handled here so this service does
      // not run it twice. It is started before the local run, as a publish's is, and the handler's own
      // writes wait for it, so the message reaches the outbox before its effects (#1410 follow-up). It
      // does not wait on the local outcome.
      // A caller that canceled before the dispatch started gets nothing run and nothing written
      cancellationToken.ThrowIfCancellationRequested();
      var copyWrite = writeCopy
        ? _sendToOutboxViaScopeAsync(message, messageType, context, callerMemberName, callerFilePath, callerLineNumber, options,
            handledLocallyReceipt: delivered, eventStoreOnly: !writeOutbox, copyId: storedId)
        : null;
      Exception? localFailure = null;
      _registerSendEnvelope(message, context, envelope, storedId, new CallerLocation(callerMemberName, callerFilePath, callerLineNumber));
      var outerCause = _causeCopyWrite.Value;
      _causeCopyWrite.Value = copyWrite ?? outerCause;
      try {
        await _runSendHandlerAsync(invoker, message, messageType, envelope, context, storedId, cancellationToken);
      } catch (Exception ex) when (copyWrite is not null) {
        localFailure = ex;
      } finally {
        _causeCopyWrite.Value = outerCause;
        _envelopeRegistry?.Unregister(message);
      }

      _dispatcherMetrics?.MessagesDispatched.Add(1,
        new KeyValuePair<string, object?>(METRIC_MESSAGE_TYPE, messageTypeName),
        new KeyValuePair<string, object?>(METRIC_PATTERN, "send"));

      if (copyWrite is null) {
        return delivered;
      }

      IDeliveryReceipt queuedReceipt;
      try {
        queuedReceipt = await copyWrite.ConfigureAwait(false);
      } catch (Exception outboxFailure) when (localFailure is not null) {
        throw new AggregateException(localFailure, outboxFailure);
      }
      _rethrowIfFailed(localFailure);
      return queuedReceipt;
    } catch (Exception ex) {
      _dispatcherMetrics?.Errors.Add(1,
        new KeyValuePair<string, object?>(METRIC_MESSAGE_TYPE, messageTypeName),
        new KeyValuePair<string, object?>(METRIC_ERROR_TYPE, ex.GetType().Name));
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
    DispatchPolicy? policy = null,
    bool ownsOutboxWrite = true,
    [CallerMemberName] string callerMemberName = "",
    [CallerFilePath] string callerFilePath = "",
    [CallerLineNumber] int callerLineNumber = 0
  ) where TMessage : notnull {
    ArgumentNullException.ThrowIfNull(message);
    ArgumentNullException.ThrowIfNull(context);
    if (_sendsAsAnotherType(message)) {
      var (inner, route) = _unwrapRouted(message);
      return await _sendCoreAsync(inner, inner.GetType(), context, options: null, route, policy ?? DispatchPolicies.Send, ownsOutboxWrite,
        callerMemberName, callerFilePath, callerLineNumber);
    }
    return await _sendCoreAsync(message, typeof(TMessage), context, options: null, route: null, policy ?? DispatchPolicies.Send, ownsOutboxWrite,
      callerMemberName, callerFilePath, callerLineNumber);
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
    if (_sendsAsAnotherType(message)) {
      var (inner, route) = _unwrapRouted(message);
      return await _sendCoreAsync(inner, inner.GetType(), context, options, route, DispatchPolicies.Send, ownsOutboxWrite: true,
        callerMemberName, callerFilePath, callerLineNumber);
    }
    return await _sendCoreAsync(message, typeof(TMessage), context, options, route: null, DispatchPolicies.Send, ownsOutboxWrite: true,
      callerMemberName, callerFilePath, callerLineNumber);
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
  /// <tests>tests/Whizbang.Core.Component.Tests/Dispatcher/DispatcherTests.cs:LocalInvoke_WithValidMessage_ShouldReturnBusinessResultAsync</tests>
  /// <tests>tests/Whizbang.Core.Component.Tests/Dispatcher/DispatcherTests.cs:LocalInvoke_WithContext_ShouldPreserveContextAsync</tests>
  /// <tests>tests/Whizbang.Core.Component.Tests/Dispatcher/DispatcherTests.cs:LocalInvokeAsync_DoesNotRequireTypePreservation_ForInProcessRPCAsync</tests>
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
    // Dispatch writes, so every entry refuses while the schema is not ready (#1410 follow-up)
    _throwIfSchemaNotReady();
    ArgumentNullException.ThrowIfNull(message);
    _throwIfLocalDispatchNeedsContext(message);

    ArgumentNullException.ThrowIfNull(context);

    // Set message context accessor so receptors can inject IMessageContext
    // This enables cascaded receptors to access UserId, TenantId, MessageId, etc.
    MessageContextAccessor.CurrentContext = context;

    // Unwrap Routed<T> if needed - users can call LocalInvokeAsync(Route.Local(event))
    if (message is IRouted routed) {
      if (routed.Mode == DispatchModes.None || routed.Value == null) {
        throw new ArgumentException(ROUTED_NONE_ERROR, nameof(message));
      }
      message = routed.Value;
    }

    var messageType = message.GetType();
    // A [GenerateStreamId] message gets its stream id before the receptor sees it, as on a publish (#1410 follow-up)
    _autoGenerateStreamIdIfNeeded(message, messageType);

    // One lookup for every result overload; a receptor returning a tuple that holds the response is
    // run once and the response extracted from its result
    var invoker = _lookupResultInvoker<TResult>(message, messageType) ?? throw new ReceptorNotFoundException(messageType);
    return _localInvokeWithCastFallbackAsync(invoker, message, messageType, context, callerMemberName, callerFilePath, callerLineNumber);
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
    // Dispatch writes, so every entry refuses while the schema is not ready (#1410 follow-up)
    _throwIfSchemaNotReady();
    ArgumentNullException.ThrowIfNull(message);
    _throwIfLocalDispatchNeedsContext(message);

    ArgumentNullException.ThrowIfNull(context);

    // Set message context accessor so receptors can inject IMessageContext
    // This enables cascaded receptors to access UserId, TenantId, MessageId, etc.
    MessageContextAccessor.CurrentContext = context;

    // Unwrap Routed<T> if needed
    if (message is IRouted routed) {
      if (routed.Mode == DispatchModes.None || routed.Value == null) {
        throw new ArgumentException(ROUTED_NONE_ERROR, nameof(message));
      }
      message = routed.Value;
    }

    var messageType = message.GetType();
    // A [GenerateStreamId] message gets its stream id before the receptor sees it, as on a publish (#1410 follow-up)
    _autoGenerateStreamIdIfNeeded(message, messageType);

    // Check if receptor has [AwaitPerspectiveSync] attributes - requires async path
    var hasSyncAttributes = _receptorRegistry is not null && _localReceptorsFor(messageType).Exists(r => r.SyncAttributes is { Count: > 0 });

    // Check if there are ImmediateDetached or PostLifecycle receptors — if so, must go through async path
    var hasImmediateDetached = _hasImmediateDetachedReceptors(messageType);
    var hasPostLifecycle = _hasPostLifecycleReceptors(messageType);

    // Try async receptor first (async takes precedence)
    var asyncInvoker = _lookupVoidReceptorInvoker(message, messageType);
    if (asyncInvoker != null) {
      // If sync attributes, tracing, ImmediateDetached, or PostLifecycle exist, go through async path
      if (_traceStore != null || hasSyncAttributes || hasImmediateDetached || hasPostLifecycle || _processesTagsAfterReceptor) {
        return _localInvokeVoidWithSyncAndTracingAsync(message, messageType, context, asyncInvoker, callerMemberName, callerFilePath, callerLineNumber);
      }

      // FAST PATH: Zero allocation when no tracing, sync attributes, ImmediateDetached, or PostLifecycle
      // Invoke using delegate - zero reflection, strongly typed
      // Avoid async/await state machine allocation by returning task directly
      return asyncInvoker(message);
    }

    // Fallback to void sync receptor
    var syncInvoker = _lookupVoidSyncReceptorInvoker(message, messageType);
    if (syncInvoker != null) {
      // If sync attributes, ImmediateDetached, or PostLifecycle exist, must go through async path
      if (hasSyncAttributes || hasImmediateDetached || hasPostLifecycle || _processesTagsAfterReceptor) {
        return _localInvokeVoidSyncWithSyncCheckAsync(syncInvoker, message, messageType);
      }
      // Invoke synchronously - returns pre-completed ValueTask
      syncInvoker(message);
      return ValueTask.CompletedTask;
    }

    // Fallback to any receptor (void or non-void) for cascade support
    // This enables void LocalInvokeAsync to cascade events from non-void receptors
    var anyInvoker = _lookupReceptorInvokerAny(message, messageType);
    if (anyInvoker != null) {
      return _localInvokeVoidWithAnyInvokerAndTracingAsync(anyInvoker, message, messageType, context, callerMemberName, callerFilePath, callerLineNumber);
    }

    throw new ReceptorNotFoundException(messageType);
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

  // ========================================
  // LOCAL INVOKE PRIVATE HELPERS (Result-returning)
  // ========================================

  /// <summary>
  /// Wrapper that tries the typed invoker first, but falls back to RPC extraction
  /// on InvalidCastException. This handles the case where the receptor returns a
  /// complex type (tuple, array, etc.) but the caller requests a specific type.
  /// </summary>
  /// <docs>fundamentals/dispatcher/rpc-extraction</docs>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherRpcExtractionTests.cs</tests>
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
      SetSyncContext(await _awaitPerspectiveSyncIfNeededAsync(message, messageType));

      var result = await _localInvokeWithTracingAsync(message, messageType, context, asyncInvoker, callerMemberName, callerFilePath, callerLineNumber);

      _dispatcherMetrics?.MessagesDispatched.Add(1,
        new KeyValuePair<string, object?>(METRIC_MESSAGE_TYPE, messageType.Name),
        new KeyValuePair<string, object?>(METRIC_PATTERN, PATTERN_LOCAL_INVOKE));

      return result;
    } catch (Exception ex) {
      _dispatcherMetrics?.Errors.Add(1,
        new KeyValuePair<string, object?>(METRIC_MESSAGE_TYPE, messageType.Name),
        new KeyValuePair<string, object?>(METRIC_ERROR_TYPE, ex.GetType().Name));
      throw;
    } finally {
      sw.Stop();
      _dispatcherMetrics?.LocalInvokeDuration.Record(sw.Elapsed.TotalMilliseconds);
    }
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
    var envelope = _createEnvelope(message, context, new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }, callerMemberName, callerFilePath, callerLineNumber);

    // Register envelope so receptor can look it up via IEventStore.AppendAsync(message)
    _envelopeRegistry?.Register(envelope);
    try {
      if (_traceStore != null) {
        await _traceStore.StoreAsync(envelope);
      }

      // Start dispatch activity to serve as parent for handler traces
      // Handler traces created via ITracer.BeginHandlerTrace will link to this activity
      using var dispatchActivity = WhizbangActivitySource.Execution.StartActivity($"Dispatch {messageType.Name}");
      dispatchActivity?.SetTag(TAG_MESSAGE_TYPE, TypeNameFormatter.DisplayName(messageType));
      dispatchActivity?.SetTag(TAG_MESSAGE_ID, envelope.MessageId.ToString());
      dispatchActivity?.SetTag(TAG_CORRELATION_ID, context.CorrelationId.ToString());

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
      await ProcessTagsIfEnabledAsync(message, messageType);

      // Invoke PostLifecycle receptors (local events don't go through perspectives)
      await _invokePostLifecycleReceptorsAsync(envelope, message, messageType);

      // Invoke ImmediateDetached lifecycle receptors after business receptor completes
      await _invokeImmediateDetachedReceptorsAsync(envelope, messageType);
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
    var envelope = _createEnvelope(message, context, new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }, callerMemberName, callerFilePath, callerLineNumber);

    // Register envelope so receptor can look it up via IEventStore.AppendAsync(message)
    _envelopeRegistry?.Register(envelope);
    try {
      if (_traceStore != null) {
        await _traceStore.StoreAsync(envelope);
      }

      // Start dispatch activity to serve as parent for handler traces
      // Handler traces created via ITracer.BeginHandlerTrace will link to this activity
      using var dispatchActivity = WhizbangActivitySource.Execution.StartActivity($"Dispatch {messageType.Name}");
      dispatchActivity?.SetTag(TAG_MESSAGE_TYPE, TypeNameFormatter.DisplayName(messageType));
      dispatchActivity?.SetTag(TAG_MESSAGE_ID, envelope.MessageId.ToString());
      dispatchActivity?.SetTag(TAG_CORRELATION_ID, context.CorrelationId.ToString());

      // Runs the receptor once; its result cascades, less the response when the response is extracted from it
      var result = await _invokeAndCascadeAsync(invoker, message, messageType, envelope, CancellationToken.None);

      // Process tags after successful receptor completion
      await ProcessTagsIfEnabledAsync(message, messageType);

      // Invoke PostLifecycle receptors (local events don't go through perspectives)
      await _invokePostLifecycleReceptorsAsync(envelope, message, messageType);

      // Invoke ImmediateDetached lifecycle receptors after business receptor completes
      await _invokeImmediateDetachedReceptorsAsync(envelope, messageType);

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
    // Dispatch writes, so every entry refuses while the schema is not ready (#1410 follow-up)
    _throwIfSchemaNotReady();
    ArgumentNullException.ThrowIfNull(message);
    _throwIfLocalDispatchNeedsContext(message);
    ArgumentNullException.ThrowIfNull(context);

    // Set message context accessor so receptors can inject IMessageContext
    // This enables cascaded receptors to access UserId, TenantId, MessageId, etc.
    MessageContextAccessor.CurrentContext = context;

    // Unwrap Routed<T> if needed - the generic TMessage may be Routed<T>
    // We need to use runtime type to get the actual inner message type
    object actualMessage = message;
    if (message is IRouted routed) {
      if (routed.Mode == DispatchModes.None || routed.Value == null) {
        throw new ArgumentException(ROUTED_NONE_ERROR, nameof(message));
      }
      actualMessage = routed.Value;
    }

    var messageType = actualMessage.GetType();
    // A [GenerateStreamId] message gets its stream id before the receptor sees it, as on a publish (#1410 follow-up)
    _autoGenerateStreamIdIfNeeded(actualMessage, messageType);

    // Get strongly-typed delegate from generated code
    var invoker = _lookupResultInvoker<TResult>(actualMessage, messageType)
      ?? throw new ReceptorNotFoundException(messageType);

    return _localInvokeWithTracingAsyncInternalAsync<TMessage, TResult>(message, actualMessage, messageType, context, invoker, new CallerLocation(callerMemberName, callerFilePath, callerLineNumber));
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
    // Dispatch writes, so every entry refuses while the schema is not ready (#1410 follow-up)
    _throwIfSchemaNotReady();
    ArgumentNullException.ThrowIfNull(message);
    _throwIfLocalDispatchNeedsContext(message);
    ArgumentNullException.ThrowIfNull(context);

    // Set message context accessor so receptors can inject IMessageContext
    // This enables cascaded receptors to access UserId, TenantId, MessageId, etc.
    MessageContextAccessor.CurrentContext = context;

    // Unwrap Routed<T> if needed - the generic TMessage may be Routed<T>
    // We need to use runtime type to get the actual inner message type
    object actualMessage = message;
    if (message is IRouted routed) {
      if (routed.Mode == DispatchModes.None || routed.Value == null) {
        throw new ArgumentException(ROUTED_NONE_ERROR, nameof(message));
      }
      actualMessage = routed.Value;
    }

    var messageType = actualMessage.GetType();
    // A [GenerateStreamId] message gets its stream id before the receptor sees it, as on a publish (#1410 follow-up)
    _autoGenerateStreamIdIfNeeded(actualMessage, messageType);

    // Check if receptor has [AwaitPerspectiveSync] attributes - requires async path
    var hasSyncAttributes = _receptorRegistry is not null && _localReceptorsFor(messageType).Exists(r => r.SyncAttributes is { Count: > 0 });

    // Try async receptor first (async takes precedence); two or more local receptors all run (#1412)
    var asyncInvoker = _runnerAsVoid(messageType) ?? GetVoidReceptorInvoker(actualMessage, messageType);
    if (asyncInvoker != null) {
      return _localInvokeVoidWithTracingAsyncInternalAsync<TMessage>(message, actualMessage, messageType, context, asyncInvoker, new CallerLocation(callerMemberName, callerFilePath, callerLineNumber));
    }

    // Check if there are ImmediateDetached or PostLifecycle receptors — if so, must go through async path
    var hasImmediateDetached = _hasImmediateDetachedReceptors(messageType);
    var hasPostLifecycle = _hasPostLifecycleReceptors(messageType);

    // Fallback to void sync receptor
    var syncInvoker = GetVoidSyncReceptorInvoker(actualMessage, messageType);
    if (syncInvoker != null) {
      // If sync attributes, ImmediateDetached, or PostLifecycle exist, must go through async path
      if (hasSyncAttributes || hasImmediateDetached || hasPostLifecycle || _processesTagsAfterReceptor) {
        return _localInvokeVoidSyncWithSyncCheckAsync(syncInvoker, actualMessage, messageType);
      }
      // Invoke synchronously - returns pre-completed ValueTask
      syncInvoker(actualMessage);
      return ValueTask.CompletedTask;
    }

    // Fallback to any receptor (void or non-void) for cascade support
    // This enables void LocalInvokeAsync to cascade events from non-void receptors
    var anyInvoker = _lookupReceptorInvokerAny(actualMessage, messageType);
    if (anyInvoker != null) {
      return _localInvokeVoidWithAnyInvokerAndTracingAsync(anyInvoker, actualMessage, messageType, context, callerMemberName, callerFilePath, callerLineNumber);
    }

    throw new ReceptorNotFoundException(messageType);
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
    CallerLocation caller
  ) {
    var sw = Stopwatch.StartNew();
    try {
      // Await perspective sync if receptor has [AwaitPerspectiveSync] attributes
      SetSyncContext(await _awaitPerspectiveSyncIfNeededAsync(actualMessage, messageType));

      var envelope = _createEnvelope<TMessage>(message, context, new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }, caller.MemberName, caller.FilePath, caller.LineNumber);

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
          dispatchActivity.SetTag(TAG_MESSAGE_TYPE, TypeNameFormatter.DisplayName(messageType));
          dispatchActivity.SetTag(TAG_MESSAGE_ID, envelope.MessageId.ToString());
          dispatchActivity.SetTag(TAG_CORRELATION_ID, context.CorrelationId.ToString());
          dispatchActivity.SetTag(TAG_DEBUG_PARENT_ID, parentActivity?.Id ?? "none");
          dispatchActivity.SetTag(TAG_DEBUG_PARENT_SOURCE, parentActivity?.Source.Name ?? "none");
        }

        // Runs the receptor once; its result cascades, less the response when the response is extracted from it
        var result = await _invokeAndCascadeAsync(invoker, actualMessage, messageType, envelope, CancellationToken.None);

        // Process tags after successful receptor completion
        await ProcessTagsIfEnabledAsync(actualMessage, messageType);

        // Invoke PostLifecycle receptors (local events don't go through perspectives)
        await _invokePostLifecycleReceptorsAsync(envelope, actualMessage, messageType);

        // Invoke ImmediateDetached lifecycle receptors after business receptor completes
        await _invokeImmediateDetachedReceptorsAsync(envelope, messageType);

        _dispatcherMetrics?.MessagesDispatched.Add(1,
          new KeyValuePair<string, object?>(METRIC_MESSAGE_TYPE, messageType.Name),
          new KeyValuePair<string, object?>(METRIC_PATTERN, PATTERN_LOCAL_INVOKE));

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
        new KeyValuePair<string, object?>(METRIC_MESSAGE_TYPE, messageType.Name),
        new KeyValuePair<string, object?>(METRIC_ERROR_TYPE, ex.GetType().Name));
      throw;
    } finally {
      sw.Stop();
      _dispatcherMetrics?.LocalInvokeDuration.Record(sw.Elapsed.TotalMilliseconds);
    }
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
      SetSyncContext(await _awaitPerspectiveSyncIfNeededAsync(message, messageType));

      var envelope = _createEnvelope(message, context, new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }, callerMemberName, callerFilePath, callerLineNumber);

      // Register envelope so receptor can look it up via IEventStore.AppendAsync(message)
      _envelopeRegistry?.Register(envelope);
      try {
        if (_traceStore != null) {
          await _traceStore.StoreAsync(envelope);
        }

        // Start dispatch activity to serve as parent for handler traces
        // Handler traces created via ITracer.BeginHandlerTrace will link to this activity
        using var dispatchActivity = WhizbangActivitySource.Execution.StartActivity($"Dispatch {messageType.Name}");
        dispatchActivity?.SetTag(TAG_MESSAGE_TYPE, TypeNameFormatter.DisplayName(messageType));
        dispatchActivity?.SetTag(TAG_MESSAGE_ID, envelope.MessageId.ToString());
        dispatchActivity?.SetTag(TAG_CORRELATION_ID, context.CorrelationId.ToString());

        // Invoke using delegate - zero reflection, strongly typed
        await invoker(message);
        await ProcessTagsIfEnabledAsync(message, messageType);

        // Invoke PostLifecycle receptors (local events don't go through perspectives)
        await _invokePostLifecycleReceptorsAsync(envelope, message, messageType);

        // Invoke ImmediateDetached lifecycle receptors after business receptor completes
        await _invokeImmediateDetachedReceptorsAsync(envelope, messageType);
      } finally {
        // Unregister envelope after receptor completes (or throws)
        _envelopeRegistry?.Unregister(envelope);
      }

      _dispatcherMetrics?.MessagesDispatched.Add(1,
        new KeyValuePair<string, object?>(METRIC_MESSAGE_TYPE, messageType.Name),
        new KeyValuePair<string, object?>(METRIC_PATTERN, PATTERN_LOCAL_INVOKE));
    } catch (Exception ex) {
      _dispatcherMetrics?.Errors.Add(1,
        new KeyValuePair<string, object?>(METRIC_MESSAGE_TYPE, messageType.Name),
        new KeyValuePair<string, object?>(METRIC_ERROR_TYPE, ex.GetType().Name));
      throw;
    } finally {
      sw.Stop();
      _dispatcherMetrics?.LocalInvokeDuration.Record(sw.Elapsed.TotalMilliseconds);
    }
  }

  /// <summary>
  /// Async path for void sync LocalInvoke with sync check or ImmediateDetached.
  /// Called when sync receptor has [AwaitPerspectiveSync] attributes or ImmediateDetached receptors.
  /// </summary>
  private async ValueTask _localInvokeVoidSyncWithSyncCheckAsync(
    VoidSyncReceptorInvoker invoker,
    object message,
    Type messageType
  ) {
    // Await perspective sync if receptor has [AwaitPerspectiveSync] attributes
    SetSyncContext(await _awaitPerspectiveSyncIfNeededAsync(message, messageType));

    // Start dispatch activity to serve as parent for handler traces
    // Handler traces created via ITracer.BeginHandlerTrace will link to this activity
    using var dispatchActivity = WhizbangActivitySource.Execution.StartActivity($"Dispatch {messageType.Name}");
    dispatchActivity?.SetTag(TAG_MESSAGE_TYPE, TypeNameFormatter.DisplayName(messageType));

    // Invoke synchronously, with the message's envelope registered for a receptor that looks it up
    var registered = _registerLocalRunEnvelope(message, sourceEnvelope: null, stampMessage: true) is not null;
    try {
      invoker(message);
    } finally {
      if (registered) {
        _envelopeRegistry!.Unregister(message);
      }
    }
    await ProcessTagsIfEnabledAsync(message, messageType);

    // Invoke PostLifecycle and ImmediateDetached lifecycle receptors after business receptor completes
    // Create a minimal envelope for lifecycle invocation
    if (_hasPostLifecycleReceptors(messageType) || _hasImmediateDetachedReceptors(messageType)) {
      var envelope = _createEnvelope(message, MessageContext.Create(_cascadeContextFactory.NewRoot()), new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }, "", "", 0);
      await _invokePostLifecycleReceptorsAsync(envelope, message, messageType);
      await _invokeImmediateDetachedReceptorsAsync(envelope, messageType);
    }
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
    CallerLocation caller
  ) {
    var sw = Stopwatch.StartNew();
    try {
      // Await perspective sync if receptor has [AwaitPerspectiveSync] attributes
      SetSyncContext(await _awaitPerspectiveSyncIfNeededAsync(actualMessage, messageType));

      var envelope = _createEnvelope<TMessage>(message, context, new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }, caller.MemberName, caller.FilePath, caller.LineNumber);

      // Register envelope so receptor can look it up via IEventStore.AppendAsync(message)
      _envelopeRegistry?.Register(envelope);
      try {
        if (_traceStore != null) {
          await _traceStore.StoreAsync(envelope);
        }

        // Start dispatch activity to serve as parent for handler traces
        // Handler traces created via ITracer.BeginHandlerTrace will link to this activity
        using var dispatchActivity = WhizbangActivitySource.Execution.StartActivity($"Dispatch {messageType.Name}");
        dispatchActivity?.SetTag(TAG_MESSAGE_TYPE, TypeNameFormatter.DisplayName(messageType));
        dispatchActivity?.SetTag(TAG_MESSAGE_ID, envelope.MessageId.ToString());
        dispatchActivity?.SetTag(TAG_CORRELATION_ID, context.CorrelationId.ToString());

        // Invoke using delegate with unwrapped message - zero reflection, strongly typed
        await invoker(actualMessage);
        await ProcessTagsIfEnabledAsync(actualMessage, messageType);

        // Invoke PostLifecycle receptors (local events don't go through perspectives)
        await _invokePostLifecycleReceptorsAsync(envelope, actualMessage, messageType);

        // Invoke ImmediateDetached lifecycle receptors after business receptor completes
        await _invokeImmediateDetachedReceptorsAsync(envelope, messageType);
      } finally {
        // Unregister envelope after receptor completes (or throws)
        _envelopeRegistry?.Unregister(envelope);
      }

      _dispatcherMetrics?.MessagesDispatched.Add(1,
        new KeyValuePair<string, object?>(METRIC_MESSAGE_TYPE, messageType.Name),
        new KeyValuePair<string, object?>(METRIC_PATTERN, PATTERN_LOCAL_INVOKE));
    } catch (Exception ex) {
      _dispatcherMetrics?.Errors.Add(1,
        new KeyValuePair<string, object?>(METRIC_MESSAGE_TYPE, messageType.Name),
        new KeyValuePair<string, object?>(METRIC_ERROR_TYPE, ex.GetType().Name));
      throw;
    } finally {
      sw.Stop();
      _dispatcherMetrics?.LocalInvokeDuration.Record(sw.Elapsed.TotalMilliseconds);
    }
  }

  /// <summary>
  /// Internal implementation of LocalInvokeAsync with DispatchOptions.
  /// </summary>
  // S3776: Complexity 16 from Routed<T> unwrapping + async/sync invoker fallback — already well-structured
#pragma warning disable S3776
  private async ValueTask<TResult> _localInvokeWithOptionsAsync<TResult>(
    object message,
    IMessageContext context,
    DispatchOptions options,
    [CallerMemberName] string callerMemberName = "",
    [CallerFilePath] string callerFilePath = "",
    [CallerLineNumber] int callerLineNumber = 0
  ) {
    // Dispatch writes, so every entry refuses while the schema is not ready (#1410 follow-up)
    _throwIfSchemaNotReady();
    _throwIfLocalDispatchNeedsContext(message);
#pragma warning restore S3776
    ArgumentNullException.ThrowIfNull(message);
    ArgumentNullException.ThrowIfNull(context);

    options = _withTimeout(options, out var timeoutScope);
    using var timeoutDisposal = timeoutScope;
    // A Routed<T> is unwrapped and its route reaches the decision: the LocalInvoke policy ignores it,
    // a policy the caller selects may not (#1410 follow-up)
    DispatchModes? route = null;
    if (message is IRouted) {
      (message, route) = _unwrapRouted(message);
    }

    var messageType = message.GetType();
    // A [GenerateStreamId] message gets its stream id before the receptor sees it, as on a publish (#1410 follow-up)
    _autoGenerateStreamIdIfNeeded(message, messageType);
    TResult result;
    var caller = new CallerLocation(callerMemberName, callerFilePath, callerLineNumber);

    // The LocalInvoke policy (or the caller's selected one) decides (#1395): a future schedule, a
    // contradictory override, a policy that would not run the handler now, and a missing handler are all
    // refused. A call that needs a result cannot continue without a handler, ContinueWhenNoHandler or not.
    var invoker = _lookupResultInvoker<TResult>(message, messageType);
    var decision = _decide(DispatchPolicies.LocalInvoke, message, messageType, hasHandler: invoker is not null, options, route, resultRequired: true);
    _throwIfRejected(in decision, messageType);

    // Not refused, so the handler exists and runs now
    result = await _localInvokeWithTracingAndOptionsAsync(message, messageType, context, invoker!, options, caller);
    await _writeLocalInvokeCopyIfDecidedAsync(message, messageType, context, options, decision, caller);

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
    // Dispatch writes, so every entry refuses while the schema is not ready (#1410 follow-up)
    _throwIfSchemaNotReady();
    ArgumentNullException.ThrowIfNull(message);
    _throwIfLocalDispatchNeedsContext(message);
    ArgumentNullException.ThrowIfNull(context);

    options = _withTimeout(options, out var timeoutScope);
    using var timeoutDisposal = timeoutScope;
    // A Routed<T> is unwrapped and its route reaches the decision: the LocalInvoke policy ignores it,
    // a policy the caller selects may not (#1410 follow-up)
    DispatchModes? route = null;
    if (message is IRouted) {
      (message, route) = _unwrapRouted(message);
    }

    var messageType = message.GetType();
    // A [GenerateStreamId] message gets its stream id before the receptor sees it, as on a publish (#1410 follow-up)
    _autoGenerateStreamIdIfNeeded(message, messageType);
    var asyncInvoker = _lookupVoidReceptorInvoker(message, messageType);
    var caller = new CallerLocation(callerMemberName, callerFilePath, callerLineNumber);

    // The LocalInvoke policy (or the caller's selected one) decides (#1395): a future schedule is
    // refused, and ContinueWhenNoHandler lets a call with no handler return quietly
    // Each lookup runs only when the one before found nothing, and the handler found is the one invoked
    var syncInvoker = asyncInvoker is null ? _lookupVoidSyncReceptorInvoker(message, messageType) : null;
    var anyInvoker = asyncInvoker is null && syncInvoker is null ? _lookupReceptorInvokerAny(message, messageType) : null;
    var hasHandler = asyncInvoker != null || syncInvoker != null || anyInvoker != null;
    var decision = _decide(DispatchPolicies.LocalInvoke, message, messageType, hasHandler, options, route);
    _throwIfRejected(in decision, messageType);
    if (decision.Local != LocalRun.Now) {
      await _writeLocalInvokeCopyIfDecidedAsync(message, messageType, context, options, decision, caller);
      return;
    }

    if (asyncInvoker != null) {
      await _invokeVoidAsyncReceptorAsync(asyncInvoker, message, messageType, context, options, caller);
    } else if (syncInvoker != null) {
      await _invokeVoidSyncReceptorAsync(syncInvoker, message, messageType, context, options, caller);
    } else {
      // The policy refuses a local run with no handler (NoHandler), so the any-result lookup found one
      await _invokeVoidAnyReceptorAsync(anyInvoker!, message, messageType, context, options, caller);
    }
    await _writeLocalInvokeCopyIfDecidedAsync(message, messageType, context, options, decision, caller);

    // Wait for all perspectives to process cascaded events if requested
    await _waitForPerspectivesIfNeededAsync(options);
  }

  /// <summary>
  /// Writes the outbox copy a local invoke's decision asks for. The LocalInvoke policy never asks; a
  /// policy the caller selects through the options may, and the executor used to ignore it (#1410 follow-up).
  /// A copy of a message handled here is marked so, and a store-only one has no destination.
  /// </summary>
  private async ValueTask _writeLocalInvokeCopyIfDecidedAsync(object message, Type messageType, IMessageContext context,
      DispatchOptions options, DispatchDecision decision, CallerLocation caller) {
    if (!decision.WriteOutbox && !decision.WriteEventStoreOnly) {
      return;
    }
    var handledHere = decision.Local == LocalRun.Now
      ? DeliveryReceipt.Delivered(MessageId.New(), messageType.Name, context.CorrelationId, context.CausationId,
          _streamIdExtractor?.ExtractStreamId(message, messageType))
      : null;
    await _sendToOutboxViaScopeAsync(message, messageType, context, caller.MemberName, caller.FilePath, caller.LineNumber, options,
      handledLocallyReceipt: handledHere, scheduledFor: decision.DeferUntil, localDeferred: decision.Local == LocalRun.AtScheduledTime,
      eventStoreOnly: !decision.WriteOutbox).ConfigureAwait(false);
  }

  /// <summary>
  /// Invokes an async void receptor, routing through tracing if available.
  /// </summary>
  private async ValueTask _invokeVoidAsyncReceptorAsync(
    VoidReceptorInvoker asyncInvoker,
    object message,
    Type messageType,
    IMessageContext context,
    DispatchOptions options,
    CallerLocation caller
  ) {
    // The full path whenever something runs after the receptor: the fast path below creates no
    // envelope, so it fires no ImmediateDetached and no PostLifecycle stage
    if (_traceStore != null || _hasImmediateDetachedReceptors(messageType) || _hasPostLifecycleReceptors(messageType)) {
      await _localInvokeVoidWithTracingAndOptionsAsync(message, messageType, context, asyncInvoker, options, caller);
    } else {
      options.CancellationToken.ThrowIfCancellationRequested();
      // Await perspective sync if receptor has [AwaitPerspectiveSync] attributes
      SetSyncContext(await _awaitPerspectiveSyncIfNeededAsync(message, messageType, options.CancellationToken));
      await asyncInvoker(message);
      await ProcessTagsIfEnabledAsync(message, messageType, options.CancellationToken);
    }
  }

  /// <summary>
  /// Invokes a sync void receptor, routing through tracing when anything runs after it.
  /// </summary>
  private async ValueTask _invokeVoidSyncReceptorAsync(
    VoidSyncReceptorInvoker syncInvoker,
    object message,
    Type messageType,
    IMessageContext context,
    DispatchOptions options,
    CallerLocation caller
  ) {
    if (_traceStore != null || _hasImmediateDetachedReceptors(messageType) || _hasPostLifecycleReceptors(messageType)) {
      ValueTask wrappedInvoker(object msg) {
        syncInvoker(msg);
        return ValueTask.CompletedTask;
      }
      await _localInvokeVoidWithTracingAndOptionsAsync(message, messageType, context, wrappedInvoker, options, caller);
      return;
    }
    options.CancellationToken.ThrowIfCancellationRequested();
    // Await perspective sync if receptor has [AwaitPerspectiveSync] attributes
    SetSyncContext(await _awaitPerspectiveSyncIfNeededAsync(message, messageType, options.CancellationToken));
    syncInvoker(message);
    await ProcessTagsIfEnabledAsync(message, messageType, options.CancellationToken);
  }

  /// <summary>
  /// Invokes a receptor that returns a value for void dispatch, so void LocalInvokeAsync can call
  /// receptors that return events for cascading.
  /// </summary>
  private async ValueTask _invokeVoidAnyReceptorAsync(
    Func<object, ValueTask<object?>> anyInvoker,
    object message,
    Type messageType,
    IMessageContext context,
    DispatchOptions options,
    CallerLocation caller
  ) {
    options.CancellationToken.ThrowIfCancellationRequested();
    // Await perspective sync if receptor has [AwaitPerspectiveSync] attributes
    SetSyncContext(await _awaitPerspectiveSyncIfNeededAsync(message, messageType, options.CancellationToken));
    await _localInvokeVoidWithAnyInvokerAndTracingAsync(anyInvoker, message, messageType, context, caller.MemberName, caller.FilePath, caller.LineNumber);
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
    CallerLocation caller
  ) {
    // Await perspective sync if receptor has [AwaitPerspectiveSync] attributes
    SetSyncContext(await _awaitPerspectiveSyncIfNeededAsync(message, messageType, options.CancellationToken));

    var envelope = _createEnvelope(message, context, new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }, caller.MemberName, caller.FilePath, caller.LineNumber);
    _envelopeRegistry?.Register(envelope);
    try {
      if (_traceStore != null) {
        await _traceStore.StoreAsync(envelope, options.CancellationToken);
      }

      // Start dispatch activity to serve as parent for handler traces
      // Handler traces created via ITracer.BeginHandlerTrace will link to this activity
      using var dispatchActivity = WhizbangActivitySource.Execution.StartActivity($"Dispatch {messageType.Name}");
      dispatchActivity?.SetTag(TAG_MESSAGE_TYPE, TypeNameFormatter.DisplayName(messageType));
      dispatchActivity?.SetTag(TAG_MESSAGE_ID, envelope.MessageId.ToString());
      dispatchActivity?.SetTag(TAG_CORRELATION_ID, context.CorrelationId.ToString());

      options.CancellationToken.ThrowIfCancellationRequested();
      var result = await _invokeAndCascadeAsync(invoker, message, messageType, envelope, options.CancellationToken);

      // Process tags after successful receptor completion
      await ProcessTagsIfEnabledAsync(message, messageType, options.CancellationToken);

      // Invoke PostLifecycle receptors (local events don't go through perspectives)
      await _invokePostLifecycleReceptorsAsync(envelope, message, messageType, options.CancellationToken);

      // Invoke ImmediateDetached lifecycle receptors after business receptor completes
      await _invokeImmediateDetachedReceptorsAsync(envelope, messageType);

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
    CallerLocation caller
  ) {
    // Await perspective sync if receptor has [AwaitPerspectiveSync] attributes
    SetSyncContext(await _awaitPerspectiveSyncIfNeededAsync(message, messageType, options.CancellationToken));

    var envelope = _createEnvelope(message, context, new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }, caller.MemberName, caller.FilePath, caller.LineNumber);
    _envelopeRegistry?.Register(envelope);
    try {
      if (_traceStore != null) {
        await _traceStore.StoreAsync(envelope, options.CancellationToken);
      }

      // Start dispatch activity to serve as parent for handler traces
      // Handler traces created via ITracer.BeginHandlerTrace will link to this activity
      using var dispatchActivity = WhizbangActivitySource.Execution.StartActivity($"Dispatch {messageType.Name}");
      dispatchActivity?.SetTag(TAG_MESSAGE_TYPE, TypeNameFormatter.DisplayName(messageType));
      dispatchActivity?.SetTag(TAG_MESSAGE_ID, envelope.MessageId.ToString());
      dispatchActivity?.SetTag(TAG_CORRELATION_ID, context.CorrelationId.ToString());

      options.CancellationToken.ThrowIfCancellationRequested();
      await invoker(message);
      await ProcessTagsIfEnabledAsync(message, messageType, options.CancellationToken);

      // Invoke PostLifecycle receptors (local events don't go through perspectives)
      await _invokePostLifecycleReceptorsAsync(envelope, message, messageType, options.CancellationToken);

      // Invoke ImmediateDetached lifecycle receptors after business receptor completes
      await _invokeImmediateDetachedReceptorsAsync(envelope, messageType);
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
    // Dispatch writes, so every entry refuses while the schema is not ready (#1410 follow-up)
    _throwIfSchemaNotReady();
    ArgumentNullException.ThrowIfNull(message);
    _throwIfLocalDispatchNeedsContext(message);
    ArgumentNullException.ThrowIfNull(context);

    // Set message context accessor so receptors can inject IMessageContext
    MessageContextAccessor.CurrentContext = context;

    // Unwrap Routed<T> if needed
    if (message is IRouted routed) {
      if (routed.Mode == DispatchModes.None || routed.Value == null) {
        throw new ArgumentException(ROUTED_NONE_ERROR, nameof(message));
      }
      message = routed.Value;
    }

    var messageType = message.GetType();
    // A [GenerateStreamId] message gets its stream id before the receptor sees it, as on a publish (#1410 follow-up)
    _autoGenerateStreamIdIfNeeded(message, messageType);

    var invoker = _lookupResultInvoker<TResult>(message, messageType) ?? throw new ReceptorNotFoundException(messageType);
    return _localInvokeWithTracingAndReceiptAsync(message, messageType, context, invoker, callerMemberName, callerFilePath, callerLineNumber);
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
    // Dispatch writes, so every entry refuses while the schema is not ready (#1410 follow-up)
    _throwIfSchemaNotReady();
    _throwIfLocalDispatchNeedsContext(message);
    options.CancellationToken.ThrowIfCancellationRequested();
    var cascade = _cascadeContextFactory.NewRoot();
    var context = MessageContext.Create(cascade);

    ArgumentNullException.ThrowIfNull(message);

    // Unwrap Routed<T> if needed
    if (message is IRouted routed) {
      if (routed.Mode == DispatchModes.None || routed.Value == null) {
        throw new ArgumentException(ROUTED_NONE_ERROR, nameof(message));
      }
      message = routed.Value;
    }

    var messageType = message.GetType();
    // A [GenerateStreamId] message gets its stream id before the receptor sees it, as on a publish (#1410 follow-up)
    _autoGenerateStreamIdIfNeeded(message, messageType);

    var invoker = _lookupResultInvoker<TResult>(message, messageType) ?? throw new ReceptorNotFoundException(messageType);
    var invokeResult = await _localInvokeWithTracingAndReceiptAndOptionsAsync(message, messageType, context, invoker, options);
    await _waitForPerspectivesIfNeededAsync(options);
    return invokeResult;
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
    SetSyncContext(await _awaitPerspectiveSyncIfNeededAsync(message, messageType));

    var envelope = _createEnvelope(message, context, new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }, callerMemberName, callerFilePath, callerLineNumber);
    _envelopeRegistry?.Register(envelope);
    try {
      if (_traceStore != null) {
        await _traceStore.StoreAsync(envelope);
      }

      using var dispatchActivity = WhizbangActivitySource.Execution.StartActivity($"Dispatch {messageType.Name}");
      dispatchActivity?.SetTag(TAG_MESSAGE_TYPE, TypeNameFormatter.DisplayName(messageType));
      dispatchActivity?.SetTag(TAG_MESSAGE_ID, envelope.MessageId.ToString());
      dispatchActivity?.SetTag(TAG_CORRELATION_ID, context.CorrelationId.ToString());

      var result = await _invokeAndCascadeAsync(invoker, message, messageType, envelope, CancellationToken.None);
      await ProcessTagsIfEnabledAsync(message, messageType);

      // Invoke PostLifecycle receptors (local events don't go through perspectives)
      await _invokePostLifecycleReceptorsAsync(envelope, message, messageType);

      // Invoke ImmediateDetached lifecycle receptors after business receptor completes
      await _invokeImmediateDetachedReceptorsAsync(envelope, messageType);

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
    SetSyncContext(await _awaitPerspectiveSyncIfNeededAsync(message, messageType, options.CancellationToken));

    var envelope = _createEnvelope(message, context, new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }, "", "", 0);
    _envelopeRegistry?.Register(envelope);
    try {
      if (_traceStore != null) {
        await _traceStore.StoreAsync(envelope, options.CancellationToken);
      }

      using var dispatchActivity = WhizbangActivitySource.Execution.StartActivity($"Dispatch {messageType.Name}");
      dispatchActivity?.SetTag(TAG_MESSAGE_TYPE, TypeNameFormatter.DisplayName(messageType));
      dispatchActivity?.SetTag(TAG_MESSAGE_ID, envelope.MessageId.ToString());
      dispatchActivity?.SetTag(TAG_CORRELATION_ID, context.CorrelationId.ToString());

      options.CancellationToken.ThrowIfCancellationRequested();
      var result = await _invokeAndCascadeAsync(invoker, message, messageType, envelope, options.CancellationToken);
      await ProcessTagsIfEnabledAsync(message, messageType, options.CancellationToken);

      // Invoke PostLifecycle receptors (local events don't go through perspectives)
      await _invokePostLifecycleReceptorsAsync(envelope, message, messageType, options.CancellationToken);

      // Invoke ImmediateDetached lifecycle receptors after business receptor completes
      await _invokeImmediateDetachedReceptorsAsync(envelope, messageType);

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
  private ScopeDelta? _getScopeDeltaForHop(IMessageContext context, Type? messageType = null) =>
    // Priority 1: the explicit IMessageContext scope; Priority 2: ambient AsyncLocal scope. Both via the shared
    // CascadeContext helpers so scope-from-context / scope-from-ambient are resolved the ONE way everywhere.
    // Priority 3: control-plane traffic has no ambient user by design — say so explicitly, so that a
    // scope that is simply MISSING stays a detectable fault rather than looking intentional.
    HopScope.FromMessageContext(context)
      ?? HopScope.FromAmbient()
      ?? Security.SystemScopeResolver.ForUnscoped(messageType, _declaredUnscopedTypes);

  /// <summary>
  /// Creates a MessageEnvelope with initial hop containing caller information and context.
  /// Generic version - preserves type information at compile time. <paramref name="presetMessageId"/> is the
  /// id of a copy minted first, so the envelope a local run registers carries the copy's id (#1464).
  /// </summary>
  private MessageEnvelope<TMessage> _createEnvelope<TMessage>(
    TMessage message,
    IMessageContext context,
    MessageDispatchContext dispatchContext,
    string callerMemberName,
    string callerFilePath,
    int callerLineNumber,
    MessageId? presetMessageId = null
  ) {
    var messageId = presetMessageId ?? MessageId.New();

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
      Scope = _getScopeDeltaForHop(context, typeof(TMessage)),
      TraceParent = HopStamping.AmbientTraceParent
    };

    // Populate SentAt-phase properties directly on the message record
    var populatedMessage = (TMessage)AutoPopulatePopulatorRegistry.PopulateSent(message!, hop, messageId);

    var envelope = new MessageEnvelope<TMessage> {
      MessageId = messageId,
      Payload = populatedMessage,
      Hops = [],
      DispatchContext = dispatchContext
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
    MessageDispatchContext dispatchContext,
    string callerMemberName,
    string callerFilePath,
    int callerLineNumber
  ) => _createEnvelope(message, context, dispatchContext, new CallerLocation(callerMemberName, callerFilePath, callerLineNumber), MessageId.New());

  /// <summary>
  /// As the overload above, under an id the caller minted: a batch mints its copies' ids before their
  /// local runs, so they order before their effects (#1410 follow-up). With <paramref name="stampMessage"/>
  /// false the message is left as it is: an envelope only registered for a local run must not stamp the
  /// message again after its copy was stamped and stored (#1455).
  /// </summary>
  private MessageEnvelope<object> _createEnvelope(
    object message,
    IMessageContext context,
    MessageDispatchContext dispatchContext,
    CallerLocation caller,
    MessageId messageId,
    bool stampMessage = true
  ) {

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
      CallerMemberName = caller.MemberName,
      CallerFilePath = caller.FilePath,
      CallerLineNumber = caller.LineNumber,
      Metadata = hopMetadata,
      Scope = _getScopeDeltaForHop(context, messageType),
      TraceParent = HopStamping.AmbientTraceParent
    };

    // Populate SentAt-phase properties directly on the message record
    var populatedMessage = stampMessage ? AutoPopulatePopulatorRegistry.PopulateSent(message, hop, messageId) : message;

    var envelope = new MessageEnvelope<object> {
      MessageId = messageId,
      Payload = populatedMessage,
      Hops = [],
      DispatchContext = dispatchContext
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
  /// Routing behavior based on DispatchModes:
  /// <list type="bullet">
  ///   <item>Local: Invoke in-process receptors only via GetUntypedReceptorPublisher</item>
  ///   <item>Outbox: Write to outbox for cross-service delivery via _cascadeToOutboxAsync</item>
  ///   <item>Both: Both local dispatch AND outbox write</item>
  ///   <item>Default (unwrapped): Outbox only (system default)</item>
  /// </list>
  /// </para>
  /// </remarks>
  /// <docs>fundamentals/dispatcher/message-cascade#routed-message-cascading</docs>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherCascadeTests.cs:LocalInvokeAsync_TupleWithEvent_AutoPublishesEventAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherRoutedCascadeTests.cs:CascadeFromResult_WithRouteLocal_InvokesLocalReceptorAsync</tests>
  // S3776: Core event cascade orchestration — complexity from routing modes, logging, and event tracking
#pragma warning disable S3776
  private async Task _cascadeEventsFromResultAsync<TResult>(TResult result, Type originalMessageType, IMessageEnvelope? sourceEnvelope,
      object? excludedResponse = null, CancellationToken cancellationToken = default) {
#pragma warning restore S3776
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
    if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
      var resultTypeName = result?.GetType().Name ?? "null";
      var origMsgTypeName = originalMessageType.Name;
      CascadeLogger.LogDebug("[CASCADE] CascadeEventsFromResult: ResultType={ResultType}, OriginalMessageType={OriginalMessageType}",
        resultTypeName, origMsgTypeName);
    }

    // Fast path: Skip if result is null
    if (object.Equals(result, default(TResult))) {
      CascadeLogger.LogWarning("[CASCADE] CascadeEventsFromResult: Result is null, skipping cascade");
      return;
    }

    // Look up receptor default routing from [DefaultRouting] attribute on the receptor
    // This is done via the generated GetReceptorDefaultRouting method
    var receptorDefault = _lookupReceptorDefaultRouting(originalMessageType);
    if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
      CascadeLogger.LogDebug("[CASCADE] CascadeEventsFromResult: ReceptorDefaultRouting={ReceptorDefault}", receptorDefault);
    }

    // Use MessageExtractor to find all IMessage instances with routing info
    // This handles tuples, arrays, nested structures, Routed<T> wrappers, etc. using ITuple interface (AOT-safe)
    var extractedCount = 0;
    foreach (var (msg, mode) in Internal.MessageExtractor.ExtractMessagesWithRouting(result, receptorDefault)) {
      // A response extracted for the caller of a local invoke is returned, not cascaded
      if (excludedResponse is not null && ReferenceEquals(msg, excludedResponse)) {
        continue;
      }
      extractedCount++;
      var messageType = msg.GetType();
      if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
        var msgTypeName = messageType.Name;
        CascadeLogger.LogDebug("[CASCADE] CascadeEventsFromResult: Extracted message {Count}: Type={MessageType}, Mode={Mode}",
          extractedCount, msgTypeName, mode);
      }

      // Generate eventId and track event in scoped/singleton trackers
      Guid? eventId = null;
      if (msg is IEvent) {
        eventId = _generateEventIdAndTrack(msg, messageType, sourceEnvelope, track: _persists(mode));
      }

      // Dispatch based on routing mode
      await _dispatchByModeAsync(msg, messageType, mode, sourceEnvelope, eventId, cancellationToken);
    }

    if (extractedCount == 0) {
      if (CascadeLogger.IsEnabled(LogLevel.Warning)) {
        // Not null: a null (default) result returned at the fast path above.
        var resultTypeName = result!.GetType().Name;
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
  /// Generates an event ID (UUIDv7), resolves/auto-generates StreamId, and tracks
  /// the event in both scoped and singleton trackers for sync coordination.
  /// </summary>
  private Guid _generateEventIdAndTrack(object msg, Type messageType, IMessageEnvelope? sourceEnvelope, bool track = true) {
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
    var eventId = _mintEmissionId(sourceEnvelope, messageType);
    var streamId = _streamIdExtractor?.ExtractStreamId(msg, messageType) ?? Guid.Empty;

    // Auto-generate StreamId based on [GenerateStreamId] attribute policy
    streamId = _autoGenerateStreamIdIfNeeded(msg, messageType, streamId);

    // Cascade StreamId propagation: inherit from source command when event's StreamId is still empty
    streamId = _cascadeStreamIdFromSourceIfNeeded(msg, messageType, streamId, sourceEnvelope);

    // Tracked for perspective sync only when it will reach a perspective: a LocalNoPersist event never
    // does, and a sync wait on it could only time out (#1410 follow-up)
    if (track) {
      _trackEventForSync(messageType, eventId, streamId);
    }

    if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
      var eventTypeName = messageType.Name;
      CascadeLogger.LogDebug("[CASCADE] CascadeEventsFromResult: Tracked event for sync - StreamId={StreamId}, EventType={EventType}, EventId={EventId}",
        streamId, eventTypeName, eventId);
    }
#pragma warning restore CA1848
    return eventId;
  }

  /// <summary>
  /// Mints the identity of an emitted event. When the emission is driven by an inbound message (the
  /// source envelope carries a message id), the id is derived from the handling (source message,
  /// producing service, handler, emitted type, ordinal within the handling), so a retry of that row
  /// re-derives the same ids and the store's primary keys turn the second copy into a counted no-op
  /// instead of a republish. A root emission with no source keeps a fresh time-ordered id.
  /// </summary>
  /// <docs>fundamentals/dispatcher/message-cascade#emission-identity</docs>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherEmissionIdentityTests.cs</tests>
  private Guid _mintEmissionId(IMessageEnvelope? sourceEnvelope, Type emittedType) {
    if (sourceEnvelope is null) {
      return ValueObjects.TrackedGuid.New();
    }
    // A nested local cascade hands receptors a wrapper that reports the inbound message's id and handler.
    // Their emissions anchor on the cascaded message that caused them (carried by the wrapper) so they
    // cannot collide with the top-level handler's emissions of the same type.
    var anchor = sourceEnvelope is CascadeEnvelopeWrapper { EmissionAnchor: { } nestedAnchor }
      ? nestedAnchor
      : sourceEnvelope.MessageId.Value;
    if (anchor == Guid.Empty) {
      return ValueObjects.TrackedGuid.New();
    }
    var ordinal = EmissionSequence.Next(sourceEnvelope);
    var eventId = EmissionIdentity.Derive(
      anchor,
      _instanceProvider.ServiceName,
      sourceEnvelope.DispatchContext?.HandlerName,
      TypeNameFormatter.Format(emittedType),
      ordinal);
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
    if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
      CascadeLogger.LogDebug("[CASCADE] Emission id derived from source {SourceMessageId} ordinal {Ordinal}: {EventId}",
        anchor, ordinal, eventId);
    }
#pragma warning restore CA1848
    return eventId;
  }

  /// <summary>
  /// Auto-generates a StreamId based on [GenerateStreamId] attribute policy if applicable.
  /// Returns the (potentially updated) streamId.
  /// </summary>
  private Guid _autoGenerateStreamIdIfNeeded(object msg, Type messageType, Guid streamId) {
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
    if (_streamIdExtractor is not null) {
      var (shouldGenerate, onlyIfEmpty) = _streamIdExtractor.GetGenerationPolicy(msg);
      if (shouldGenerate && (!onlyIfEmpty || streamId == Guid.Empty)) {
        var minted = ValueObjects.TrackedGuid.New();
        // A [StreamId] property as well as IHasStreamId, as the publish path sets it (#1410 follow-up)
        if (msg is IHasStreamId hasStreamId) {
          hasStreamId.StreamId = minted;
        } else {
          _streamIdExtractor.SetStreamId(msg, minted);
        }
        // The stream the message now reports, which is not always the one written: a keyed collective's
        // stream is derived from its key and ignores a write
        streamId = _streamIdExtractor.ExtractStreamId(msg, messageType) ?? minted;
        if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
          CascadeLogger.LogDebug("[STREAM_ID] Auto-generated StreamId={StreamId} for EventType={EventType} (OnlyIfEmpty={OnlyIfEmpty})",
            streamId, messageType.Name, onlyIfEmpty);
        }
      }
    }
#pragma warning restore CA1848
    return streamId;
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
      var newStreamId = ValueObjects.TrackedGuid.New();

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
  /// Propagates StreamId from the source envelope when the event's StreamId is empty.
  /// Returns the (potentially updated) streamId.
  /// </summary>
  private Guid _cascadeStreamIdFromSourceIfNeeded(object msg, Type messageType, Guid streamId, IMessageEnvelope? sourceEnvelope) =>
    streamId == Guid.Empty ? _inheritSourceStreamId(msg, messageType, sourceEnvelope) ?? streamId : streamId;

  /// <summary>
  /// The one rule by which a cascaded message that names no stream takes the stream of the message that caused it:
  /// the source payload's <c>[StreamId]</c> is written onto the message (through <see cref="IHasStreamId"/> or the
  /// generated setter) and returned; null when there is no source, no extractor, or the source names no stream. The
  /// dispatcher's cascade, outbox and no-strategy paths each carried a copy of it.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/StreamResolutionParityTests.cs</tests>
  private Guid? _inheritSourceStreamId(object message, Type messageType, IMessageEnvelope? sourceEnvelope) {
    if (sourceEnvelope is null || _streamIdExtractor is null) {
      return null;
    }
    var sourceStreamId = _streamIdExtractor.ExtractStreamId(sourceEnvelope.Payload, sourceEnvelope.Payload.GetType());
    if (sourceStreamId is not { } inherited || inherited == Guid.Empty) {
      return null;
    }
    if (message is IHasStreamId hasStreamId) {
      hasStreamId.StreamId = inherited;
    } else {
      _streamIdExtractor.SetStreamId(message, inherited);
    }
    Log.StreamIdPropagatedFromSource(CascadeLogger, inherited, sourceEnvelope.Payload.GetType().Name, messageType.Name);
    return inherited;
  }

  /// <summary>
  /// Tracks an emitted event in the scoped event tracker for same-request sync coordination.
  /// </summary>
  private void _trackInScopedTracker(Guid streamId, Type messageType, Guid eventId) {
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
    // Use accessor to get current scope's tracker (works with singleton Dispatcher)
    var scopedTracker = _scopedEventTracker ?? ScopedEventTrackerAccessor.CurrentTracker;
    if (scopedTracker is not null) {
      scopedTracker.TrackEmittedEvent(streamId, messageType, eventId);
      if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
        CascadeLogger.LogDebug("[SYNC_DEBUG] Tracked in SCOPED tracker: StreamId={StreamId}, EventType={EventType}, EventId={EventId}",
          streamId, messageType.Name, eventId);
      }
    } else if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
      CascadeLogger.LogDebug("[SYNC_DEBUG] SCOPED tracker is NULL - event not tracked for same-scope sync: EventType={EventType}, EventId={EventId}",
        messageType.Name, eventId);
    }
#pragma warning restore CA1848
  }

  /// <summary>
  /// Tracks an emitted event in the singleton tracker for cross-scope sync coordination.
  /// This enables Request 2 to wait for events emitted by Request 1.
  /// </summary>
  private void _trackInSingletonTracker(Guid streamId, Type messageType, Guid eventId) {
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
    if (_syncEventTracker is not null && _trackedEventTypeRegistry is not null) {
      var perspectiveNames = _trackedEventTypeRegistry.GetPerspectiveNames(messageType);
      if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
        CascadeLogger.LogDebug("[SYNC_DEBUG] SINGLETON tracker check: EventType={EventType}, PerspectiveCount={Count}, Perspectives=[{Perspectives}]",
          TypeNameFormatter.DisplayName(messageType), perspectiveNames.Count, string.Join(", ", perspectiveNames));
      }
      foreach (var perspectiveName in perspectiveNames) {
        _syncEventTracker.TrackEvent(messageType, eventId, streamId, perspectiveName);
        if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
          CascadeLogger.LogDebug("[SYNC_DEBUG] Tracked in SINGLETON tracker: StreamId={StreamId}, EventType={EventType}, EventId={EventId}, Perspective={Perspective}",
            streamId, messageType.Name, eventId, perspectiveName);
        }
      }
    } else if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
      CascadeLogger.LogDebug("[SYNC_DEBUG] SINGLETON tracker DISABLED - _syncEventTracker={HasTracker}, _trackedEventTypeRegistry={HasRegistry}",
        _syncEventTracker is not null, _trackedEventTypeRegistry is not null);
    }
#pragma warning restore CA1848
  }

  /// <summary>
  /// Registers the envelope a send's local receptors look up: one carrying the stored message's id when
  /// the send stores a copy (#1464), otherwise the local envelope itself.
  /// </summary>
  private void _registerSendEnvelope<TMessage>(TMessage message, IMessageContext context, MessageEnvelope<TMessage> envelope,
      MessageId? storedId, CallerLocation caller) where TMessage : notnull {
    if (_envelopeRegistry is null) {
      return;
    }
    if (storedId is { } id) {
      _envelopeRegistry.Register(_createEnvelope(message, context, envelope.DispatchContext, caller, id, stampMessage: false));
    } else {
      _envelopeRegistry.Register(envelope);
    }
  }

  /// <summary>
  /// Carries out one cascaded message as the Cascade policy decides: the event store first (cause before
  /// effect), then the local receptors (generated, other assemblies, runtime), then the outbox copy,
  /// marked when handled here. The outbox copy is written whatever the local outcome; a local failure
  /// surfaces after it.
  /// </summary>
  private async Task _carryOutCascadeAsync(
      IMessage message,
      Type messageType,
      Dispatch.DispatchModes mode,
      Func<IMessageEnvelope?>? localEnvelopeFor,
      IMessageEnvelope? sourceEnvelope,
      Guid? eventId,
      CancellationToken cancellationToken) {
    var decision = _decide(DispatchPolicies.Cascade, message, messageType,
      _hasLocalPublishReceptors(messageType), options: null, route: mode);
    if (await _cascadeEndsHereAsync(decision, message, messageType, sourceEnvelope, cancellationToken).ConfigureAwait(false)) {
      return;
    }
    _throwIfRejected(in decision, messageType);

    // The copy's id is minted here when the caller did not give one, so the local run's envelope carries
    // the id the copy is stored under (#1464)
    var copyId = _writesCopy(in decision) ? eventId ?? MessageId.New().Value : eventId;
    if (decision.WriteEventStoreOnly && message is IEvent) {
      await CascadeToEventStoreOnlyAsync(message, messageType, sourceEnvelope, copyId);
    }

    // The copy is started before the local run, as a publish's is, and the handlers' own writes wait for
    // it: the cause reaches the outbox before its effects (#1410 follow-up). It does not wait on the
    // local outcome.
    var copyWrite = decision.WriteOutbox ? _startCascadeCopy(message, messageType, sourceEnvelope, copyId, in decision, cancellationToken) : null;

    Exception? localFailure = null;
    if (decision.Local == LocalRun.Now) {
      var outerCause = _causeCopyWrite.Value;
      _causeCopyWrite.Value = copyWrite ?? outerCause;
      try {
        await _runCascadeHandlersAsync(message, messageType, localEnvelopeFor?.Invoke(), sourceEnvelope,
          _writesCopy(in decision) ? new MessageId(copyId!.Value) : null, cancellationToken).ConfigureAwait(false);
      } catch (Exception ex) when (copyWrite is not null) {
        localFailure = ex;
      } finally {
        _causeCopyWrite.Value = outerCause;
      }
    }

    if (copyWrite is not null) {
      try {
        await copyWrite.ConfigureAwait(false);
      } catch (Exception outboxFailure) when (localFailure is not null) {
        throw new AggregateException(localFailure, outboxFailure);
      }
    }
    _rethrowIfFailed(localFailure);
  }

  /// <summary>
  /// Whether a cascade ends before the decision is carried out: routed nowhere, or a returned value that is
  /// neither a command nor an event, with no receptor of its own here. That value goes to the receptors of
  /// an interface or base type it implements, as a mediator would; with none it is refused (false), unless
  /// <see cref="WhizbangCoreOptions.RequireReceptorForReturnedResult"/> is off, when it is skipped (#1418).
  /// </summary>
  private async Task<bool> _cascadeEndsHereAsync(DispatchDecision decision, IMessage message, Type messageType,
      IMessageEnvelope? sourceEnvelope, CancellationToken cancellationToken) {
    if (decision.Reject == DispatchRejection.NoRoute) {
      // A cascade routed nowhere (Route.None) has nothing to do; it has always been skipped quietly
      return true;
    }
    if (decision.Reject != DispatchRejection.NoHandler || message is ICommand || message is IEvent) {
      return false;
    }
    if (await _runBaseTypeReceptorsAsync(message, messageType, sourceEnvelope, cancellationToken).ConfigureAwait(false)) {
      return true;
    }
    if (_coreOptions.RequireReceptorForReturnedResult) {
      return false;
    }
    if (CascadeLogger.IsEnabled(LogLevel.Warning)) {
      Log.UnhandledResultSkipped(CascadeLogger, messageType.Name);
    }
    return true;
  }

  /// <summary>
  /// A cascade's local run: every local receptor (generated, other assemblies, runtime) with the
  /// message's envelope registered for them to find, then tags, as on a publish.
  /// </summary>
  private async Task _runCascadeHandlersAsync(IMessage message, Type messageType, IMessageEnvelope? localEnvelope,
      IMessageEnvelope? sourceEnvelope, MessageId? copyId, CancellationToken cancellationToken) {
    var registered = _registerLocalRunEnvelope(message, sourceEnvelope, stampMessage: copyId is null, copyId);
    try {
      await _expandIfOwnedCompositeAsync(message, messageType, copyId).ConfigureAwait(false);
      if (GetUntypedReceptorPublisher(messageType) is { } publisher) {
        await publisher(message, localEnvelope, cancellationToken);
      }
      await _publishToForeignLookupsAsync(message, messageType, localEnvelope, cancellationToken).ConfigureAwait(false);
      await _publishToRuntimeLocalReceptorsAsync(message, messageType, cancellationToken).ConfigureAwait(false);
      // Tags after the handlers, as on a publish (#1410 follow-up)
      await ProcessTagsIfEnabledAsync(message, messageType, cancellationToken).ConfigureAwait(false);
      await _invokeLocalRunPostLifecycleAsync(message, messageType, localEnvelope ?? registered, sourceEnvelope, copyId, cancellationToken)
        .ConfigureAwait(false);
      await _startLocalRunDetachedHooksAsync(message, messageType, localEnvelope ?? registered, sourceEnvelope, copyId).ConfigureAwait(false);
    } finally {
      if (registered is not null) {
        _envelopeRegistry!.Unregister(message);
      }
    }
  }

  /// <summary>
  /// Starts writing a cascaded message's outbox copy, marked as the decision says, and returns the write.
  /// The mark covers only the synchronous start of the write, which captures it.
  /// </summary>
#pragma warning disable RCS1229 // Awaiting here would hold the mark across the caller's local run.
  private Task _startCascadeCopy(IMessage message, Type messageType, IMessageEnvelope? sourceEnvelope, Guid? eventId, in DispatchDecision decision,
      CancellationToken cancellationToken) {
    var previous = _outboxCopyMark.Value;
    var previousToken = _copyWriteToken.Value;
    _outboxCopyMark.Value = _markFor(in decision);
    _copyWriteToken.Value = new CopyWriteToken(cancellationToken);
    try {
      return CascadeToOutboxAsync(message, messageType, sourceEnvelope, eventId);
    } finally {
      _outboxCopyMark.Value = previous;
      _copyWriteToken.Value = previousToken;
    }
  }
#pragma warning restore RCS1229

  /// <summary>
  /// Dispatches a cascaded message based on routing mode: local, event store, and/or outbox.
  /// </summary>
  private Task _dispatchByModeAsync(IMessage msg, Type messageType, Dispatch.DispatchModes mode, IMessageEnvelope? sourceEnvelope, Guid? eventId,
      CancellationToken cancellationToken = default) =>
    // The Cascade policy decides (#1395); local dispatch sees no envelope on this path, as before, and
    // the caller's token reaches the cascaded receptors and outbox write
    _carryOutCascadeAsync(msg, messageType, mode, localEnvelopeFor: null, sourceEnvelope, eventId, cancellationToken);

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
  /// <tests>tests/Whizbang.Core.Tests/Tags/DispatcherTagProcessingLoggingTests.cs</tests>
  /// <tests>tests/Whizbang.Core.Tests/Tags/DispatcherTagProcessingLoggingTests.cs:ProcessorCompletingLater_IsAwaitedBeforeTheStepCompletesAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Tags/DispatcherTagProcessingLoggingTests.cs:ProcessorFaulted_ReachesTheCallerAsync</tests>
  internal ValueTask ProcessTagsIfEnabledAsync(object message, Type messageType, CancellationToken ct = default) {
    // Runs after every dispatch, so it allocates nothing while debug is off (#1756): the log calls are
    // source-generated and check the level before building anything, and the step completes synchronously unless
    // the processor does not, so no async state machine is created on the common paths
    var logger = DispatcherLogger;
    Log.TagProcessingEntered(logger, messageType.Name);

    // Skip if tag processing is disabled
    if (!_coreOptions.EnableTagProcessing) {
      Log.TagProcessingDisabled(logger);
      return ValueTask.CompletedTask;
    }

    // Skip immediate processing if using lifecycle stage mode
    if (_coreOptions.TagProcessingMode != TagProcessingMode.AfterReceptorCompletion) {
      Log.TagProcessingModeSkipped(logger, _coreOptions.TagProcessingMode);
      return ValueTask.CompletedTask;
    }

    // Skip if no processor is registered
    if (_messageTagProcessor is null) {
      Log.NoTagProcessorRegistered(logger);
      return ValueTask.CompletedTask;
    }

    Log.TagProcessingCalling(logger);
    // Use static AsyncLocal accessor - designed for singleton services like Dispatcher
    var scope = ScopeContextAccessor.CurrentContext;
    var processing = _messageTagProcessor.ProcessTagsAsync(message, messageType, LifecycleStage.AfterReceptorCompletion, scope, ct);
    if (!processing.IsCompletedSuccessfully) {
      return _awaitTagProcessingAsync(processing, logger);
    }
    processing.GetAwaiter().GetResult();
    Log.TagProcessingCompleted(logger);
    return ValueTask.CompletedTask;
  }

  // A processor that has not finished (or has failed) is awaited here, so its exception reaches the caller unchanged
  private static async ValueTask _awaitTagProcessingAsync(ValueTask processing, ILogger logger) {
    await processing;
    Log.TagProcessingCompleted(logger);
  }

  /// <summary>
  /// Starts the ImmediateDetached lifecycle receptors for the dispatched message, after the business receptor,
  /// cascade and tag processing, and returns without waiting for them (#1393): a detached stage runs in its
  /// own scope, nobody waits for it, and its exception is logged, never returned to the caller.
  /// </summary>
  /// <docs>fundamentals/lifecycle/lifecycle-stages#immediate-async</docs>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherUncoveredPathsTests.cs:LocalInvokeAsync_Void_WithImmediateDetachedReceptors_InvokesScopedInvokerAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherUncoveredPathsTests.cs:LocalInvokeAsync_Void_WithReceptorRegistry_NoImmediateDetachedReceptors_SkipsAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherUncoveredPathsTests.cs:LocalInvokeAsync_Void_WithTraceStore_NoReceptorRegistry_SkipsImmediateDetachedAsync</tests>
  /// <tests>tests/Whizbang.Core.Component.Tests/Dispatcher/ImmediateDetachedOncePerDispatchComponentTests.cs:ALocalDispatch_ReturnsWithoutWaitingForImmediateDetachedAsync</tests>
  /// <tests>tests/Whizbang.Core.Component.Tests/Dispatcher/ImmediateDetachedOncePerDispatchComponentTests.cs:AnImmediateDetachedFailure_NeverReachesTheCallerAsync</tests>
  private ValueTask _invokeImmediateDetachedReceptorsAsync(IMessageEnvelope envelope, Type messageType) {
    return _startDetachedStagesAsync(envelope, messageType, _detachedAfterHandlerStages);
  }

  // The detached stages a send or local invoke starts after its handler: the LocalImmediateDetached hooks (#1392),
  // then ImmediateDetached (#1393)
  private static readonly LifecycleStage[] _detachedAfterHandlerStages = [LifecycleStage.LocalImmediateDetached, LifecycleStage.ImmediateDetached];
  private static readonly LifecycleStage[] _localImmediateDetachedStage = [LifecycleStage.LocalImmediateDetached];

  /// <summary>
  /// Starts the LocalImmediateDetached hooks for a message a publish or cascade just handled in-process, after its
  /// handlers, without waiting for them (#1392).
  /// </summary>
  private ValueTask _startLocalRunDetachedHooksAsync(object message, Type messageType, IMessageEnvelope? envelope,
      IMessageEnvelope? sourceEnvelope, MessageId? storedId) {
    if (_receptorRegistry is null || _receptorRegistry.GetReceptorsFor(messageType, LifecycleStage.LocalImmediateDetached).Count == 0) {
      return ValueTask.CompletedTask;
    }
    return _startDetachedStagesAsync(_localRunEnvelopeFor(message, envelope, sourceEnvelope, storedId), messageType,
      _localImmediateDetachedStage);
  }

  private ValueTask _startDetachedStagesAsync(IMessageEnvelope envelope, Type messageType, LifecycleStage[] stages) {
    // Nothing to start without a registry or a receptor at one of the stages for this message type
    if (_receptorRegistry is null || !Array.Exists(stages, stage => _receptorRegistry.GetReceptorsFor(messageType, stage).Count > 0)) {
      return ValueTask.CompletedTask;
    }

    // This message was admitted at its entry; its stages do not refuse it again (#1419). The mark, like the
    // caller's security context, flows into the background task with the execution context.
    LocalDispatchAdmission.Mark();
    // Not the caller's token: that call has returned by the time the stage runs, and a detached stage outlives it.
    // Recorded, so the host waits for it instead of disposing the provider under it (#1721).
    _detachedWork.Track(_runDetachedStagesAsync(envelope, stages));
    return ValueTask.CompletedTask;
  }

  private Task _runDetachedStagesAsync(IMessageEnvelope envelope, LifecycleStage[] stages) =>
    Task.Run(async () => {
      try {
        // Dispatcher is a singleton: the scoped IReceptorInvoker comes from a scope of the stage's own
        await using var scope = _scopeFactory.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<IReceptorInvoker>() is not { } scopedInvoker) {
          return;
        }
        foreach (var stage in stages) {
          var context = new LifecycleExecutionContext { CurrentStage = stage, MessageSource = MessageSource.Local };
          await _invokeDetachedStageAsync(scopedInvoker, envelope, stage, context).ConfigureAwait(false);
        }
      } catch (Exception ex) {
        Log.DetachedStageFailed(DispatcherLogger, ex, LifecycleStage.ImmediateDetached, envelope.MessageId.Value);
      }
    }, CancellationToken.None);

  // One stage's failure is logged and does not stop the next stage
  private async Task _invokeDetachedStageAsync(IReceptorInvoker invoker, IMessageEnvelope envelope, LifecycleStage stage,
      LifecycleExecutionContext context) {
    try {
      await invoker.InvokeAsync(envelope, stage, context, CancellationToken.None).ConfigureAwait(false);
    } catch (Exception ex) {
      Log.DetachedStageFailed(DispatcherLogger, ex, stage, envelope.MessageId.Value);
    }
  }

  /// <summary>
  /// Invokes PostLifecycle lifecycle receptors for the dispatched message.
  /// Called after the business receptor completes in the local dispatch path.
  /// Local events don't go through perspectives, so PostLifecycle fires immediately.
  /// Creates a scope to resolve scoped IReceptorInvoker (Dispatcher is a singleton).
  /// </summary>
  /// <docs>fundamentals/lifecycle/lifecycle-stages#post-lifecycle</docs>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherPostLifecycleCoverageTests.cs:LocalInvokeAsync_Void_FiresPostLifecycleDetached_WhenReceptorsRegisteredAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherPostLifecycleCoverageTests.cs:LocalInvokeAsync_Void_FiresBothAsyncAndInline_WhenBothRegisteredAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Lifecycle/PostLifecyclePipelineTests.cs:Dispatcher_NoWhenAll_FiresPostLifecycleDetachedAsync</tests>
  private async ValueTask _invokePostLifecycleReceptorsAsync(
      IMessageEnvelope envelope,
      object message,
      Type messageType,
      CancellationToken ct = default) {
    // Short-circuit if no receptor registry available
    if (_receptorRegistry is null) {
      return;
    }
    // This message was admitted at its entry; its stages do not refuse it again (#1419)
    LocalDispatchAdmission.Mark();

    // Create a scope to resolve scoped services
    // Dispatcher is a singleton — cannot inject scoped services directly
    // NOTE: We do NOT short-circuit on compile-time receptor count here because
    // runtime-registered receptors (via IReceptorRegistry.Register) and tags
    // must still fire even when no compile-time receptors exist.
    await using var scope = _scopeFactory.CreateAsyncScope();

    // Prefer coordinator path — handles receptors + tags
    if (_lifecycleCoordinator is not null) {
      var eventId = envelope.MessageId.Value;
      var tracking = _lifecycleCoordinator.BeginTracking(
        eventId, envelope, LifecycleStage.PostLifecycleDetached, MessageSource.Local);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleDetached, scope.ServiceProvider, ct).ConfigureAwait(false);
      await tracking.AdvanceToAsync(LifecycleStage.PostLifecycleInline, scope.ServiceProvider, ct).ConfigureAwait(false);
      _lifecycleCoordinator.AbandonTracking(eventId);
      return;
    }

    // Fallback: direct invocation when coordinator not registered
    var scopedInvoker = scope.ServiceProvider.GetService<IReceptorInvoker>();
    if (scopedInvoker is null) {
      return;
    }

    var context = new LifecycleExecutionContext {
      CurrentStage = LifecycleStage.PostLifecycleDetached,
      MessageSource = MessageSource.Local
    };

    // Always invoke both PostLifecycle stages — ReceptorInvoker handles
    // the no-receptor case (still processes tags as lifecycle observers). ImmediateDetached is not
    // re-fired after them: it fires once, after the business receptor (#1386)
    await scopedInvoker.InvokeAsync(envelope, LifecycleStage.PostLifecycleDetached, context, ct).ConfigureAwait(false);
    await scopedInvoker.InvokeAsync(envelope, LifecycleStage.PostLifecycleInline,
      context with { CurrentStage = LifecycleStage.PostLifecycleInline }, ct).ConfigureAwait(false);

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
  /// Checks if there are any ImmediateDetached receptors registered for the given message type.
  /// Used to bypass fast paths when ImmediateDetached processing is needed.
  /// </summary>
  private bool _hasImmediateDetachedReceptors(Type messageType) {
    return _receptorRegistry is not null
      && (_receptorRegistry.GetReceptorsFor(messageType, LifecycleStage.ImmediateDetached).Count > 0
        || _receptorRegistry.GetReceptorsFor(messageType, LifecycleStage.LocalImmediateDetached).Count > 0);
  }

  /// <summary>
  /// Checks if there are any PostLifecycle receptors registered for the given message type.
  /// Used to bypass fast paths when PostLifecycle processing is needed.
  /// </summary>
  private bool _hasPostLifecycleReceptors(Type messageType) {
    if (_receptorRegistry is null) {
      return false;
    }

    return _receptorRegistry.GetReceptorsFor(messageType, LifecycleStage.PostLifecycleDetached).Count > 0
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
  /// <docs>fundamentals/dispatcher/message-cascade#auto-cascade-to-outbox</docs>
  /// <tests>tests/Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:Generator_WithEventReturningReceptor_GeneratesCascadeToOutboxAsync</tests>
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
  /// Called by CascadeMessageAsync when DispatchModes.EventStore flag is set without DispatchModes.Outbox.
  /// Events are stored to wh_event_store and perspective events are created, but transport is skipped.
  /// </para>
  /// </remarks>
  /// <docs>fundamentals/dispatcher/message-cascade#event-store-only</docs>
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
  /// <tests>tests/Whizbang.Core.Component.Tests/Dispatcher/DispatcherTests.cs:Publish_WithEvent_ShouldNotifyAllHandlersAsync</tests>
  /// <tests>tests/Whizbang.Core.Component.Tests/Dispatcher/DispatchTangentialParityComponentTests.cs:EveryEntryThatRunsHereAndWritesACopy_HandsTheHandlersTheMessageAsStoredAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DispatchExecutionIntegrationTests.cs:MessageWithALocalHandler_TheHandlerSeesTheSentTimeAndCorrelationTheStoredRowCarriesAsync</tests>
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
    // A Routed<T> passed straight to a publish is honored (#1395), as on a send
    if (eventData is IRouted) {
      return await PublishAsync(eventData, new DispatchOptions()).ConfigureAwait(false);
    }
#pragma warning restore S2955

    _throwIfSchemaNotReady();
    _throwIfLocalDispatchNeedsContext(eventData);
    var sw = Stopwatch.StartNew();
    var eventType = eventData.GetType();
    var eventTypeName = eventType.Name;
    // #587: the publish seam is where a re-emission cascade becomes visible — a service
    // publishing a type it also consumes. Resolved lazily and nullable: hosts without the
    // registry (or diagnostic) pay nothing.
    _resolveReEmissionDiagnostic()?.RecordEmission(TypeNameFormatter.Format(eventType));
    try {

      // Auto-generate StreamId for events with [GenerateStreamId] attribute
      _autoGenerateStreamIdIfNeeded(eventData, eventType);

      // Create MessageId once - used for the outbox row and, for an event, as its event store event_id (store_outbox_messages -> _emit_event_store_chain)
      var messageId = MessageId.New();

      // Extract stream ID early — needed for both sync tracking and delivery receipt
      var streamId = _streamIdExtractor?.ExtractStreamId(eventData, eventType);

      // Track in singleton tracker for cross-scope sync (mirrors cascade path behavior).
      // Without this, events published via PublishAsync are invisible to IPerspectiveSyncAwaiter,
      // causing WaitForStreamAsync to return NoPendingEvents immediately.
      if (streamId.HasValue) {
        _trackEventForSync(eventType, messageId.Value, streamId.Value);
      }

      // The Publish policy decides (#1395): every local receptor runs here, and the event goes out
      var decision = _decide(DispatchPolicies.Publish, eventData, eventType, _hasLocalPublishReceptors(eventType), options: null, unmarkedKind: MessageTraits.Event);
      _throwIfRejected(in decision, eventType);

      // Get strongly-typed delegate from generated code
      var publisher = GetReceptorPublisher(eventData, eventType);


      // Start outbox publishing concurrently with the local receptor.
      // The receptor (publisher) can take 30+ seconds for heavy operations — we don't want
      // to delay cross-service event delivery by that amount. Starting the outbox first also
      // ensures FIFO order: the original event enters the outbox before any cascaded events
      // produced by the receptor.
      // Capture the establishing context SYNCHRONOUSLY here (the calling site) so the emitted child inherits
      // correlation+causation+scope from the hop even when the outbox builder runs where AsyncLocal no longer
      // flows (a detached-stage or worker emit). Without this, PublishAsync passed sourceEnvelope=null and the
      // child fabricated a fresh correlation + null scope across the boundary.
      var establishingEnvelope = _captureAmbientSourceEnvelope();
      // The local path runs below, so the stored envelope records it (see _recordLocalDispatchAsync), and
      // the copy is marked as handled here (#1398). The outbox write does not wait on the local outcome.
      var outboxTask = _publishCopyAsync(eventData, eventType, messageId, establishingEnvelope, options: null, decision);

      if (decision.Local == LocalRun.Now) {
        // CancellationToken.None: this PublishAsync overload takes no token, and the local
        // receptor invocation is likewise uncancellable — explicit opt-out, not an oversight.
        await _runPublishReceptorsAsync(publisher, eventData, eventType, outboxTask, _writesCopy(in decision) ? messageId : null, CancellationToken.None);
      }

      await outboxTask;

      _dispatcherMetrics?.MessagesDispatched.Add(1,
        new KeyValuePair<string, object?>(METRIC_MESSAGE_TYPE, eventTypeName),
        new KeyValuePair<string, object?>(METRIC_PATTERN, "publish"));

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
        new KeyValuePair<string, object?>(METRIC_MESSAGE_TYPE, eventTypeName),
        new KeyValuePair<string, object?>(METRIC_ERROR_TYPE, ex.GetType().Name));
      throw;
    } finally {
      sw.Stop();
      _dispatcherMetrics?.PublishDuration.Record(sw.Elapsed.TotalMilliseconds);
    }
  }

  /// <summary>
  /// Publishes an event to all registered handlers with dispatch options.
  /// Future <see cref="DispatchOptions.ScheduledFor"/> gates the in-process
  /// local receptor the same way <c>wh_outbox.scheduled_for</c> (mig 040)
  /// gates cross-pod delivery — the saga watchdog re-arm path relies on this
  /// to avoid the cascade-abandon failure mode.
  /// </summary>
  /// <docs>fundamentals/sagas/completion-orchestration</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Dispatcher/DispatcherTests.cs:PublishAsync_WithDispatchOptions_CompletesAsync</tests>
  /// <tests>tests/Whizbang.Core.Component.Tests/Dispatcher/DispatcherTests.cs:PublishAsync_WithCanceledToken_ThrowsOperationCanceledExceptionAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherScheduledForLocalReceptorTests.cs:PublishAsync_WithScheduledForInFuture_DoesNotInvokeLocalReceptorInlineAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherScheduledForLocalReceptorTests.cs:PublishAsync_WithoutScheduledFor_InvokesLocalReceptorInlineAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherScheduledForLocalReceptorTests.cs:PublishAsync_WithScheduledForInPast_InvokesLocalReceptorInlineAsync</tests>
  /// <tests>tests/Whizbang.Core.Component.Tests/Dispatcher/DispatchTangentialParityComponentTests.cs:EveryEntryThatRunsHereAndWritesACopy_HandsTheHandlersTheMessageAsStoredAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DispatchExecutionIntegrationTests.cs:MessageWithALocalHandler_TheHandlerSeesTheSentTimeAndCorrelationTheStoredRowCarriesAsync</tests>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public Task<IDeliveryReceipt> PublishAsync<TEvent>(TEvent eventData, DispatchOptions options) {
#pragma warning disable S2955 // Generic parameters not constrained to reference types should not be compared to 'null'
    if (eventData == null) {
      throw new ArgumentNullException(nameof(eventData));
    }
#pragma warning restore S2955
    // A Routed<T> passed straight to a publish is honored (#1395): its message is published under its
    // route. A RoutedNone is refused as a send refuses it.
    if (eventData is IRouted) {
      var (inner, route) = _unwrapRouted(eventData);
      return _publishWithOptionsAsync(inner, options, route);
    }
    return _publishWithOptionsAsync(eventData, options, route: null);
  }

  private async Task<IDeliveryReceipt> _publishWithOptionsAsync<TEvent>(TEvent eventData, DispatchOptions options, DispatchModes? route) {

    options = _withTimeout(options, out var timeoutScope);
    using var timeoutDisposal = timeoutScope;
    options.CancellationToken.ThrowIfCancellationRequested();

    _throwIfSchemaNotReady();
    _throwIfLocalDispatchNeedsContext(eventData);
    var sw = Stopwatch.StartNew();
    var eventType = eventData!.GetType();
    var eventTypeName = eventType.Name;
    // #587: the publish seam is where a re-emission cascade becomes visible — a service
    // publishing a type it also consumes. Resolved lazily and nullable: hosts without the
    // registry (or diagnostic) pay nothing.
    _resolveReEmissionDiagnostic()?.RecordEmission(TypeNameFormatter.Format(eventType));
    try {

      // Auto-generate StreamId for events with [GenerateStreamId] attribute
      _autoGenerateStreamIdIfNeeded(eventData, eventType);

      var messageId = MessageId.New();

      // Tracked for perspective sync as the plain overload tracks it (#1410 follow-up)
      if (_streamIdExtractor?.ExtractStreamId(eventData, eventType) is { } trackedStreamId) {
        _trackEventForSync(eventType, messageId.Value, trackedStreamId);
      }

      options.CancellationToken.ThrowIfCancellationRequested();

      // The Publish policy (or the caller's selected one) decides (#1395), including a schedule: a
      // future ScheduledFor defers local handling to the copy that comes back (#1401)
      var decision = _decide(DispatchPolicies.Publish, eventData, eventType, _hasLocalPublishReceptors(eventType), options, route, unmarkedKind: MessageTraits.Event);
      _throwIfRejected(in decision, eventType);


      // Start outbox concurrently with receptor (see other overload for rationale).
      // options.ScheduledFor flows through to the outbox row's scheduled_for column so
      // wh_outbox's pickup query (mig 040) gates publication until the time elapses. The composite also
      // goes over transport here (step 1.2) for other subscribing services.
      // Capture the establishing context synchronously (see the other overload) so a detached/worker emit's
      // child inherits identity+scope from the hop rather than fabricating a fresh root across the boundary.
      var establishingEnvelope = _captureAmbientSourceEnvelope();

      // ScheduledFor gates the in-process local receptors the same way it gates the outbox pickup query:
      // without it the local receptor fired inline despite a future schedule, which once made a saga
      // watchdog re-arm several ticks within milliseconds. The policy decides it (LocalRun.AtScheduledTime).
      var outboxTask = _publishCopyAsync(eventData, eventType, messageId, establishingEnvelope, options, decision);
      if (decision.Local == LocalRun.Now) {
        await _runPublishReceptorsAsync(GetReceptorPublisher(eventData, eventType), eventData, eventType, outboxTask, _writesCopy(in decision) ? messageId : null, options.CancellationToken);
      }

      await outboxTask;

      _dispatcherMetrics?.MessagesDispatched.Add(1,
        new KeyValuePair<string, object?>(METRIC_MESSAGE_TYPE, eventTypeName),
        new KeyValuePair<string, object?>(METRIC_PATTERN, "publish"));

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
        new KeyValuePair<string, object?>(METRIC_MESSAGE_TYPE, eventTypeName),
        new KeyValuePair<string, object?>(METRIC_ERROR_TYPE, ex.GetType().Name));
      throw;
    } finally {
      sw.Stop();
      _dispatcherMetrics?.PublishDuration.Record(sw.Elapsed.TotalMilliseconds);
    }
  }

  /// <summary>
  /// Publishes an event at most once per claim key. Composes
  /// <see cref="IClaimedEmissionStore.TryClaimAsync"/> with
  /// <see cref="PublishAsync{TEvent}(TEvent)"/>: the first caller to claim the
  /// key emits the event and returns <c>true</c>; concurrent callers with the
  /// same key no-op and return <c>false</c>. A publish that fails gives its claim
  /// back, so a retry can publish (#1414); the publish runs under the caller's token.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherPublishOnceTests.cs:PublishOnceAsync_PublishThrowsOutsideATransaction_ReleasesTheClaimSoARetryPublishesAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherPublishOnceTests.cs:PublishOnceAsync_PassesTheCallersTokenToThePublishAsync</tests>
  /// <docs>fundamentals/dispatcher/publish-once</docs>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public async Task<bool> PublishOnceAsync<TEvent>(string claimKey, TEvent eventData, CancellationToken cancellationToken = default) {
    ArgumentException.ThrowIfNullOrWhiteSpace(claimKey);
#pragma warning disable S2955 // Generic parameters not constrained to reference types should not be compared to 'null'
    if (eventData == null) {
      throw new ArgumentNullException(nameof(eventData));
    }
#pragma warning restore S2955

    cancellationToken.ThrowIfCancellationRequested();

    // Resolve the claim store from a per-call scope. IClaimedEmissionStore is
    // typically scoped (e.g. EFCoreClaimedEmissionStore holds a DbContext); a
    // singleton Dispatcher must not capture a scoped instance.
    await using var scope = _scopeFactory.CreateAsyncScope();
    var claimStore = scope.ServiceProvider.GetService<IClaimedEmissionStore>()
      ?? throw new InvalidOperationException(
        "IClaimedEmissionStore is not registered. Call AddWhizbangClaimedEmissionStore() (Postgres driver) or register an implementation before using PublishOnceAsync.");

    var eventTypeName = eventData.GetType().Name;

    var claimed = await claimStore.TryClaimAsync(claimKey, TrackedGuid.New(), cancellationToken).ConfigureAwait(false);
    if (!claimed) {
      _dispatcherMetrics?.PublishOnceClaimsLost.Add(1,
        new KeyValuePair<string, object?>(METRIC_MESSAGE_TYPE, eventTypeName));
      return false;
    }

    _dispatcherMetrics?.PublishOnceClaimsWon.Add(1,
      new KeyValuePair<string, object?>(METRIC_MESSAGE_TYPE, eventTypeName));
    Exception publishFailure;
    try {
      // The publish runs under the caller's token (#1414)
      await PublishAsync(eventData, new DispatchOptions { CancellationToken = cancellationToken }).ConfigureAwait(false);
      return true;
    } catch (Exception ex) {
      publishFailure = ex;
    }
    // A claim kept after its publish failed makes every retry a duplicate, so the event would never be
    // published. Inside a transaction the rollback undoes the claim too; outside one it is given back
    // here (#1414). The release is not canceled with the publish: it must run to give the key back.
    try {
      await claimStore.ReleaseAsync(claimKey, CancellationToken.None).ConfigureAwait(false);
    } catch (Exception releaseFailure) {
      throw new AggregateException(publishFailure, releaseFailure);
    }
    // Rethrown with its original stack trace. The release is awaited outside the catch: an await inside one
    // leaves a sequence point after its rethrow that nothing can reach.
    return await Task.FromException<bool>(publishFailure).ConfigureAwait(false);
  }

  /// <summary>
  /// Cascades a message with explicit routing mode.
  /// Called by IEventCascader after resolving routing from wrappers and attributes.
  /// </summary>
  /// <docs>fundamentals/dispatcher/message-cascade#cascade-to-outbox</docs>
  /// <docs>fundamentals/security/message-security#security-context-in-event-cascades</docs>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherCoverageSweepOutboxTests.cs:CascadeMessageAsync_EventWithIHasStreamId_InheritsStreamIdFromSourceAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherCoverageSweepRoutedCascadeTests.cs:CascadeMessageAsync_OwnedCommandOutboxMode_NoHandler_ThrowsAndNeverReachesTheOutboxAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherCoverageSweepRoutedCascadeTests.cs:CascadeMessageAsync_NonOwnedCommandOutboxMode_StaysOnOutboxAsync</tests>
  /// <tests>tests/Whizbang.Core.Component.Tests/Dispatcher/DispatchTangentialParityComponentTests.cs:EveryEntryThatRunsHereAndWritesACopy_HandsTheHandlersTheMessageAsStoredAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DispatchExecutionIntegrationTests.cs:MessageWithALocalHandler_TheHandlerSeesTheSentTimeAndCorrelationTheStoredRowCarriesAsync</tests>
  // S3776: Multi-mode cascade dispatch — complexity from conditional logging + Local/EventStore/Outbox routing paths
#pragma warning disable S3776
  public async Task CascadeMessageAsync(IMessage message, IMessageEnvelope? sourceEnvelope, Dispatch.DispatchModes mode, CancellationToken cancellationToken = default) {
    // Dispatch writes, so every entry refuses while the schema is not ready (#1410 follow-up)
    _throwIfSchemaNotReady();
    // A cascade with a source envelope continues a flow that was already admitted; only a new one is checked
    if (sourceEnvelope is null) {
      _throwIfLocalDispatchNeedsContext(message);
    }
#pragma warning restore S3776
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
      eventId = _mintEmissionId(sourceEnvelope, messageType);
      var streamId = _resolveStreamId(message, messageType, sourceEnvelope);
      if (_persists(mode)) {
        _trackEventForSync(messageType, eventId.Value, streamId);
      }
    }

    // The Cascade policy decides (#1395): the route, the owned rule (an owned command never leaves its
    // service, #1403) and a command routed store-only (refused). The local dispatch sees the source
    // envelope wrapped so IsDefaultDispatch=true (default-stage fan-out only); with no source envelope
    // threaded, the ambient establishing context is captured so a detached-stage receptor still sees a
    // hop carrying identity. The wrapper also carries the cascaded message's own id as the anchor for
    // whatever nested receptors emit, so their emissions do not derive this handler's ids and get
    // deduplicated away; a cascaded command takes the next ordinal of this handling as its anchor.
    // Built only when the message runs here: building it takes an emission ordinal, as it always has.
    // With neither a source nor an ambient context, the handlers get no envelope and take the ambient
    // context, as a publish's do: a placeholder envelope carried no message, and message security
    // refused it as a null payload (#1410 follow-up)
    IMessageEnvelope? cascadeEnvelopeFor() =>
      (sourceEnvelope ?? _captureAmbientSourceEnvelope()) is { } cascadeSource
        ? new CascadeEnvelopeWrapper(cascadeSource) { EmissionAnchor = eventId ?? _mintEmissionId(sourceEnvelope, messageType) }
        : null;
    await _carryOutCascadeAsync(message, messageType, mode, cascadeEnvelopeFor, sourceEnvelope, eventId, cancellationToken).ConfigureAwait(false);

    sw.Stop();
    _dispatcherMetrics?.CascadeDuration.Record(sw.Elapsed.TotalMilliseconds);
    _dispatcherMetrics?.EventsCascaded.Add(1,
      new KeyValuePair<string, object?>("event_type", messageType.Name),
      new KeyValuePair<string, object?>("destination", mode.ToString()));
  }

  /// <summary>
  /// Resolves the StreamId for a cascaded event, including inheritance from source command
  /// and auto-generation based on [GenerateStreamId] attribute policy.
  /// </summary>
  private Guid _resolveStreamId(IMessage message, Type messageType, IMessageEnvelope? sourceEnvelope) {
    var streamId = _streamIdExtractor?.ExtractStreamId(message, messageType) ?? Guid.Empty;

    // Cascade StreamId propagation: inherit from source command when event's StreamId is empty
    streamId = _cascadeStreamIdFromSourceIfNeeded(message, messageType, streamId, sourceEnvelope);

    // Auto-generate StreamId based on [GenerateStreamId] attribute policy, for a [StreamId] property as well
    // as IHasStreamId: the cascade used to leave a plain property empty (#1410 follow-up)
    if (streamId == Guid.Empty) {
      streamId = _autoGenerateStreamIdIfNeeded(message, messageType, streamId);
    }

    return streamId;
  }

  /// <summary>
  /// Tracks a cascaded event in both scoped and singleton trackers for perspective sync.
  /// </summary>
  /// <summary>Whether a message dispatched with this mode is stored, and so reaches a perspective.</summary>
  private static bool _persists(Dispatch.DispatchModes mode) =>
    (mode & (Dispatch.DispatchModes.Outbox | Dispatch.DispatchModes.EventStore)) != 0;

  private void _trackEventForSync(Type messageType, Guid eventId, Guid streamId) {
    // The scoped tracker of the current request (the field, or the ambient one for a singleton dispatcher)
    // and the singleton tracker, as every other emission path tracks (#1410 follow-up)
    _trackInScopedTracker(streamId, messageType, eventId);
    _trackInSingletonTracker(streamId, messageType, eventId);
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
  /// <docs>fundamentals/dispatcher/message-cascade#auto-cascade-to-outbox</docs>
  /// <tests>tests/Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs:Generator_CascadeToOutbox_CallsPublishToOutboxWithMessageIdAsync</tests>
  /// <param name="recordLocalDispatch">
  /// True when the caller's local path runs this event's default-stage receptors itself, so the stored
  /// envelope records that for the receptor dedup store.
  /// </param>
  protected async Task PublishToOutboxAsync<TEvent>(TEvent eventData, Type eventType, MessageId messageId, IMessageEnvelope? sourceEnvelope = null, bool eventStoreOnly = false, DispatchOptions? options = null, bool recordLocalDispatch = false) {
    // The caller's schedule, declared priority and payload limit, when it dispatched with options.
    var scheduledFor = options?.ScheduledFor;
    var priority = options?.Priority ?? Whizbang.Core.Priority.WorkPriority.UNDECLARED;
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
    if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
      var eventTypeName = eventType.Name;
      CascadeLogger.LogDebug("[CASCADE] PublishToOutboxAsync: Called for {EventType}, MessageId={MessageId}", eventTypeName, messageId);
    }
#pragma warning restore CA1848

    // No-rebroadcast guard (plans/composite-events-turnkey.md, Phase D): a fan-out child carries
    // EventFlags.NoRebroadcast on its envelope. Any attempt to re-broadcast it (publish triggered while
    // processing it) is dropped here at the outbox-enqueue boundary — the enforced "children never
    // outbox" invariant, on top of hop-based echo suppression.
    if (NoRebroadcastGuard.ShouldSuppress(sourceEnvelope)) {
#pragma warning disable CA1848
      if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
        CascadeLogger.LogDebug("[CASCADE] Suppressed re-broadcast of NoRebroadcast message {EventType}", eventType.Name);
      }
#pragma warning restore CA1848
      return;
    }

    // Create scope to resolve scoped IWorkCoordinatorStrategy
    // Guard against ObjectDisposedException during application shutdown or hot reload
    IServiceScope scope;
    try {
      scope = _scopeFactory.CreateScope();
    } catch (ObjectDisposedException ex) {
      // Service provider is disposed - application is shutting down
      // Dropping events during shutdown is acceptable behavior
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
      if (CascadeLogger.IsEnabled(LogLevel.Warning)) {
        var eventTypeName = eventType.Name;
        CascadeLogger.LogWarning(ex,
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
        await _handleNoStrategyFallbackAsync(eventData, eventType, messageId, sourceEnvelope, eventStoreOnly, scope);
        return;
      }

      // Resolve destination topic using registry and routing strategy
      // When eventStoreOnly is true, use null destination to bypass transport
      string? destination = eventStoreOnly ? null : _resolveDestination(eventData, eventType);

      // Cascade StreamId propagation: inherit from source command when event's StreamId is empty
      _cascadeStreamIdToOutboxEvent(eventData, eventType, sourceEnvelope);

      // Create envelope with hop and serialize to outbox message
      var envelope = _createOutboxEnvelopeWithHop(eventData, eventType, messageId, sourceEnvelope, destination);
      _stampExplicitPriority(envelope, priority);   // an explicit number on the options is the caller's declaration
      envelope.PayloadLimitOverride = options?.MaxPayloadBytes;
      // A copy marked handled here (a cascade that ran the event's receptors, which reaches this through
      // the generated outbox cascade) records it as a publish does (#1410 follow-up)
      if (recordLocalDispatch || _outboxCopyMark.Value == OutboxCopyMark.HandledLocally) {
        await _recordLocalDispatchAsync(envelope, eventType, scope.ServiceProvider).ConfigureAwait(false);
      }

      // Serialize, queue, and flush
      // The caller's token, from its options or the cascade that started this write, reaches the queue and
      // the flush (#1410 follow-up)
      await _serializeQueueAndFlushAsync(envelope, eventData!, eventType, destination, strategy, scheduledFor,
        options?.CancellationToken ?? _ambientCopyWriteToken());
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
  /// Records, on the envelope about to be stored, that the publish's local path runs this event's
  /// default-stage receptors, so the receptor dedup store sees that firing at every later stage.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The dedup store keeps "a receptor that fired for a message does not fire again for it" by reading
  /// the records an envelope carries. The local path invokes its receptors directly rather than through
  /// the receptor invoker, so it wrote none, and a later stage reaching the same receptor for the same
  /// message fired it again wherever no same-service rule applied: a saga watchdog tick published
  /// without a schedule was handled by the publishing host and again by another host of the same saga.
  /// </para>
  /// <para>
  /// Written before the envelope is serialized, because the outbox write runs beside the local path,
  /// not after it. The receptors recorded are those at the local default stage, which are the ones the
  /// local path runs. With tracking off, or no dedup store, nothing is recorded, as the invoker records
  /// nothing then either.
  /// </para>
  /// </remarks>
  /// <docs>fundamentals/receptors/exactly-once-firing</docs>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherLocalDispatchRecordTests.cs</tests>
  /// <tests>tests/Whizbang.Sagas.Tests/SagaWatchdogTickDeliveryCountTests.cs:ImmediateTick_ReceivedByAnotherHostOfTheSameSaga_IsNotHandledAgainAsync</tests>
  private async ValueTask _recordLocalDispatchAsync(IMessageEnvelope envelope, Type eventType, IServiceProvider scopedProvider) {
    if (_receptorRegistry is null
        || _whizbangOptions.Guardrails.ReceptorInvocationTracking == ReceptorInvocationTracking.Off
        || scopedProvider.GetService<IReceptorDedupStore>() is not { } dedupStore) {
      return;
    }

    var now = DateTimeOffset.UtcNow;
    foreach (var receptor in _receptorRegistry.GetReceptorsFor(eventType, LifecycleStage.LocalImmediateInline)) {
      await dedupStore.RecordInvocationAsync(envelope, new ReceptorInvocationRecord {
        ReceptorId = receptor.ReceptorId,
        Stage = LifecycleStage.LocalImmediateInline,
        CompletedAt = now,
        Duration = TimeSpan.Zero,
        ServiceName = _instanceProvider.ServiceName,
      }, CancellationToken.None).ConfigureAwait(false);
    }
  }

  /// <summary>
  /// Handles the case when no IWorkCoordinatorStrategy is registered.
  /// Falls back to deferred channel or logs a warning.
  /// </summary>
  private async Task _handleNoStrategyFallbackAsync<TEvent>(TEvent eventData, Type eventType, MessageId messageId, IMessageEnvelope? sourceEnvelope, bool eventStoreOnly, IServiceScope scope,
      MessageHop? stampedBy = null) {
    // Try to get deferred channel for resilient event publishing
    var deferredChannel = scope.ServiceProvider.GetService<IDeferredOutboxChannel>();

    if (deferredChannel != null) {
      // Queue to deferred channel for next lifecycle loop pickup
      await _deferEventToChannelAsync(eventData, eventType, messageId, sourceEnvelope, eventStoreOnly, deferredChannel, stampedBy);
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
    if (logger?.IsEnabled(LogLevel.Warning) == true) {
      var eventTypeName = eventType.Name;
      logger.LogWarning(
        "IWorkCoordinatorStrategy not registered and IDeferredOutboxChannel not available - " +
        "event will not be published to outbox for cross-service delivery. " +
        "Register IWorkCoordinatorStrategy or IDeferredOutboxChannel to enable outbox pattern. EventType: {EventType}",
        eventTypeName);
    }
#pragma warning restore CA1848
  }

  /// <summary>
  /// Cascades StreamId from source envelope to event when the event's StreamId is empty.
  /// This enables the pattern where [GenerateStreamId] is on the command and events inherit it.
  /// </summary>
  private void _cascadeStreamIdToOutboxEvent<TEvent>(TEvent eventData, Type eventType, IMessageEnvelope? sourceEnvelope) =>
    _propagateStreamIdFromSource(eventData!, eventType, sourceEnvelope);

  /// <summary>
  /// Producer-side composite fan-out (step 1.1 of the composite lifecycle). When a service publishes a
  /// composite in its OWN domain, it expands the composite and <b>local-publishes each inner event</b>
  /// through the normal local-only flow (<see cref="DispatchModes.Local"/> = local receptor dispatch + event
  /// store, NO transport), so the publishing service materializes its own composite's children immediately —
  /// event store + receptors + perspectives, exactly as if each inner event were published individually,
  /// minus the per-event outbox row. The composite ALSO goes over transport (step 1.2) for other subscribing
  /// services, which fan out the same way on receive (step 2.1); the publishing service's own transported
  /// copy loops back and is echo-discarded, so there is no double fan-out. Inner events never rebroadcast
  /// (DispatchModes.Local never reaches the outbox). Sequential await preserves producer-yielded order.
  /// </summary>
  /// <docs>fundamentals/messaging/composite-events#publish-time-local-fan-out</docs>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherCompositePublishFanoutTests.cs:OwnedComposite_AtomicAtomicity_PropagatesChildFailureAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherCompositePublishFanoutTests.cs:OwnedComposite_FansOutAtPublish_ViaDispatchOptionsOverloadAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherCompositePublishFanoutTests.cs:NonOwnedComposite_DoesNotFanOutAtPublishAsync</tests>
  /// <remarks>
  /// Internal rather than private so the not-a-composite answer can be asserted. Both call sites
  /// test the message first, so the guard cannot fire through a publish — it is what keeps a
  /// future caller from reading inner events off something that has none.
  /// </remarks>
  // S3776: linear loop with one atomicity branch — already minimal.
#pragma warning disable S3776
  internal async Task FanOutCompositeLocallyAtPublishAsync(object composite, Type compositeType, MessageId messageId) {
    if (composite is not ICompositeEvent comp) {
      return;
    }

    // Producer-side fail-fast: a malformed composite surfaces synchronously in the publishing handler's
    // try/catch, not silently later.
    var innerCount = comp.InnerEvents.Count();
    if (innerCount > comp.MaxInnerEventsAllowed) {
      throw new InvalidOperationException(
        $"Composite '{compositeType.Name}' carries {innerCount} inner events, exceeding MaxInnerEventsAllowed ({comp.MaxInnerEventsAllowed}).");
    }

    // Source envelope representing the composite, so each inner event inherits composite lineage
    // (CausationId = the composite's MessageId, CausationType = the composite type).
    var sourceEnvelope = _createOutboxEnvelopeWithHop(comp, compositeType, messageId, sourceEnvelope: null, destination: null);

    // Atomicity governs child failure: Atomic propagates (the whole publish fails); Independent logs + continues.
    var atomic = comp.Atomicity == FanoutAtomicity.Atomic;
    foreach (var inner in comp.InnerEvents) {
      if (inner is null) {
        continue;
      }
      try {
        await CascadeMessageAsync(inner, sourceEnvelope, DispatchModes.Local).ConfigureAwait(false);
      } catch (Exception ex) when (!atomic) {
#pragma warning disable CA1848
        CascadeLogger.LogWarning(ex,
          "[CASCADE] Owned composite '{Composite}' inner event '{Inner}' failed under Independent atomicity — skipping.",
          compositeType.Name, inner.GetType().Name);
#pragma warning restore CA1848
      }
    }
  }
#pragma warning restore S3776

  // Hop-first identity/scope resolution now lives in CascadeContext (CascadeContext.ResolveHopFirstIdentity /
  // ResolveHopFirstScope) so every hop builder — here AND in other classes (e.g. SecurityContextEventStoreDecorator)
  // — shares the ONE hop-first precedence rule instead of each re-implementing it.

  private static readonly object _captureSentinelPayload = new();

  /// <summary>
  /// Snapshots the ambient establishing message context into a lightweight source envelope SYNCHRONOUSLY at a
  /// child-emit calling site (<c>PublishAsync</c>), so the emitted child inherits correlation + causation + scope
  /// from the hop even when the outbox builder later runs where AsyncLocal no longer flows (detached/worker).
  /// Causation is the establishing message's id (the parent). Returns <see langword="null"/> when there is no
  /// establishing context — a genuine root emit, which then resolves a fresh trace-aligned root as before.
  /// </summary>
  private MessageEnvelope<object>? _captureAmbientSourceEnvelope() {
    var initiating = ScopeContextAccessor.CurrentInitiatingContext;
    if (initiating is null || initiating.CorrelationId.Value == Guid.Empty) {
      return null;
    }
    // Scope honors the ShouldPropagate contract via HopScope.FromAmbient (identical to
    // CascadeContext.ResolveHopFirstScope's ambient branch — the ONE ambient-scope rule). Do NOT re-add an
    // initiating-scope fallback here: it would bypass PropagateToOutgoingMessages=false and leak a deliberately
    // non-propagating scope onto the child hop (the divergence the audit found; the fallback was accidental
    // symmetry copied from the identity capture, never a contract decision).
    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _instanceProvider.ToInfo(),
      Timestamp = DateTimeOffset.UtcNow,
      CorrelationId = initiating.CorrelationId,
      CausationId = initiating.MessageId,
      Scope = HopScope.FromAmbient(),
    };
    return new MessageEnvelope<object> {
      MessageId = initiating.MessageId,
      Payload = _captureSentinelPayload,
      Hops = [hop],
      DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local },
    };
  }

  /// <summary>
  /// Creates a MessageEnvelope with hop metadata for outbox publishing.
  /// Includes scope propagation from ambient context or source envelope.
  /// </summary>
  private MessageEnvelope<TEvent> _createOutboxEnvelopeWithHop<TEvent>(TEvent eventData, Type eventType, MessageId messageId, IMessageEnvelope? sourceEnvelope, string? destination) {
    // Extract aggregate ID and add to hop metadata (for streamId extraction)
    var hopMetadata = _createHopMetadata(eventData!, eventType);

    // Scope + identity are carried on the SOURCE HOP captured at the calling site — hop-FIRST — falling back to
    // ambient only when no source hop was threaded (see CascadeContext.ResolveHopFirstScope /
    // ResolveHopFirstIdentity — the single place the hop-first precedence lives, shared by every hop builder).
    // Hop-first, then ambient, then the marker recording why there is no scope at all. The
    // publish path does not share _createEnvelope, so without this the marker covered every
    // path EXCEPT the one control-plane traffic actually uses.
    var finalScope = Security.OutboxHopScope.Resolve(sourceEnvelope, eventType, _declaredUnscopedTypes);

    var (correlation, causation) = CascadeContext.ResolveHopFirstIdentity(sourceEnvelope);

    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _instanceProvider.ToInfo(),
      Topic = _hopTopic(destination),
      Timestamp = DateTimeOffset.UtcNow,
      Metadata = hopMetadata,
      Scope = finalScope,
      CorrelationId = correlation,
      CausationId = causation,
      TraceParent = HopStamping.AmbientTraceParent
    };

    // Populate SentAt-phase properties (SentAt, CorrelationId, identifiers) onto the event object so a PUBLISHED
    // event carries them exactly like a dispatched one — the SignalR notification hook and audit read these off
    // the object, not the hop. Parity with _createEnvelope; runs after the hop so it reads the resolved identity.
    var populatedPayload = (TEvent)AutoPopulatePopulatorRegistry.PopulateSent(eventData!, hop, messageId);

    var envelope = new MessageEnvelope<TEvent> {
      MessageId = messageId,
      Payload = populatedPayload,
      Hops = [],
      DispatchContext = _outboxCopyDispatchContext()
    };
    envelope.AddHop(hop);

#pragma warning disable CA1848 // Diagnostic logging - performance not critical
    if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
      CascadeLogger.LogDebug("[CASCADE] PublishToOutboxAsync: Queueing event {EventType} with destination '{Destination}'",
        eventType.Name, destination);
      CascadeLogger.LogDebug("[TRACE] PublishToOutboxAsync: TraceParent={TraceParent}, HasActivity={HasActivity}",
        hop.TraceParent ?? "(null)", System.Diagnostics.Activity.Current is not null);
    }
#pragma warning restore CA1848

    return envelope;
  }

  /// <summary>
  /// Serializes the envelope to an outbox message, queues it on the strategy, and flushes.
  /// Guards against ObjectDisposedException during shutdown.
  /// </summary>
  private async Task _serializeQueueAndFlushAsync<TEvent>(MessageEnvelope<TEvent> envelope, TEvent eventData, Type eventType, string? destination, IWorkCoordinatorStrategy strategy, DateTimeOffset? scheduledFor = null,
      CancellationToken cancellationToken = default) {
    // Serialize envelope to OutboxMessage
    var newOutboxMessage = _serializeToNewOutboxMessage(envelope, eventData!, eventType, destination, scheduledFor);
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
    if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
      var newMsgId = newOutboxMessage.MessageId;
      var newMsgType = newOutboxMessage.MessageType;
      CascadeLogger.LogDebug("[CASCADE] PublishToOutboxAsync: Created NewOutboxMessage, MessageId={MessageId}, Type={Type}",
        newMsgId, newMsgType);
    }
#pragma warning restore CA1848

    // An effect is queued after its cause (#1410 follow-up)
    await _afterCauseCopyWriteAsync().ConfigureAwait(false);

    // Composite pre-fanout hook (plans/composite-events-turnkey.md, Phase B): when a dispatch step has
    // opened an ambient outbox collector, divert this message into it instead of the work-coordinator so
    // it can be committed atomically with that step's other effects. AsyncLocal-gated — null off the path.
    if (DispatchOutboxCollector.Current is { } collector) {
      collector.Add(newOutboxMessage);
      return;
    }

    // Queue event for batched processing — async path routes through the per-stream
    // sliding-window batcher when StreamAffinityWorkCoordinatorStrategy is active.
    // Guard against ObjectDisposedException — the singleton IntervalWorkCoordinatorStrategy
    // may be disposed during host shutdown while in-flight receptors are still publishing.
    try {
      await strategy.QueueOutboxMessageAsync(newOutboxMessage, cancellationToken).ConfigureAwait(false);
    } catch (ObjectDisposedException ex) {
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
      if (CascadeLogger.IsEnabled(LogLevel.Warning)) {
        CascadeLogger.LogWarning(ex,
          "[CASCADE] PublishToOutboxAsync: Strategy disposed during shutdown - event {EventType} will not be published to outbox. MessageId={MessageId}",
          eventType.Name, envelope.MessageId);
      }
#pragma warning restore CA1848
      return;
    }
#pragma warning disable CA1848 // Diagnostic logging - performance not critical
    if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
      CascadeLogger.LogDebug("[CASCADE] PublishToOutboxAsync: Called QueueOutboxMessage");
    }
#pragma warning restore CA1848

    // Fire-and-forget signal — the strategy decides when to actually flush
    // (Interval=timer, Batch=debounce/size, Immediate/Scoped=now).
    await strategy.FlushAsync(WorkBatchOptions.SkipInboxClaiming, cancellationToken);

#pragma warning disable CA1848 // Diagnostic logging - performance not critical
    if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
      CascadeLogger.LogDebug("[CASCADE] PublishToOutboxAsync: Successfully queued event {EventType} via work coordinator",
        eventType.Name);
    }
#pragma warning restore CA1848
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
  /// <docs>fundamentals/dispatcher/message-cascade#auto-cascade-to-outbox</docs>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherCoverageSweepOutboxTests.cs:PublishToOutboxDynamic_EventWithIHasStreamId_InheritsStreamIdFromSourceAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherCoverageSweepOutboxTests.cs:PublishToOutboxDynamic_EventAlreadyHasStreamId_SkipsPropagationAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherNoRebroadcastGuardTests.cs:NoRebroadcastSource_IsSuppressedBeforeSerializationAsync</tests>
  protected async Task PublishToOutboxDynamicAsync(IMessage eventData, Type eventType, MessageId messageId, IMessageEnvelope? sourceEnvelope = null, bool eventStoreOnly = false, int priority = Whizbang.Core.Priority.WorkPriority.UNDECLARED) {
    // No-rebroadcast guard (Phase D) — see PublishToOutboxAsync.
    if (NoRebroadcastGuard.ShouldSuppress(sourceEnvelope)) {
#pragma warning disable CA1848
      if (CascadeLogger.IsEnabled(LogLevel.Debug)) {
        CascadeLogger.LogDebug("[CASCADE] Suppressed re-broadcast of NoRebroadcast message {EventType}", eventType.Name);
      }
#pragma warning restore CA1848
      return;
    }
    // Create scope to resolve scoped IWorkCoordinatorStrategy
    var scope = _scopeFactory.CreateScope();
    try {
      var strategy = scope.ServiceProvider.GetService<IWorkCoordinatorStrategy>();

      // If no strategy is registered, skip outbox routing (local-only event)
      if (strategy == null) {
        return;
      }

      // Resolve destination topic using registry and routing strategy
      string? destination = eventStoreOnly ? null : _resolveDestination(eventData, eventType);

      // Cascade StreamId propagation: inherit from source command when event's StreamId is empty
      _propagateStreamIdFromSource(eventData, eventType, sourceEnvelope);

      // Serialize and create envelope
      // Marked as the typed path marks a copy: handled here only when the decision ran it here, deferred
      // when its local handling waits for the schedule (#1410 follow-up)
      var jsonEnvelope = _serializeToJsonEnvelope(eventData, eventType, messageId, _outboxCopyDispatchContext());
      // Priority step 1: a cascade emission is declared like any other send; the ambient parent supplies inheritance,
      // and an explicit number on the options is kept.
      _stampExplicitPriority(jsonEnvelope, priority);
      jsonEnvelope.Priority = _declarePriority(jsonEnvelope, TypeNameFormatter.AssemblyQualifiedNameOrDisplay(eventType), scheduledFor: null);

      // Add hop with metadata and scope
      var hopMetadata = _createHopMetadata(eventData, eventType);
      _addOutboxHop(jsonEnvelope, destination, hopMetadata, sourceEnvelope, eventType);

      // Build and queue the outbox message
      // The stream its first hop names, or its message id: the rule the typed outbox path and every receiver use. The
      // hop was just written from the payload's [StreamId], so this is that stream when it names one (#1798).
      var streamId = StreamIdentity.Of(jsonEnvelope);
      _payloadLimits.Enforce(jsonEnvelope.Payload, eventType, messageId.Value, streamId);

      var newOutboxMessage = _buildOutboxMessage(jsonEnvelope, destination, eventType, eventData, streamId, _ephemeralModeResolver);
      // An effect is queued after its cause (#1410 follow-up)
      await _afterCauseCopyWriteAsync().ConfigureAwait(false);
      // Composite pre-fanout hook (Phase B): divert into the ambient collector when one is open.
      if (DispatchOutboxCollector.Current is { } collector) {
        collector.Add(newOutboxMessage);
        return;
      }
      // The cascade's token reaches the queue and the flush (#1410 follow-up)
      var cancellationToken = _ambientCopyWriteToken();
      await strategy.QueueOutboxMessageAsync(newOutboxMessage, cancellationToken).ConfigureAwait(false);
      await strategy.FlushAsync(WorkBatchOptions.SkipInboxClaiming, cancellationToken);
    } finally {
      if (scope is IAsyncDisposable asyncDisposable) {
        await asyncDisposable.DisposeAsync();
      } else {
        scope.Dispose();
      }
    }
  }

  /// <summary>
  /// Propagates StreamId from source envelope to event when the event has no StreamId set.
  /// </summary>
  private void _propagateStreamIdFromSource(object eventData, Type eventType, IMessageEnvelope? sourceEnvelope) {
    // Nothing is read without a source to inherit from: the payload's own stream is read only when it could be replaced.
    if (sourceEnvelope is null || _streamIdExtractor is null
        || (_streamIdExtractor.ExtractStreamId(eventData, eventType) is { } own && own != Guid.Empty)) {
      return;
    }
    _inheritSourceStreamId(eventData, eventType, sourceEnvelope);
  }

  /// <summary>
  /// Serializes event data to a JsonElement envelope using the runtime type's JSON type info.
  /// </summary>
  private static MessageEnvelope<JsonElement> _serializeToJsonEnvelope(IMessage eventData, Type eventType, MessageId messageId, MessageDispatchContext dispatchContext) {
    var typeNameForLookup = TypeNameFormatter.AssemblyQualifiedNameOrDisplay(eventType);
    var combinedOptions = Serialization.JsonContextRegistry.CreateCombinedOptions();
    var jsonTypeInfo = Serialization.JsonContextRegistry.GetTypeInfoByName(typeNameForLookup, combinedOptions)
      ?? throw new InvalidOperationException(
        $"No JSON type info found for {TypeNameFormatter.DisplayName(eventType)}. Ensure the type is registered in a JsonSerializerContext.");

    var payloadJson = JsonSerializer.SerializeToElement(eventData, jsonTypeInfo);

    return new MessageEnvelope<JsonElement> {
      MessageId = messageId,
      Payload = payloadJson,
      Hops = [],
      DispatchContext = dispatchContext
    };
  }

  /// <summary>
  /// Adds a hop to the envelope indicating the message is being stored to the outbox.
  /// </summary>
  private void _addOutboxHop(
    MessageEnvelope<JsonElement> jsonEnvelope,
    string? destination,
    Dictionary<string, JsonElement>? hopMetadata,
    IMessageEnvelope? sourceEnvelope,
      Type? payloadType) {
    // Hop-FIRST scope + identity: the source hop captured at the calling site is authoritative (survives
    // detached/worker boundaries); ambient is only the fallback. Shared helpers — the single place the rule lives.
    var (correlation, causation) = CascadeContext.ResolveHopFirstIdentity(sourceEnvelope);

    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _instanceProvider.ToInfo(),
      Topic = _hopTopic(destination),
      Timestamp = DateTimeOffset.UtcNow,
      Metadata = hopMetadata,
      Scope = Security.OutboxHopScope.Resolve(sourceEnvelope, payloadType, _declaredUnscopedTypes),
      CorrelationId = correlation,
      CausationId = causation,
      TraceParent = HopStamping.AmbientTraceParent
    };
    jsonEnvelope.AddHop(hop);
  }

  /// <summary>
  /// Declares the priority of an outgoing message through the registered producer hooks (priority step 1):
  /// the dispatch context, whether it is scheduled, the ambient parent's number and whatever the envelope
  /// already declares. Without a chain the envelope's own number is kept.
  /// </summary>
  /// <docs>fundamentals/messaging/message-priority#declaration</docs>
  /// <tests>tests/Whizbang.Core.Tests/Priority/DispatcherPriorityStampingTests.cs</tests>
  /// <summary>
  /// Priority step 1: an explicit number on the dispatch options is the caller's declaration. It is stamped before
  /// the producer hooks run, and the framework's default hook keeps an explicit declaration.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Priority/DispatcherPriorityStampingTests.cs:Send_WithAPriorityOnTheOptions_KeepsItOverTheContextRulesAsync</tests>
  private static void _stampExplicitPriority<TMessage>(MessageEnvelope<TMessage> envelope, int priority) {
    if (Whizbang.Core.Priority.WorkPriority.IsDeclared(priority)) {
      envelope.Priority = priority;
    }
  }

  private int _declarePriority(IMessageEnvelope envelope, string messageTypeName, DateTimeOffset? scheduledFor) {
    if (_priorityHooks is null) {
      return envelope.Priority;
    }
    return _priorityHooks.DeclarePriority(new Whizbang.Core.Priority.PriorityDeclarationContext(
      envelope, messageTypeName, envelope.DispatchContext, scheduledFor is not null,
      Whizbang.Core.Priority.PriorityContext.CurrentParent, envelope.Priority));
  }

  /// <summary>
  /// Builds an OutboxMessage from the serialized envelope and metadata.
  /// </summary>
  private static OutboxMessage _buildOutboxMessage(
    MessageEnvelope<JsonElement> jsonEnvelope,
    string? destination,
    Type eventType,
    IMessage eventData,
    Guid streamId,
    IEphemeralModeResolver? ephemeralModeResolver) {
    return new OutboxMessage {
      MessageId = jsonEnvelope.MessageId.Value,
      Destination = destination,
      Envelope = jsonEnvelope,
      Metadata = new EnvelopeMetadata {
        MessageId = jsonEnvelope.MessageId,
        Hops = jsonEnvelope.CopyHops(),
        EphemeralTtlSeconds = Whizbang.Core.Messaging.EphemeralTtlDeriver.Derive(eventData, ephemeralModeResolver)
      },
      EnvelopeType = Whizbang.Core.Messaging.EnvelopeTypeNameHelper.Format(TypeNameFormatter.AssemblyQualifiedName(eventType)),
      StreamId = streamId,
      IsEvent = eventData is IEvent,
      Flags = Whizbang.Core.Messaging.EventFlagsDeriver.FromPayload(eventData, ephemeralModeResolver),
      Scope = _extractScope(jsonEnvelope),
      MessageType = TypeNameFormatter.AssemblyQualifiedName(eventType),
      Priority = jsonEnvelope.Priority,
      CollectiveLinkType = CollectivePredecessorLink.TypeFor(eventData)
    };
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
  /// Where a message's outbox copy goes: an event to its topic, a composite to its events' topic, and anything
  /// else to its owner's command destination, as a send routes it. A cascade writes commands through the same
  /// outbox paths as events and used to send them to an event topic (#1410 follow-up). A composite is not an
  /// <see cref="IEvent"/>, and routing it as a command sent it only to its owner, which discards its own echo,
  /// so no subscriber received it.
  /// </summary>
  private string _resolveDestination(object? message, Type messageType) => message switch {
    IEvent => _resolveEventTopic(messageType),
    ICompositeEvent composite => _resolveCompositeTopic(composite, messageType),
    _ => _resolveCommandDestination(messageType),
  };

  /// <summary>
  /// The topic a composite goes to: the one its inner events share, where the services subscribed to them
  /// listen, whatever namespace the composite type itself is declared in. When its events would go to
  /// different topics, or it carries none, the composite's own type decides.
  /// </summary>
  private string _resolveCompositeTopic(ICompositeEvent composite, Type compositeType) {
    string? shared = null;
    foreach (var inner in composite.InnerEvents) {
      if (inner is null) {
        continue;
      }
      var topic = _resolveDestination(inner, inner.GetType());
      if (shared is null) {
        shared = topic;
      } else if (!string.Equals(shared, topic, StringComparison.Ordinal)) {
        return _resolveEventTopic(compositeType);
      }
    }
    return shared ?? _resolveEventTopic(compositeType);
  }

  /// <summary>
  /// Writes a cascaded message whose type no generated cascade arm names to the outbox by its runtime
  /// type, instead of dropping it (#1410 follow-up). The generated cascade switch falls through to this.
  /// </summary>
  /// <param name="message">The cascaded message.</param>
  /// <param name="messageType">Its runtime type.</param>
  /// <param name="sourceEnvelope">The cascade's source envelope.</param>
  /// <param name="eventId">The emission id the cascade minted, if any.</param>
  /// <param name="eventStoreOnly">True to store it without transport.</param>
  /// <docs>fundamentals/dispatcher/message-cascade#auto-cascade-to-outbox</docs>
  /// <tests>tests/Whizbang.Core.Component.Tests/Dispatcher/DispatchExecutorShapeComponentTests.cs</tests>
  protected Task CascadeByRuntimeTypeAsync(IMessage message, Type messageType, IMessageEnvelope? sourceEnvelope, Guid? eventId,
      bool eventStoreOnly) {
    var messageId = eventId.HasValue ? new MessageId(eventId.Value) : MessageId.New();
    return PublishToOutboxDynamicAsync(message, messageType, messageId, sourceEnvelope, eventStoreOnly);
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
    var resolvedTopic = _topicRoutingStrategy.ResolveTopic(eventType, baseTopic, context);
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
  [SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "Writes one send's outbox copy: the message, its context and caller location, plus the optional schedule, local receipt, deferred mark and the id the policy and the local run decided. A parameter object would be allocated on every send.")]
  private async Task<IDeliveryReceipt> _sendToOutboxViaScopeAsync<TMessage>(
    TMessage message,
    Type messageType,
    IMessageContext context,
    string callerMemberName,
    string callerFilePath,
    int callerLineNumber,
    DispatchOptions? options = null,
    IDeliveryReceipt? handledLocallyReceipt = null,
    DateTimeOffset? scheduledFor = null,
    bool localDeferred = false,
    bool eventStoreOnly = false,
    MessageId? copyId = null
  ) where TMessage : notnull {
    // Create scope to resolve scoped IWorkCoordinatorStrategy
    var scope = _scopeFactory.CreateScope();
    try {
      var strategy = scope.ServiceProvider.GetService<IWorkCoordinatorStrategy>();
      // A dispatch step that opened an outbox collector commits this copy with its other effects, as a
      // publish's copy does (#1410 follow-up)
      var collector = DispatchOutboxCollector.Current;

      // Handled locally already: with no outbox to broadcast through, the local run is the delivery
      if (strategy == null && collector is null && handledLocallyReceipt is not null) {
        return handledLocallyReceipt;
      }

      // If no strategy is registered, throw - no local receptor and no outbox
      if (strategy == null && collector is null) {
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

      // Where the copy goes: an event to its topic and a command to its owner, as every outbox path
      // resolves it; none at all when the event is only stored (#1410 follow-up)
      var destination = eventStoreOnly ? null : _resolveDestination(message, messageType);

      // Create envelope with hop for observability, typed when the caller's type is the message's
      // Under the local run's id when this send also runs here, so both carry one id (#1464)
      var envelope = _createSendEnvelope(message, context, new MessageDispatchContext { Mode = _outboxModeFor(handledLocallyReceipt), Source = MessageSource.Local, LocalDeferred = localDeferred }, callerMemberName, callerFilePath, callerLineNumber, copyId);
      _stampExplicitPriority(envelope, options?.Priority ?? Whizbang.Core.Priority.WorkPriority.UNDECLARED);
      envelope.PayloadLimitOverride = options?.MaxPayloadBytes;

      // Start dispatch activity to serve as parent for handler traces (on receiving end)
      // The activity context will be propagated through the outbox message
      var parentActivity = Activity.Current;
      using var dispatchActivity = WhizbangActivitySource.Execution.StartActivity($"Dispatch {messageType.Name} (Outbox)", ActivityKind.Internal);
      if (dispatchActivity != null) {
        dispatchActivity.SetTag(TAG_MESSAGE_TYPE, TypeNameFormatter.DisplayName(messageType));
        dispatchActivity.SetTag(TAG_MESSAGE_ID, envelope.MessageId.ToString());
        dispatchActivity.SetTag(TAG_CORRELATION_ID, context.CorrelationId.ToString());
        dispatchActivity.SetTag("whizbang.dispatch.destination", destination);
        dispatchActivity.SetTag(TAG_DEBUG_PARENT_ID, parentActivity?.Id ?? "none");
        dispatchActivity.SetTag(TAG_DEBUG_PARENT_SOURCE, parentActivity?.Source.Name ?? "none");
      }

      // Serialize envelope to OutboxMessage
      var newOutboxMessage = _serializeToNewOutboxMessage(envelope, message, messageType, destination, scheduledFor);

      // An effect is queued after its cause (#1410 follow-up)
      await _afterCauseCopyWriteAsync().ConfigureAwait(false);
      if (collector is not null) {
        collector.Add(newOutboxMessage);
      } else {
        // Queue message for batched processing; the async path routes through the per-stream batcher. The
        // caller's cancellation, when it dispatched with options, reaches the queue and the flush.
        var cancellationToken = options?.CancellationToken ?? CancellationToken.None;
        await strategy!.QueueOutboxMessageAsync(newOutboxMessage, cancellationToken).ConfigureAwait(false);

        // Flush strategy to execute the batch (strategy determines when to actually flush)
        await strategy.FlushAsync(WorkBatchOptions.SkipInboxClaiming, cancellationToken);
      }

      // Extract stream ID from [StreamId] attribute for delivery receipt
      var streamId = _streamIdExtractor?.ExtractStreamId(message, messageType);

      // Handled here as well: the local run is the delivery the caller sees. Otherwise the message is queued.
      return handledLocallyReceipt ?? DeliveryReceipt.Accepted(
        envelope.MessageId,
        destination ?? messageType.Name,
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
    List<BatchCopy> messages,
    List<DispatchDecision> copyDecisions,
    DispatchOptions options,
    IReadOnlyList<OutboxMessage>? heldEffects = null
  ) {
    var receipts = new List<IDeliveryReceipt>();

    // Create ONE scope for all messages
    var scope = _scopeFactory.CreateScope();
    try {
      // A dispatch step that opened an outbox collector commits these copies with its other effects
      var collector = DispatchOutboxCollector.Current;
      var strategy = collector is not null ? null : scope.ServiceProvider.GetService<IWorkCoordinatorStrategy>();
      if (collector is null && strategy is null) {
        return await _writeBatchWithNoOutboxAsync(messages, copyDecisions, heldEffects, scope).ConfigureAwait(false);
      }

      // Queue ALL messages to the strategy
      for (var i = 0; i < messages.Count; i++) {
        var (copy, receipt) = _buildBatchCopy(messages[i], copyDecisions[i], options);
        await _addOrQueueCopyAsync(collector, strategy, copy, options.CancellationToken).ConfigureAwait(false);
        receipts.Add(receipt);
      }

      // What the batch's handlers emitted, after the batch's own copies (#1410 follow-up)
      foreach (var effect in heldEffects ?? []) {
        await _addOrQueueCopyAsync(collector, strategy, effect, options.CancellationToken).ConfigureAwait(false);
      }

      // Flush ONCE for all messages (a collector's step commits them instead)
      if (strategy is not null) {
        await strategy.FlushAsync(WorkBatchOptions.SkipInboxClaiming, options.CancellationToken);
      }

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
  /// A batch's copies with no outbox: events are deferred or dropped with a log, as a single publish
  /// handles them; a batch holding a command cannot deliver it, nor can held writes, which are built
  /// outbox messages with no other way out (#1410 follow-up).
  /// </summary>
  private async Task<List<IDeliveryReceipt>> _writeBatchWithNoOutboxAsync(
      List<BatchCopy> messages,
      List<DispatchDecision> copyDecisions, IReadOnlyList<OutboxMessage>? heldEffects, IServiceScope scope) {
    if (!messages.TrueForAll(static m => m.Message is IEvent) || heldEffects is not null) {
      throw new InvalidOperationException("No IWorkCoordinatorStrategy registered. Cannot route messages to outbox.");
    }
    var receipts = new List<IDeliveryReceipt>(messages.Count);
    for (var i = 0; i < messages.Count; i++) {
      var (eventData, eventType, eventContext, envelope) = messages[i];
      var messageId = envelope.MessageId;
      await _handleNoStrategyFallbackAsync(eventData, eventType, messageId, sourceEnvelope: null, eventStoreOnly: !copyDecisions[i].WriteOutbox, scope,
        stampedBy: envelope.Hops[^1]).ConfigureAwait(false);
      receipts.Add(DeliveryReceipt.Accepted(messageId, eventType.Name, eventContext.CorrelationId, eventContext.CausationId,
        _streamIdExtractor?.ExtractStreamId(eventData, eventType)));
    }
    return receipts;
  }

  /// <summary>
  /// One batch message's copy, from the envelope recorded before its local run (see
  /// <see cref="_recordBatchCopy"/>). It carries the caller's priority and payload limit and the schedule
  /// of a deferred run, and has no destination when the event is only stored.
  /// </summary>
  private (OutboxMessage Copy, IDeliveryReceipt Receipt) _buildBatchCopy(BatchCopy item, DispatchDecision decision, DispatchOptions options) {
    var (message, messageType, context, envelope) = item;
    var destination = decision.WriteOutbox ? _resolveDestination(message, messageType) : null;
    _stampExplicitPriority(envelope, options.Priority);
    envelope.PayloadLimitOverride = options.MaxPayloadBytes;
    var copy = _serializeToNewOutboxMessage(envelope, message, messageType, destination, decision.DeferUntil);
    var receipt = DeliveryReceipt.Accepted(envelope.MessageId, destination ?? messageType.Name, context.CorrelationId, context.CausationId,
      _streamIdExtractor?.ExtractStreamId(message, messageType));
    return (copy, receipt);
  }

  /// <summary>Hands a built copy to the open unit of work, or queues it on the strategy.</summary>
  private static async ValueTask _addOrQueueCopyAsync(DispatchOutboxCollector? collector, IWorkCoordinatorStrategy? strategy,
      OutboxMessage copy, CancellationToken cancellationToken) {
    if (collector is not null) {
      collector.Add(copy);
    } else {
      await strategy!.QueueOutboxMessageAsync(copy, cancellationToken).ConfigureAwait(false);
    }
  }

  /// <summary>
  /// Runs one batch message locally through the send path: typed, so its envelope keeps the message's
  /// type, when the message is the batch's declared type and carries no route; otherwise by its own type
  /// and under its route.
  /// </summary>
  private Task<IDeliveryReceipt> _sendBatchMessageAsync<TMessage>(object message, DispatchModes? route, BatchCopy? copy, DispatchPolicy policy,
      string callerMemberName) where TMessage : notnull {
    // Under its recorded copy's context, so the message carries the correlation its copy's envelope does (#1455)
    var context = copy?.Context ?? MessageContext.Create(_cascadeContextFactory.NewRoot());
    // ...and under its id, so the handler's envelope and the stored copy are one message (#1464)
    var copyId = copy?.Envelope.MessageId;
    return route is null && message is TMessage typed && !_sendsAsAnotherType(typed)
      ? _sendCoreAsync(typed, typeof(TMessage), context, options: null, route: null, policy, ownsOutboxWrite: false, callerMemberName, "", 0, copyId)
      : _sendCoreAsync(message, message.GetType(), context, options: null, route, policy, ownsOutboxWrite: false, callerMemberName, "", 0, copyId);
  }

  /// <summary>
  /// Decides every message of a batch before any of them runs, so a refused message fails the whole
  /// batch with nothing handled and nothing queued.
  /// </summary>
  private (Type[] Types, DispatchDecision[] Decisions) _decideBatch(
      List<object> messages, DispatchModes?[] routes, DispatchPolicy callPolicy, DispatchOptions options) {
    var policy = _policyEngine.Resolve(callPolicy, options.Policy);
    var fanOut = policy.Expects(DispatchExpectations.FanOutAllReceptors);
    var types = new Type[messages.Count];
    var decisions = new DispatchDecision[messages.Count];
    for (var i = 0; i < messages.Count; i++) {
      var message = messages[i];
      var messageType = message.GetType();
      var hasHandler = fanOut
        ? _hasLocalPublishReceptors(messageType)
        : _lookupSendInvoker(message, messageType) != null;
      types[i] = messageType;
      decisions[i] = _decide(policy, message, messageType, hasHandler, options, routes[i], unmarkedKind: fanOut ? MessageTraits.Event : MessageTraits.Command);
      _throwIfRejected(in decisions[i], messageType);
    }
    return (types, decisions);
  }

  /// <summary>
  /// Copies the policy dropped because there is no outbox to send them through: says so, once per batch.
  /// </summary>
  private void _warnCopiesSkippedForNoOutbox(DispatchDecision[] decisions) {
    var skippedForNoOutbox = 0;
    foreach (var decision in decisions) {
      if (decision.Reason == DispatchReason.NoOutbox && decision.Local == LocalRun.Now && !decision.WriteOutbox) {
        skippedForNoOutbox++;
      }
    }
    if (skippedForNoOutbox > 0 && CascadeLogger.IsEnabled(LogLevel.Warning)) {
      Log.OutboxDeliverySkipped(CascadeLogger, skippedForNoOutbox);
    }
  }

  /// <summary>
  /// The one batch path behind every batch call. Every message is decided first, so a refused message
  /// fails the whole batch before anything runs. Then, in order, a message runs locally when the
  /// policy says so and its outbox copy is queued when the policy says so, marked when handled here.
  /// A local failure still queues that message's copy; the batch then stops or continues per
  /// <see cref="DispatchOptions.BatchFailure"/>, and the copies are flushed before the failure surfaces.
  /// </summary>
  private async Task<IEnumerable<IDeliveryReceipt>> _dispatchManyAsync(
      List<object> messages,
      DispatchPolicy callPolicy,
      DispatchOptions options,
      Func<Dispatcher, object, DispatchModes?, BatchCopy?, CancellationToken, Task<IDeliveryReceipt>> runLocally) {
    // Dispatch writes, so every entry refuses while the schema is not ready (#1410 follow-up)
    _throwIfSchemaNotReady();
    // The caller's token and timeout cover the whole batch, as they cover a single dispatch
    options = _withTimeout(options, out var timeoutScope);
    using var timeoutDisposal = timeoutScope;
    var cancellationToken = options.CancellationToken;
    cancellationToken.ThrowIfCancellationRequested();
    // A Routed<T> in a batch is honored as on a single call (#1395): each message is decided, run and
    // written under its own route
    var routes = _unwrapBatchRoutes(messages);
    var (types, decisions) = _decideBatch(messages, routes, callPolicy, options);
    foreach (var type in types) {
      _throwIfLocalDispatchTypeNeedsContext(type);
    }

    var batch = new BatchRun(messages, routes, types, decisions);
    var stopAtFirstFailure = options.BatchFailure == BatchFailureMode.Stop;
    // The batch's copies are written in one flush after its local runs, so what the handlers emit is held
    // back and written after them, in the same flush: a cause reaches the outbox before its effects
    // (#1410 follow-up). Only where there is an outbox to write to; otherwise each effect takes its own
    // no-outbox path as before.
    var heldEffects = _holdsEffectsBehindCopies(decisions) ? DispatchOutboxCollector.BeginCollecting() : (DispatchOutboxCollector.CollectingScope?)null;
    try {
      await _runBatchAsync(batch, runLocally, stopAtFirstFailure, cancellationToken).ConfigureAwait(false);
    } finally {
      heldEffects?.Dispose();
    }

    _warnCopiesSkippedForNoOutbox(decisions);

    try {
      // None held reads as null, so every check below asks one question
      var held = heldEffects?.Collector.Collected is { Count: > 0 } collected ? collected : null;
      await _flushOutboxBatchAndCollectReceiptsAsync(batch.OutboxMessages, batch.CopyDecisions, batch.Receipts, options, held);
    } catch (Exception outboxFailure) when (batch.Failures.Count > 0) {
      batch.Failures.Add(outboxFailure);
    }

    if (batch.Failures.Count == 1 && stopAtFirstFailure) {
      _rethrowIfFailed(batch.Failures[0]);
    }
    if (batch.Failures.Count > 0) {
      throw new AggregateException(batch.Failures);
    }
    return batch.Receipts;
  }

  /// <summary>A batch call's messages with their routes, types and decisions, and what it has run, written and failed so far.</summary>
  private sealed class BatchRun(List<object> messages, DispatchModes?[] routes, Type[] types, DispatchDecision[] decisions) {
    public List<object> Messages { get; } = messages;
    public DispatchModes?[] Routes { get; } = routes;
    public Type[] Types { get; } = types;
    public DispatchDecision[] Decisions { get; } = decisions;
    public List<IDeliveryReceipt> Receipts { get; } = [];
    public List<BatchCopy> OutboxMessages { get; } = [];
    public List<DispatchDecision> CopyDecisions { get; } = [];
    public List<Exception> Failures { get; } = [];
  }

  /// <summary>
  /// A batch message's copy as recorded before its local run: the message, its type and context, and the
  /// envelope that stamped it, whose id is the copy's.
  /// </summary>
  private readonly record struct BatchCopy(object Message, Type MessageType, IMessageContext Context, MessageEnvelope<object> Envelope);

  /// <summary>
  /// A published batch's local run for one event: every local receptor, handed the id its copy is stored
  /// under when the batch writes one (#1464). Shared by both PublishManyAsync overloads.
  /// </summary>
  private static readonly Func<Dispatcher, object, DispatchModes?, BatchCopy?, CancellationToken, Task<IDeliveryReceipt>> _publishManyLocally =
    static (self, eventData, _, copy, cancellationToken) => self._publishLocallyAsync(eventData, eventData.GetType(), copy?.Envelope.MessageId, cancellationToken);

  /// <summary>
  /// Records one batch message's copy, marked as a single dispatch marks it: a copy this service already
  /// handled says so, so the sender's PreOutbox stage does not run that handler again; a copy whose local
  /// handling waits for its schedule says that (#1397, #1401). Its envelope is built now, before the local
  /// run: that stamps the message once, so the handlers see what the stored copy carries (#1455), and
  /// mints the copy's id first, so the event store orders it before its effects (#1410 follow-up).
  /// </summary>
  private BatchCopy _recordBatchCopy(object message, Type messageType, in DispatchDecision decision,
      [CallerMemberName] string callerMemberName = "",
      [CallerFilePath] string callerFilePath = "",
      [CallerLineNumber] int callerLineNumber = 0) {
    var context = MessageContext.Create(_cascadeContextFactory.NewRoot());
    var dispatchContext = new MessageDispatchContext {
      Mode = decision.MarkHandledLocally ? DispatchModes.Both : DispatchModes.Outbox,
      Source = MessageSource.Local,
      LocalDeferred = decision.Local == LocalRun.AtScheduledTime
    };
    return new BatchCopy(message, messageType, context,
      _createEnvelope(message, context, dispatchContext, callerMemberName, callerFilePath, callerLineNumber));
  }

  /// <summary>Unwraps each <c>Routed&lt;T&gt;</c> in a batch in place and returns the routes, null where there is none.</summary>
  private DispatchModes?[] _unwrapBatchRoutes(List<object> messages) {
    var routes = new DispatchModes?[messages.Count];
    for (var i = 0; i < messages.Count; i++) {
      if (messages[i] is IRouted) {
        (messages[i], routes[i]) = _unwrapRouted(messages[i]);
      }
    }
    return routes;
  }

  /// <summary>
  /// Runs a batch's messages in order: each copy is recorded (its id minted before the local run, since
  /// the event store numbers a batch's events on a stream in id order, so the cause takes its place
  /// before anything its handlers emit), then the message runs here when decided. A failure is recorded;
  /// under <see cref="BatchFailureMode.Stop"/> the messages after it are neither run nor written.
  /// </summary>
  private async Task _runBatchAsync(BatchRun batch, Func<Dispatcher, object, DispatchModes?, BatchCopy?, CancellationToken, Task<IDeliveryReceipt>> runLocally,
      bool stopAtFirstFailure, CancellationToken cancellationToken) {
    for (var i = 0; i < batch.Messages.Count; i++) {
      cancellationToken.ThrowIfCancellationRequested();
      var decision = batch.Decisions[i];
      BatchCopy? copy = null;
      if (_writesCopy(in decision)) {
        batch.CopyDecisions.Add(decision);
        copy = _recordBatchCopy(batch.Messages[i], batch.Types[i], in decision);
        batch.OutboxMessages.Add(copy.Value);
      }
      if (decision.Local != LocalRun.Now) {
        continue;
      }
      try {
        batch.Receipts.Add(await runLocally(this, batch.Messages[i], batch.Routes[i], copy, cancellationToken));
      } catch (Exception ex) {
        batch.Failures.Add(ex);
        if (stopAtFirstFailure) {
          return;
        }
      }
    }
  }

  /// <summary>
  /// Whether a batch holds its handlers' writes back until its own copies are written: when some message
  /// runs here, some message writes a copy, and there is an outbox (or an open collector) to write to.
  /// </summary>
  private bool _holdsEffectsBehindCopies(DispatchDecision[] decisions) =>
    (_outboxRegistered || DispatchOutboxCollector.Current is not null)
      && Array.Exists(decisions, static d => d.Local == LocalRun.Now)
      && Array.Exists(decisions, static d => d.WriteOutbox || d.WriteEventStoreOnly);

  /// <summary>
  /// Runs every local receptor for a published event, as <c>PublishAsync</c> does: generated ones in
  /// this assembly and others, and runtime registrations at a local stage.
  /// </summary>
  private async Task<IDeliveryReceipt> _publishLocallyAsync(object eventData, Type eventType, MessageId? copyId, CancellationToken cancellationToken) {
    _autoGenerateStreamIdIfNeeded(eventData, eventType);
    var publisher = GetReceptorPublisher(eventData, eventType);
    var registered = _registerLocalRunEnvelope(eventData, sourceEnvelope: null, stampMessage: copyId is null, copyId);
    try {
      await _expandIfOwnedCompositeAsync(eventData, eventType, copyId).ConfigureAwait(false);
      await publisher(eventData);
      await _publishToForeignLookupsAsync(eventData, eventType, null, cancellationToken).ConfigureAwait(false);
      await _publishToRuntimeLocalReceptorsAsync(eventData, eventType, cancellationToken).ConfigureAwait(false);
    } finally {
      if (registered is not null) {
        _envelopeRegistry!.Unregister(eventData);
      }
    }
    await ProcessTagsIfEnabledAsync(eventData, eventType, cancellationToken);
    await _invokeLocalRunPostLifecycleAsync(eventData, eventType, registered, sourceEnvelope: null, copyId, cancellationToken);
    await _startLocalRunDetachedHooksAsync(eventData, eventType, registered, sourceEnvelope: null, copyId);
    return DeliveryReceipt.Delivered(MessageId.New(), eventType.Name, correlationId: null, causationId: null,
      streamId: _streamIdExtractor?.ExtractStreamId(eventData, eventType));
  }

  private async Task _flushOutboxBatchAndCollectReceiptsAsync(
    List<BatchCopy> outboxMessages,
    List<DispatchDecision> copyDecisions,
    List<IDeliveryReceipt> receipts,
    DispatchOptions options,
    IReadOnlyList<OutboxMessage>? heldEffects
  ) {
    // A batch stopped before its first copy still writes what its handlers emitted
    if (outboxMessages.Count == 0 && heldEffects is null) {
      return;
    }

    try {
      var outboxReceipts = await _sendManyToOutboxAsync(outboxMessages, copyDecisions, options, heldEffects);
      // Only add Accepted receipts for messages not handled here now
      // (locally-handled messages already have Delivered receipts)
      for (var i = 0; i < outboxReceipts.Count; i++) {
        if (copyDecisions[i].Local != LocalRun.Now) {
          receipts.Add(outboxReceipts[i]);
        }
      }
    } catch (InvalidOperationException) when (copyDecisions.TrueForAll(static d => d.Local == LocalRun.Now) && heldEffects is null) {
      // Held effects are messages the handlers wrote, with no other way out: they fail the batch instead
      // of being dropped behind this warning (#1410 follow-up)
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
  [Obsolete("Use the W4 CT-only overload LocalInvokeAndSyncAsync<TMessage>(message, SyncMode, CancellationToken). Will be removed in the next major.")]
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
      var syncResult = await _waitForAllPerspectivesAsync(_syncTimeout(timeout), onWaiting, onDecisionMade, cancellationToken);

      if (syncResult.Outcome == SyncOutcome.TimedOut) {
        throw new TimeoutException(
            $"Perspectives did not complete processing within {_syncTimeout(timeout)}. " +
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
  [Obsolete("Use the W4 CT-only overload LocalInvokeAndSyncAsync<TMessage>(message, SyncMode, CancellationToken). Will be removed in the next major.")]
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
      return await _waitForAllPerspectivesAsync(_syncTimeout(timeout), onWaiting, onDecisionMade, cancellationToken);
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
  [Obsolete("Use the W4 CT-only overload LocalInvokeAndSyncAsync<TMessage>(message, SyncMode, CancellationToken). Will be removed in the next major.")]
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
          message, _syncTimeout(timeout), onWaiting, onDecisionMade, cancellationToken);

      if (syncResult.Outcome == SyncOutcome.TimedOut) {
        throw new TimeoutException(
            $"Perspective {typeof(TPerspective).Name} did not complete processing within {_syncTimeout(timeout)}. " +
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
  public async ValueTask LocalInvokeAndSyncAsync<TMessage>(
      TMessage message,
      SyncMode mode,
      CancellationToken cancellationToken = default)
      where TMessage : notnull {
    var sw = Stopwatch.StartNew();
    try {
      await LocalInvokeAsync(message);

      // StreamOnly: handler completed + events emitted; no perspective wait.
      // AllProjections: delegate to the existing per-perspective wait. The wait
      // uses the caller's CT as the only bound — Timeout.InfiniteTimeSpan signals
      // "no timeout-based exit" so the legacy timeout path is bypassed.
      if (mode == SyncMode.StreamOnly) {
        return;
      }

      _ = await _waitForAllPerspectivesAsync(
        Timeout.InfiniteTimeSpan,
        onWaiting: null,
        onDecisionMade: null,
        cancellationToken);
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
          message, _syncTimeout(timeout), onWaiting, onDecisionMade, cancellationToken);
    } finally {
      sw.Stop();
      _dispatcherMetrics?.LocalInvokeAndSyncDuration.Record(sw.Elapsed.TotalMilliseconds);
    }
  }

  // ========================================
  // W4 — NEW SHAPE: SyncMode + CT-only, no TimeSpan
  // ========================================

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
    var streamId = _streamIdExtractor?.ExtractStreamId(message, message.GetType());
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
  /// <tests>tests/Whizbang.Core.Component.Tests/Dispatcher/DispatcherTests.cs:SendMany_WithMultipleCommands_ShouldReturnAllReceiptsAsync</tests>
  /// <tests>tests/Whizbang.Core.Component.Tests/Dispatcher/DispatcherTests.cs:SendManyAsync_Generic_CreatesTypedEnvelopesAsync</tests>
  /// <tests>tests/Whizbang.Core.Component.Tests/Dispatcher/DispatchTangentialParityComponentTests.cs:EveryEntryThatRunsHereAndWritesACopy_HandsTheHandlersTheMessageAsStoredAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DispatchExecutionIntegrationTests.cs:MessageWithALocalHandler_TheHandlerSeesTheSentTimeAndCorrelationTheStoredRowCarriesAsync</tests>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull =>
    SendManyAsync(messages, new DispatchOptions());

  /// <inheritdoc />
  public async Task<IEnumerable<IDeliveryReceipt>> SendManyAsync<TMessage>(IEnumerable<TMessage> messages, DispatchOptions options) where TMessage : notnull {
    ArgumentNullException.ThrowIfNull(messages);
    ArgumentNullException.ThrowIfNull(options);
    var sw = Stopwatch.StartNew();
    try {
      var messageList = messages.Cast<object>().ToList();
      _dispatcherMetrics?.SendManyBatchSize.Record(messageList.Count);
      // Each message: the SendMany policy, on the message's own type, run locally through the typed send
      return await _dispatchManyAsync(messageList, DispatchPolicies.SendMany, options,
        static (self, message, route, copy, _) => self._sendBatchMessageAsync<TMessage>(message, route, copy, DispatchPolicies.SendMany, nameof(SendManyAsync)));
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
  public Task<IEnumerable<IDeliveryReceipt>> SendManyAsync(IEnumerable<object> messages) =>
    SendManyAsync(messages, new DispatchOptions());

  /// <inheritdoc />
  public async Task<IEnumerable<IDeliveryReceipt>> SendManyAsync(IEnumerable<object> messages, DispatchOptions options) {
    ArgumentNullException.ThrowIfNull(messages);
    ArgumentNullException.ThrowIfNull(options);
    var sw = Stopwatch.StartNew();
    try {
      var messageList = messages.ToList();
      _dispatcherMetrics?.SendManyBatchSize.Record(messageList.Count);
      return await _dispatchManyAsync(messageList, DispatchPolicies.SendMany, options,
        static (self, message, route, copy, _) => self._sendBatchMessageAsync<object>(message, route, copy, DispatchPolicies.SendMany, nameof(SendManyAsync)));
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
  /// <tests>tests/Whizbang.Core.Component.Tests/Dispatcher/DispatchTangentialParityComponentTests.cs:EveryEntryThatRunsHereAndWritesACopy_HandsTheHandlersTheMessageAsStoredAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/DispatchExecutionIntegrationTests.cs:MessageWithALocalHandler_TheHandlerSeesTheSentTimeAndCorrelationTheStoredRowCarriesAsync</tests>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync<TEvent>(IEnumerable<TEvent> events) where TEvent : notnull =>
    PublishManyAsync(events, new DispatchOptions());

  /// <inheritdoc />
  public async Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync<TEvent>(IEnumerable<TEvent> events, DispatchOptions options) where TEvent : notnull {
    ArgumentNullException.ThrowIfNull(events);
    ArgumentNullException.ThrowIfNull(options);
    var sw = Stopwatch.StartNew();
    try {
      var eventList = events.Cast<object>().ToList();
      _dispatcherMetrics?.SendManyBatchSize.Record(eventList.Count);
      // Each event: the PublishMany policy, every local receptor (#1402), like PublishAsync
      return await _dispatchManyAsync(eventList, DispatchPolicies.PublishMany, options,
        _publishManyLocally);
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
  public Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync(IEnumerable<object> events) =>
    PublishManyAsync(events, new DispatchOptions());

  /// <inheritdoc />
  public async Task<IEnumerable<IDeliveryReceipt>> PublishManyAsync(IEnumerable<object> events, DispatchOptions options) {
    ArgumentNullException.ThrowIfNull(events);
    ArgumentNullException.ThrowIfNull(options);
    var sw = Stopwatch.StartNew();
    try {
      var eventList = events.ToList();
      _dispatcherMetrics?.SendManyBatchSize.Record(eventList.Count);
      return await _dispatchManyAsync(eventList, DispatchPolicies.PublishMany, options,
        _publishManyLocally);
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
  /// <docs>fundamentals/dispatcher/dispatch-patterns#localsendmanyasync</docs>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync<TMessage>(IEnumerable<TMessage> messages) where TMessage : notnull =>
    LocalSendManyAsync(messages, new DispatchOptions());

  /// <inheritdoc />
  public async ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync<TMessage>(IEnumerable<TMessage> messages, DispatchOptions options) where TMessage : notnull {
    ArgumentNullException.ThrowIfNull(messages);
    ArgumentNullException.ThrowIfNull(options);
    return await _dispatchManyAsync([.. messages.Cast<object>()], DispatchPolicies.LocalSendMany, options,
      static (self, message, route, copy, _) => self._sendBatchMessageAsync<TMessage>(message, route, copy, DispatchPolicies.LocalSendMany, nameof(LocalSendManyAsync)));
  }


  /// <summary>
  /// Sends multiple messages to local receptors ONLY (no outbox delivery).
  /// For AOT compatibility, use the generic overload LocalSendManyAsync&lt;TMessage&gt;.
  /// Throws <see cref="ReceptorNotFoundException"/> if any message has no local receptor.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherOutboxTests.cs:LocalSendManyAsync_NonGeneric_WithLocalReceptor_DoesNotPublishToOutboxAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Dispatcher/DispatcherOutboxTests.cs:LocalSendManyAsync_NonGeneric_ProcessesAllMessagesLocallyAsync</tests>
  /// <docs>fundamentals/dispatcher/dispatch-patterns#localsendmanyasync</docs>
#if !WHIZBANG_ENABLE_FRAMEWORK_DEBUGGING
  [DebuggerStepThrough]
  [StackTraceHidden]
#endif
  public ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync(IEnumerable<object> messages) =>
    LocalSendManyAsync(messages, new DispatchOptions());

  /// <inheritdoc />
  public async ValueTask<IEnumerable<IDeliveryReceipt>> LocalSendManyAsync(IEnumerable<object> messages, DispatchOptions options) {
    ArgumentNullException.ThrowIfNull(messages);
    ArgumentNullException.ThrowIfNull(options);
    return await _dispatchManyAsync([.. messages], DispatchPolicies.LocalSendMany, options,
      static (self, message, route, copy, _) => self._sendBatchMessageAsync<object>(message, route, copy, DispatchPolicies.LocalSendMany, nameof(LocalSendManyAsync)));
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
  /// <tests>tests/Whizbang.Core.Tests/Messaging/DeferredDispatchTests.cs</tests>
  private async Task _deferEventToChannelAsync<TEvent>(
    TEvent eventData,
    Type eventType,
    MessageId messageId,
    IMessageEnvelope? sourceEnvelope,
    bool eventStoreOnly,
    IDeferredOutboxChannel deferredChannel,
    MessageHop? stampedBy = null) {

    // 1. Resolve destination topic
    string? destination = eventStoreOnly ? null : _resolveDestination(eventData, eventType);

    // 2. Extract aggregate ID and add to hop metadata
    var hopMetadata = _createHopMetadata(eventData!, eventType);

    // 3. Add hop indicating message is being deferred. Scope + identity ride the SOURCE HOP captured at the
    // calling site — hop-FIRST — via the SAME shared helpers as _createOutboxEnvelopeWithHop/_addOutboxHop, so the
    // deferred path can't drift to ambient-first (the boundary bug: a detached/worker defer has no ambient).
    var finalScope3 = Security.OutboxHopScope.Resolve(sourceEnvelope, typeof(TEvent), _declaredUnscopedTypes);
    var (correlation3, causation3) = CascadeContext.ResolveHopFirstIdentity(sourceEnvelope);

    // A batch already stamped the event when it recorded the copy, before the local run; the deferred hop
    // carries that stamp's time and identity, so stamping again writes what the handlers saw (#1455)
    var hop = new MessageHop {
      Type = HopType.Current,
      ServiceInstance = _instanceProvider.ToInfo(),
      Topic = _hopTopic(destination),
      Timestamp = stampedBy is null ? DateTimeOffset.UtcNow : stampedBy.Timestamp,
      Metadata = hopMetadata,
      Scope = finalScope3,
      CorrelationId = stampedBy is null ? correlation3 : stampedBy.CorrelationId,
      CausationId = stampedBy is null ? causation3 : stampedBy.CausationId,
      TraceParent = HopStamping.AmbientTraceParent
    };

    // 4. Populate SentAt-phase properties onto the event object (parity with _createEnvelope) so a deferred
    // published event carries SentAt/CorrelationId/identifiers just like a dispatched one.
    var populatedPayload = (TEvent)AutoPopulatePopulatorRegistry.PopulateSent(eventData!, hop, messageId);

    var envelope = new MessageEnvelope<TEvent> {
      MessageId = messageId,
      Payload = populatedPayload,
      Hops = [],
      DispatchContext = _outboxCopyDispatchContext()
    };
    envelope.AddHop(hop);

    // 5. Serialize to OutboxMessage
    var outboxMessage = _serializeToNewOutboxMessage(envelope, populatedPayload, eventType, destination);

    // 6. Queue to deferred channel (NOT direct DB write)
    await deferredChannel.QueueAsync(outboxMessage);
  }

  // ========================================
  // SERIALIZATION HELPERS
  // ========================================

  // The wait a sync call allows: the caller's timeout, or the dispatcher's default.
  private static TimeSpan _syncTimeout(TimeSpan? timeout) => timeout ?? _defaultSyncTimeout;

  // The topic a hop records: the destination it was sent to, or the event store when it was only stored.
  private static string _hopTopic(string? destination) => destination ?? "(event-store)";

  /// <summary>
  /// Creates an OutboxMessage for work coordinator pattern.
  /// Extracts stream_id from aggregate ID or falls back to message ID.
  /// Type information is preserved in the MessageEnvelope&lt;TMessage&gt; instance itself.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/StreamResolutionParityTests.cs:StoredStream_IsTheStreamTheProducerStoredItOnAsync</tests>
  private OutboxMessage _serializeToNewOutboxMessage<TMessage>(
    MessageEnvelope<TMessage> envelope,
    TMessage payload,
    Type payloadType,
    string? destination,
    DateTimeOffset? scheduledFor = null
  ) {
    // DIAGNOSTIC: Check if TMess age is JsonElement BEFORE calling serializer
    if (typeof(TMessage) == typeof(JsonElement)) {
      throw new InvalidOperationException(
        "BUG IN DISPATCHER: _serializeToNewOutboxMessage called with TMessage=JsonElement. " +
        $"MessageId: {envelope.MessageId}. " +
        $"Envelope type: {TypeNameFormatter.DisplayName(envelope.GetType())}. " +
        $"Payload type: {TypeNameFormatter.RuntimeDisplayName(payload)}. " +
        $"PayloadType parameter: {TypeNameFormatter.DisplayName(payloadType)}. " +
        "This indicates Dispatcher is being passed a MessageEnvelope<JsonElement> instead of a strongly-typed envelope.");
    }

    // QueuedAt is the time the message is written to the outbox (#1557): stamped on the copy this row
    // stores and sends, from the injected clock. A message with no QueuedAt property is returned as is.
    envelope.Payload = (TMessage)AutoPopulatePopulatorRegistry.PopulateQueued(envelope.Payload!, _timeProvider.GetUtcNow());

    // The stream its first hop names, or its message id: read the way every receiver reads it, so a consumer stores
    // the message on the stream this producer stores it on (#1798).
    var streamId = StreamIdentity.Of(envelope);

    // Guard: fail-fast if StreamId is Guid.Empty (indicates missing [GenerateStreamId] or unpopulated StreamId)
    if (payload is IEvent) {
      StreamIdGuard.ThrowIfEmpty(streamId, envelope.MessageId.Value, "Dispatcher.Outbox", TypeNameFormatter.DisplayName(payload.GetType()));
    }

    // Priority step 1: declared before the row is built so the number travels inside the stored envelope
    // and sits on the row for the store. The message type is rendered by the shared helper.
    var declaredPriority = _declarePriority(envelope, TypeNameFormatter.AssemblyQualifiedNameOrDisplay(payloadType), scheduledFor);
    envelope.Priority = declaredPriority;

    // Use centralized envelope serializer (REQUIRED)
    if (_envelopeSerializer == null) {
      throw new InvalidOperationException(
        "IEnvelopeSerializer is required but not registered. " +
        "Ensure you call services.AddWhizbang() to register core services.");
    }

    var serialized = _envelopeSerializer.SerializeEnvelope(envelope);
    serialized.JsonEnvelope.Priority = declaredPriority;   // the storage form carries the declaration too

    // Measured on the serialized form, which is what is stored and sent; an oversized message stops here.
    _payloadLimits.Enforce(serialized.JsonEnvelope.Payload, payloadType, envelope.MessageId.Value, streamId,
      envelope.PayloadLimitOverride);

    // DIAGNOSTIC: Log if MessageType is JsonElement (should never happen after serializer checks)
    if (serialized.MessageType.Contains("JsonElement", StringComparison.OrdinalIgnoreCase)) {
      throw new InvalidOperationException(
        $"CRITICAL BUG: EnvelopeSerializer returned MessageType='{serialized.MessageType}' which contains 'JsonElement'. " +
        $"MessageId: {envelope.MessageId}. " +
        $"Envelope type: {TypeNameFormatter.DisplayName(envelope.GetType())}. " +
        $"TMessage type parameter: {TypeNameFormatter.DisplayName(typeof(TMessage))}. " +
        $"Payload type: {TypeNameFormatter.RuntimeDisplayName(payload)}. " +
        $"PayloadType parameter: {TypeNameFormatter.DisplayName(payloadType)}. " +
        "The serializer defensive checks should have caught this!");
    }

    var outboxMessage = new OutboxMessage {
      MessageId = envelope.MessageId.Value,
      Destination = destination,
      Envelope = serialized.JsonEnvelope,
      Metadata = new EnvelopeMetadata {
        MessageId = envelope.MessageId,
        Hops = envelope.CopyHops(),
        EphemeralTtlSeconds = Whizbang.Core.Messaging.EphemeralTtlDeriver.Derive(payload, _ephemeralModeResolver)
      },
      EnvelopeType = serialized.EnvelopeType,
      StreamId = streamId,
      IsEvent = payload is IEvent,
      Flags = Whizbang.Core.Messaging.EventFlagsDeriver.FromPayload(payload, _ephemeralModeResolver),
      Scope = _extractScope(envelope),
      MessageType = serialized.MessageType,
      ScheduledFor = scheduledFor,
      Priority = declaredPriority,
      CollectiveLinkType = CollectivePredecessorLink.TypeFor(payload)
    };

    // FINAL CHECK: Throw if ANY type string contains JsonElement
    if (outboxMessage.MessageType.Contains("JsonElement", StringComparison.OrdinalIgnoreCase) ||
        outboxMessage.EnvelopeType.Contains("JsonElement", StringComparison.OrdinalIgnoreCase)) {
      throw new InvalidOperationException(
        "FINAL CHECK FAILED: OutboxMessage contains JsonElement in type metadata. " +
        $"MessageId={outboxMessage.MessageId}, " +
        $"MessageType={outboxMessage.MessageType}, " +
        $"EnvelopeType={outboxMessage.EnvelopeType}, " +
        $"TMessage={TypeNameFormatter.DisplayName(typeof(TMessage))}, " +
        $"PayloadType={TypeNameFormatter.DisplayName(payloadType)}, " +
        $"Payload runtime type={TypeNameFormatter.RuntimeDisplayName(payload)}. " +
        "This means either: (1) Envelope parameter was MessageEnvelope<JsonElement>, " +
        "(2) Payload was JsonElement, or (3) PayloadType parameter was typeof(JsonElement). " +
        "All these cases should have been caught by earlier checks!");
    }

    return outboxMessage;
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

    // The one writer of the hop's stream key; StreamIdentity reads it back on every storing site (#1798).
    return StreamIdentity.HopMetadata(streamId.Value);
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
  /// <docs>fundamentals/dispatcher/message-cascade#cascade-security-context</docs>
  /// <docs>fundamentals/security/message-security#security-context-in-event-cascades</docs>
  protected abstract Func<object, IMessageEnvelope?, CancellationToken, Task>? GetUntypedReceptorPublisher(Type eventType);

  /// <summary>
  /// For a message with two or more local receptors, a runner that runs every one, in priority order (#1412).
  /// When <paramref name="wantsAnswer"/> is set, the first result that is a <typeparamref name="TAnswer"/> is
  /// returned instead of cascaded; every other result cascades. Null for a message with one receptor or none,
  /// which the single-receptor lookups handle. Generated.
  /// </summary>
  /// <typeparam name="TAnswer">The result type the caller asked for.</typeparam>
  /// <param name="messageType">The message's runtime type.</param>
  /// <param name="wantsAnswer">Whether the caller takes a result.</param>
  /// <returns>The runner: (message, source envelope, token) to the answer, or default when none answered.</returns>
  /// <docs>fundamentals/receptors/receptor-priority</docs>
  protected virtual Func<object, IMessageEnvelope?, CancellationToken, Task<TAnswer?>>? GetLocalReceptorRunner<TAnswer>(Type messageType, bool wantsAnswer) => null;

  /// <summary>
  /// The interfaces and base types this assembly's local receptors are declared for (#1418). Generated.
  /// </summary>
  /// <docs>fundamentals/dispatcher/message-cascade#mediator-lookup</docs>
  protected virtual IReadOnlyList<Type> GetPolymorphicReceptorMessageTypes() => [];

  /// <summary>
  /// Where receptors declared for interfaces and base types come from: this assembly's tables, then every
  /// other assembly's lookup, each as its declared types and the publisher for one of them (#1418).
  /// </summary>
  private IEnumerable<(IReadOnlyList<Type> DeclaredTypes, Func<Type, Func<object, IMessageEnvelope?, CancellationToken, Task>?> PublisherFor)> _baseTypeReceptorSources() {
    yield return (GetPolymorphicReceptorMessageTypes(), GetUntypedReceptorPublisher);
    foreach (var lookup in _foreignLookups()) {
      yield return (lookup.LookupPolymorphicReceptorMessageTypes(), lookup.LookupUntypedReceptorPublisher);
    }
  }

  /// <summary>
  /// Hands a message that has no receptor of its own to the receptors declared for an interface or base type
  /// it implements, as a mediator does (#1418): this assembly's, then other assemblies', then runtime
  /// registrations. Returns whether any ran.
  /// </summary>
  private async Task<bool> _runBaseTypeReceptorsAsync(object message, Type messageType, IMessageEnvelope? sourceEnvelope,
      CancellationToken cancellationToken) {
    var ran = false;
    foreach (var (declaredTypes, publisherFor) in _baseTypeReceptorSources()) {
      foreach (var declared in declaredTypes) {
        if (declared != messageType && declared.IsAssignableFrom(messageType) && publisherFor(declared) is { } publish) {
          await publish(message, sourceEnvelope, cancellationToken).ConfigureAwait(false);
          ran = true;
        }
      }
    }
    if (_receptorRegistry is not null) {
      var runtime = _runtimeReceptorsForBaseTypesOf(_receptorRegistry, messageType);
      if (runtime.Count > 0) {
        await _invokeRuntimeLocalReceptorsAsync(runtime, message, messageType, cancellationToken).ConfigureAwait(false);
        ran = true;
      }
    }
    return ran;
  }

  /// <summary>
  /// The receptors registered at runtime for an interface or base type of <paramref name="messageType"/> at the local
  /// stages, each instance once (#1418).
  /// </summary>
  private static List<ReceptorInfo> _runtimeReceptorsForBaseTypesOf(IReceptorRegistry registry, Type messageType) {
    var runtime = new List<ReceptorInfo>();
    foreach (var stage in _localStages) {
      foreach (var receptor in registry.GetRuntimeReceptorsForBaseTypesOf(messageType, stage)) {
        if (receptor.RuntimeInstance is { } instance && !runtime.Exists(r => ReferenceEquals(r.RuntimeInstance, instance))) {
          runtime.Add(receptor);
        }
      }
    }
    return runtime;
  }

  /// <summary>
  /// Whether a receptor's result answers a caller that asked for <typeparamref name="TAnswer"/>: the result
  /// itself, or the value of a <c>Routed&lt;T&gt;</c> when the caller asked for the inner type. Generic, so a
  /// value-type result is matched without boxing. Used by the generated runner (#1412).
  /// </summary>
  /// <typeparam name="TAnswer">The type the caller asked for.</typeparam>
  /// <typeparam name="TResponse">The receptor's declared response type.</typeparam>
  /// <param name="result">The receptor's result.</param>
  /// <param name="answer">The answer, when this returns true.</param>
  /// <returns>True when the result answers the caller.</returns>
  protected static bool TryTakeLocalAnswer<TAnswer, TResponse>(TResponse result, out TAnswer? answer) {
    if (result is TAnswer typed) {
      answer = typed;
      return true;
    }
    if (result is IRouted { Value: TAnswer inner }) {
      answer = inner;
      return true;
    }
    answer = default;
    return false;
  }

  private VoidReceptorInvoker? _runnerAsVoid(Type messageType) =>
    GetLocalReceptorRunner<object>(messageType, wantsAnswer: false) is { } runner
      ? msg => new ValueTask(runner(msg, null, CancellationToken.None))
      : null;

  private ReceptorInvoker<TResult>? _runnerAsResult<TResult>(Type messageType) =>
    GetLocalReceptorRunner<TResult>(messageType, wantsAnswer: true) is { } runner
      ? async msg => (await runner(msg, null, CancellationToken.None).ConfigureAwait(false))!
      : null;

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
  /// <docs>fundamentals/dispatcher/message-cascade#routed-message-cascading</docs>
  /// <tests>tests/Whizbang.Generators.Tests/ReceptorDiscoveryGeneratorTests.cs</tests>
  protected abstract Dispatch.DispatchModes? GetReceptorDefaultRouting(Type messageType);

  /// <summary>
  /// AOT-compatible logging for security-related dispatcher events.
  /// Uses compile-time LoggerMessage source generator for zero-allocation, high-performance logging.
  /// </summary>
  private static partial class Log {
    /// <summary>Logs a warning when security context propagates with an empty GUID UserId.</summary>
    [LoggerMessage(
      EventId = 1,
      Level = LogLevel.Warning,
      Message = "Security context is being propagated with empty GUID (00000000-0000-0000-0000-000000000000) as UserId. " +
                "This may indicate the original request didn't capture the user's identity correctly. TenantId: {TenantId}",
      SkipEnabledCheck = true)]
    public static partial void EmptyGuidUserIdPropagated(ILogger logger, string? tenantId);

    /// <summary>Logs a warning when an explicit context (AsSystem/RunAs) has an empty GUID UserId.</summary>
    [LoggerMessage(
      EventId = 2,
      Level = LogLevel.Warning,
      Message = "Explicit security context (AsSystem/RunAs) created with empty GUID (00000000-0000-0000-0000-000000000000) as UserId. " +
                "This may indicate the originating request didn't have proper user context. ContextType: {ContextType}, TenantId: {TenantId}",
      SkipEnabledCheck = true)]
    public static partial void EmptyGuidUserIdInExplicitContext(ILogger logger, string? contextType, string? tenantId);

    /// <summary>Logs an auto-generated StreamId for a message type.</summary>
    [LoggerMessage(
      EventId = 3,
      Level = LogLevel.Debug,
      Message = "[STREAM_ID] Auto-generated StreamId={StreamId} for MessageType={MessageType} (OnlyIfEmpty={OnlyIfEmpty})")]
    public static partial void StreamIdAutoGenerated(ILogger logger, Guid streamId, string messageType, bool onlyIfEmpty);

    /// <summary>Logs StreamId propagation from a source envelope to a cascaded event.</summary>
    [LoggerMessage(
      EventId = 4,
      Level = LogLevel.Debug,
      Message = "[STREAM_ID] Propagated StreamId={StreamId} from source {SourceType} to cascaded {EventType}")]
    public static partial void StreamIdPropagatedFromSource(ILogger logger, Guid streamId, string sourceType, string eventType);

    /// <summary>Logs a warning when outbox delivery is skipped because no IWorkCoordinatorStrategy is registered.</summary>
    [LoggerMessage(
      EventId = 5,
      Level = LogLevel.Warning,
      Message = "[DISPATCHER] SendManyAsync: No IWorkCoordinatorStrategy registered — " +
                "outbox delivery skipped. All {MessageCount} messages were handled by local receptors.")]
    public static partial void OutboxDeliverySkipped(ILogger logger, int messageCount);

    [LoggerMessage(
      EventId = 6,
      Level = LogLevel.Debug,
      Message = "[RPC] The receptor for {MessageType} returned {ResultType}; extracting the requested {RequestedType} from it")]
    public static partial void ExtractingResponse(ILogger logger, string messageType, string resultType, string requestedType);

    [LoggerMessage(
      EventId = 7,
      Level = LogLevel.Warning,
      Message = "[CASCADE] A receptor returned {MessageType}, which is neither a command nor an event; nothing in this service " +
                "handles it and it belongs to this service, so it is not sent anywhere. It was skipped.")]
    public static partial void UnhandledResultSkipped(ILogger logger, string messageType);

    [LoggerMessage(
      EventId = 8,
      Level = LogLevel.Error,
      Message = "A {Stage} receptor failed for message {MessageId}. The stage is detached, so the dispatch was not failed.")]
    public static partial void DetachedStageFailed(ILogger logger, Exception exception, LifecycleStage stage, Guid messageId);

    [LoggerMessage(EventId = 9, Level = LogLevel.Debug, Message = "ProcessTagsIfEnabledAsync called for {MessageType}")]
    public static partial void TagProcessingEntered(ILogger logger, string messageType);

    [LoggerMessage(EventId = 10, Level = LogLevel.Debug, Message = "Tag processing is DISABLED - returning early")]
    public static partial void TagProcessingDisabled(ILogger logger);

    [LoggerMessage(EventId = 11, Level = LogLevel.Debug,
      Message = "TagProcessingMode is {TagProcessingMode}, not AfterReceptorCompletion - returning early")]
    public static partial void TagProcessingModeSkipped(ILogger logger, TagProcessingMode tagProcessingMode);

    [LoggerMessage(EventId = 12, Level = LogLevel.Debug, Message = "No IMessageTagProcessor registered - returning early")]
    public static partial void NoTagProcessorRegistered(ILogger logger);

    [LoggerMessage(EventId = 13, Level = LogLevel.Debug, Message = "Calling ProcessTagsAsync at AfterReceptorCompletion...")]
    public static partial void TagProcessingCalling(ILogger logger);

    [LoggerMessage(EventId = 14, Level = LogLevel.Debug, Message = "ProcessTagsAsync completed")]
    public static partial void TagProcessingCompleted(ILogger logger);
  }
}
