// Template snippets for code generation.
// These are valid C# methods containing #region blocks that get extracted
// and used as templates during code generation.

using System;
using System.Threading.Tasks;
using Whizbang.Generators.Templates.Placeholders;
using Microsoft.Extensions.DependencyInjection;

namespace Whizbang.Generators.Templates.Snippets;

/// <summary>
/// Contains template snippets for dispatcher code generation.
/// Each #region contains a code snippet that gets extracted and has placeholders replaced.
/// </summary>
public class DispatcherSnippets {
  // Placeholder properties to make snippets compile
  protected IServiceProvider ServiceProvider => null!;
  protected IServiceScopeFactory _scopeFactory => null!;

  /// <summary>
  /// Example method showing snippet structure for Send routing.
  /// The actual snippets are extracted from #region blocks.
  /// </summary>
  protected ReceptorInvoker<TResult>? SendRoutingExample<TResult>(object message, Type messageType) {
    #region SEND_ROUTING_SNIPPET
    if (messageType == typeof(__MESSAGE_TYPE__)) {
      // Check if receptor is registered before returning invoker
      // Use a temporary scope to check registration (try keyed first, fall back to non-keyed)
      using (var checkScope = _scopeFactory.CreateScope()) {
        var checkReceptor = checkScope.ServiceProvider.GetKeyedService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>>("__RECEPTOR_CLASS__")
                         ?? checkScope.ServiceProvider.GetService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>>();
        if (checkReceptor == null) {
          return null;
        }
      }

      [System.Diagnostics.DebuggerStepThrough]
      async ValueTask<TResult> InvokeReceptor(object msg) {
        // Create scope for each invocation to properly handle scoped services
        var scope = _scopeFactory.CreateScope();
        try {
          // Establish message context from ambient AsyncLocal (ScopeContextAccessor)
          // This enables receptors to access UserId, TenantId via IMessageContext
          global::Whizbang.Core.Security.SecurityContextHelper.EstablishMessageContextForCascade(scope.ServiceProvider);

          // Await perspective sync if receptor has [AwaitPerspectiveSync] attributes
          __SYNC_AWAIT_CODE__
          // Try keyed service first (generated registrations), fall back to non-keyed (manual/test registrations)
          var receptor = scope.ServiceProvider.GetKeyedService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>>("__RECEPTOR_CLASS__")
                      ?? scope.ServiceProvider.GetRequiredService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>>();
          var typedMsg = (__MESSAGE_TYPE__)msg;
          var result = await receptor.HandleAsync(typedMsg);
          // Unwrap Routed<T> if receptor returned a wrapped value for cascade control
          // Cast to object first to avoid C# compile-time type checking (CS8121)
          if ((object)result is global::Whizbang.Core.Dispatch.IRouted routedResult && routedResult.Value is TResult unwrappedValue) {
            return unwrappedValue;
          }
          return (TResult)(object)result!;
        } finally {
          if (scope is IAsyncDisposable asyncDisposable) {
            await asyncDisposable.DisposeAsync();
          } else {
            scope.Dispose();
          }
        }
      }

      return InvokeReceptor;
    }
    #endregion

    return null;
  }

  /// <summary>
  /// Example method showing snippet structure for Publish routing.
  /// The actual snippets are extracted from #region blocks.
  /// </summary>
  protected ReceptorPublisher<TEvent> PublishRoutingExample<TEvent>(TEvent @event, Type eventType) {
    #region PUBLISH_ROUTING_SNIPPET
    if (eventType == typeof(__MESSAGE_TYPE__)) {
      [System.Diagnostics.DebuggerStepThrough]
      async Task PublishToReceptors(TEvent evt) {
        // Create scope for each invocation to properly handle scoped services
        var scope = _scopeFactory.CreateScope();
        try {
          // Establish message context from ambient AsyncLocal (ScopeContextAccessor)
          // This enables receptors to access UserId, TenantId via IMessageContext
          global::Whizbang.Core.Security.SecurityContextHelper.EstablishMessageContextForCascade(scope.ServiceProvider);

          var receptors = scope.ServiceProvider.GetServices<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, object>>();
          var typedEvt = (__MESSAGE_TYPE__)(object)evt!;
          foreach (var receptor in receptors) {
            await receptor.HandleAsync(typedEvt);
          }
        } finally {
          if (scope is IAsyncDisposable asyncDisposable) {
            await asyncDisposable.DisposeAsync();
          } else {
            scope.Dispose();
          }
        }
      }

      return PublishToReceptors;
    }
    #endregion

    return _ => Task.CompletedTask;
  }

  /// <summary>
  /// Example method showing snippet structure for untyped Publish routing.
  /// Used by auto-cascade to publish events extracted from receptor return values.
  /// Establishes security context from source envelope before invoking receptors.
  /// </summary>
  protected Func<object, IMessageEnvelope?, CancellationToken, Task>? UntypedPublishRoutingExample(Type eventType) {
    #region UNTYPED_PUBLISH_ROUTING_SNIPPET
    if (eventType == typeof(__MESSAGE_TYPE__)) {
      [System.Diagnostics.DebuggerStepThrough]
      async Task PublishToReceptorsUntyped(object evt, global::Whizbang.Core.Observability.IMessageEnvelope? sourceEnvelope, global::System.Threading.CancellationToken cancellationToken) {
        // Create scope for each invocation to properly handle scoped services
        var scope = _scopeFactory.CreateScope();
        try {
          // Establish security context from source envelope before invoking receptors
          // This enables receptors to access UserId, TenantId via IMessageContext
          if (sourceEnvelope is not null) {
            await global::Whizbang.Core.Security.SecurityContextHelper.EstablishFullContextAsync(
                sourceEnvelope,
                scope.ServiceProvider,
                cancellationToken);
          } else {
            // Cascade path: Establish message context from ambient AsyncLocal (ScopeContextAccessor)
            // This enables cascaded receptors to access UserId, TenantId via IMessageContext
            global::Whizbang.Core.Security.SecurityContextHelper.EstablishMessageContextForCascade(scope.ServiceProvider);
          }

          var receptors = scope.ServiceProvider.GetServices<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, object>>();
          var voidReceptors = scope.ServiceProvider.GetServices<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>>();
          var typedEvt = (__MESSAGE_TYPE__)evt;
          foreach (var receptor in receptors) {
            await receptor.HandleAsync(typedEvt, cancellationToken);
          }
          foreach (var voidReceptor in voidReceptors) {
            await voidReceptor.HandleAsync(typedEvt, cancellationToken);
          }
        } finally {
          if (scope is IAsyncDisposable asyncDisposable) {
            await asyncDisposable.DisposeAsync();
          } else {
            scope.Dispose();
          }
        }
      }

      return PublishToReceptorsUntyped;
    }
    #endregion

    return null;
  }

  /// <summary>
  /// Example method showing snippet structure for receptor registration.
  /// Uses keyed services to allow multiple handlers for the same message type.
  /// Also registers non-keyed for multi-handler resolution (GetServices in Publish/cascade).
  /// </summary>
  public void ReceptorRegistrationExample(IServiceCollection services) {
    #region RECEPTOR_REGISTRATION_SNIPPET
    // Register as keyed for single-handler resolution (LocalInvoke RPC)
    services.AddKeyedTransient<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>, __RECEPTOR_CLASS__>("__RECEPTOR_CLASS__");
    // Also register as non-keyed for multi-handler resolution (GetServices in Publish/cascade)
    services.AddTransient<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>, __RECEPTOR_CLASS__>();
    #endregion
  }

  /// <summary>
  /// Example method showing snippet structure for void receptor registration.
  /// Uses keyed services to allow multiple handlers for the same message type.
  /// Also registers non-keyed for multi-handler resolution (GetServices in Publish/cascade).
  /// </summary>
  public void VoidReceptorRegistrationExample(IServiceCollection services) {
    #region VOID_RECEPTOR_REGISTRATION_SNIPPET
    // Register as keyed for single-handler resolution (LocalInvoke RPC)
    services.AddKeyedTransient<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>, __RECEPTOR_CLASS__>("__RECEPTOR_CLASS__");
    // Also register as non-keyed for multi-handler resolution (GetServices in Publish/cascade)
    services.AddTransient<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>, __RECEPTOR_CLASS__>();
    #endregion
  }

  /// <summary>
  /// Example method showing snippet structure for void receptor routing.
  /// </summary>
  protected VoidReceptorInvoker? VoidSendRoutingExample(object message, Type messageType) {
    #region VOID_SEND_ROUTING_SNIPPET
    if (messageType == typeof(__MESSAGE_TYPE__)) {
      // Check if receptor is registered before returning invoker
      // Use a temporary scope to check registration (try keyed first, fall back to non-keyed)
      using (var checkScope = _scopeFactory.CreateScope()) {
        var checkReceptor = checkScope.ServiceProvider.GetKeyedService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>>("__RECEPTOR_CLASS__")
                         ?? checkScope.ServiceProvider.GetService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>>();
        if (checkReceptor == null) {
          return null;
        }
      }

      [System.Diagnostics.DebuggerStepThrough]
      async ValueTask InvokeReceptor(object msg) {
        // Create scope for each invocation to properly handle scoped services
        var scope = _scopeFactory.CreateScope();
        try {
          // Establish message context from ambient AsyncLocal (ScopeContextAccessor)
          // This enables receptors to access UserId, TenantId via IMessageContext
          global::Whizbang.Core.Security.SecurityContextHelper.EstablishMessageContextForCascade(scope.ServiceProvider);

          // Await perspective sync if receptor has [AwaitPerspectiveSync] attributes
          __SYNC_AWAIT_CODE__
          // Try keyed service first (generated registrations), fall back to non-keyed (manual/test registrations)
          var receptor = scope.ServiceProvider.GetKeyedService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>>("__RECEPTOR_CLASS__")
                      ?? scope.ServiceProvider.GetRequiredService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>>();
          var typedMsg = (__MESSAGE_TYPE__)msg;
          await receptor.HandleAsync(typedMsg);
        } finally {
          if (scope is IAsyncDisposable asyncDisposable) {
            await asyncDisposable.DisposeAsync();
          } else {
            scope.Dispose();
          }
        }
      }

      return InvokeReceptor;
    }
    #endregion

    return null;
  }

  /// <summary>
  /// Example method showing snippet structure for sync receptor routing.
  /// Invokes ISyncReceptor&lt;TMessage, TResponse&gt; synchronously and wraps result in ValueTask.
  /// </summary>
  protected SyncReceptorInvoker<TResult>? SyncSendRoutingExample<TResult>(object message, Type messageType) {
    #region SYNC_SEND_ROUTING_SNIPPET
    if (messageType == typeof(__MESSAGE_TYPE__)) {
      // Check if receptor is registered before returning invoker
      // Use a temporary scope to check registration (try keyed first, fall back to non-keyed)
      using (var checkScope = _scopeFactory.CreateScope()) {
        var checkReceptor = checkScope.ServiceProvider.GetKeyedService<__SYNC_RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>>("__RECEPTOR_CLASS__")
                         ?? checkScope.ServiceProvider.GetService<__SYNC_RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>>();
        if (checkReceptor == null) {
          return null;
        }
      }

      [System.Diagnostics.DebuggerStepThrough]
      TResult InvokeReceptor(object msg) {
        // Create scope for each invocation to properly handle scoped services
        using var scope = _scopeFactory.CreateScope();
        // Establish message context from ambient AsyncLocal (ScopeContextAccessor)
        // This enables receptors to access UserId, TenantId via IMessageContext
        global::Whizbang.Core.Security.SecurityContextHelper.EstablishMessageContextForCascade(scope.ServiceProvider);

        // Try keyed service first (generated registrations), fall back to non-keyed (manual/test registrations)
        var receptor = scope.ServiceProvider.GetKeyedService<__SYNC_RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>>("__RECEPTOR_CLASS__")
                    ?? scope.ServiceProvider.GetRequiredService<__SYNC_RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>>();
        var typedMsg = (__MESSAGE_TYPE__)msg;
        var result = receptor.Handle(typedMsg);
        // Unwrap Routed<T> if receptor returned a wrapped value for cascade control
        // Cast to object first to avoid C# compile-time type checking (CS8121)
        if ((object)result is global::Whizbang.Core.Dispatch.IRouted routedResult && routedResult.Value is TResult unwrappedValue) {
          return unwrappedValue;
        }
        return (TResult)(object)result!;
      }

      return InvokeReceptor;
    }
    #endregion

    return null;
  }

  /// <summary>
  /// Example method showing snippet structure for void sync receptor routing.
  /// Invokes ISyncReceptor&lt;TMessage&gt; synchronously.
  /// </summary>
  protected VoidSyncReceptorInvoker? VoidSyncSendRoutingExample(object message, Type messageType) {
    #region VOID_SYNC_SEND_ROUTING_SNIPPET
    if (messageType == typeof(__MESSAGE_TYPE__)) {
      // Check if receptor is registered before returning invoker
      // Use a temporary scope to check registration (try keyed first, fall back to non-keyed)
      using (var checkScope = _scopeFactory.CreateScope()) {
        var checkReceptor = checkScope.ServiceProvider.GetKeyedService<__SYNC_RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>>("__RECEPTOR_CLASS__")
                         ?? checkScope.ServiceProvider.GetService<__SYNC_RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>>();
        if (checkReceptor == null) {
          return null;
        }
      }

      [System.Diagnostics.DebuggerStepThrough]
      void InvokeReceptor(object msg) {
        // Create scope for each invocation to properly handle scoped services
        using var scope = _scopeFactory.CreateScope();
        // Establish message context from ambient AsyncLocal (ScopeContextAccessor)
        // This enables receptors to access UserId, TenantId via IMessageContext
        global::Whizbang.Core.Security.SecurityContextHelper.EstablishMessageContextForCascade(scope.ServiceProvider);

        // Try keyed service first (generated registrations), fall back to non-keyed (manual/test registrations)
        var receptor = scope.ServiceProvider.GetKeyedService<__SYNC_RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>>("__RECEPTOR_CLASS__")
                    ?? scope.ServiceProvider.GetRequiredService<__SYNC_RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>>();
        var typedMsg = (__MESSAGE_TYPE__)msg;
        receptor.Handle(typedMsg);
      }

      return InvokeReceptor;
    }
    #endregion

    return null;
  }

  /// <summary>
  /// Example method showing snippet structure for sync receptor registration.
  /// Uses keyed services to allow multiple handlers for the same message type.
  /// Also registers non-keyed for multi-handler resolution (GetServices in Publish/cascade).
  /// </summary>
  public void SyncReceptorRegistrationExample(IServiceCollection services) {
    #region SYNC_RECEPTOR_REGISTRATION_SNIPPET
    // Register as keyed for single-handler resolution (LocalInvoke RPC)
    services.AddKeyedTransient<__SYNC_RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>, __RECEPTOR_CLASS__>("__RECEPTOR_CLASS__");
    // Also register as non-keyed for multi-handler resolution (GetServices in Publish/cascade)
    services.AddTransient<__SYNC_RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>, __RECEPTOR_CLASS__>();
    #endregion
  }

  /// <summary>
  /// Example method showing snippet structure for void sync receptor registration.
  /// Uses keyed services to allow multiple handlers for the same message type.
  /// Also registers non-keyed for multi-handler resolution (GetServices in Publish/cascade).
  /// </summary>
  public void VoidSyncReceptorRegistrationExample(IServiceCollection services) {
    #region VOID_SYNC_RECEPTOR_REGISTRATION_SNIPPET
    // Register as keyed for single-handler resolution (LocalInvoke RPC)
    services.AddKeyedTransient<__SYNC_RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>, __RECEPTOR_CLASS__>("__RECEPTOR_CLASS__");
    // Also register as non-keyed for multi-handler resolution (GetServices in Publish/cascade)
    services.AddTransient<__SYNC_RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>, __RECEPTOR_CLASS__>();
    #endregion
  }

  /// <summary>
  /// Example method showing snippet structure for diagnostic messages.
  /// </summary>
  public void DiagnosticMessageExample(System.Text.StringBuilder message) {
    #region DIAGNOSTIC_MESSAGE_SNIPPET
    message.AppendLine("  [__INDEX__] __RECEPTOR_NAME__");
    message.AppendLine("      Message:  __MESSAGE_NAME__");
    message.AppendLine("      Response: __RESPONSE_NAME__");
    #endregion
  }

  /// <summary>
  /// Header snippet for generated files.
  /// </summary>
  public void GeneratedFileHeader() {
    #region GENERATED_FILE_HEADER
    // <auto-generated/>
    // Generated at: __TIMESTAMP__
#nullable enable
    #endregion
  }

  /// <summary>
  /// Example method showing snippet structure for lifecycle routing with void receptors.
  /// Uses IServiceScopeFactory to create a scope for each invocation, enabling resolution
  /// of scoped dependencies (e.g., DbContext, IOrchestratorAgent).
  /// </summary>
  protected async ValueTask LifecycleRoutingVoidExample(
      object message,
      LifecycleStage stage,
      CancellationToken cancellationToken) {
    #region LIFECYCLE_ROUTING_VOID_SNIPPET
    if (messageType == typeof(__MESSAGE_TYPE__) && stage == __LIFECYCLE_STAGE__) {
      using var scope = _scopeFactory.CreateScope();
      // Establish message context from ambient AsyncLocal (ScopeContextAccessor)
      // This enables receptors to access UserId, TenantId via IMessageContext
      global::Whizbang.Core.Security.SecurityContextHelper.EstablishMessageContextForCascade(scope.ServiceProvider);

      // Try keyed service first (generated registrations), fall back to non-keyed (manual/test registrations)
      var receptor = scope.ServiceProvider.GetKeyedService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>>("__RECEPTOR_CLASS__")
                  ?? scope.ServiceProvider.GetRequiredService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>>();
      await receptor.HandleAsync((__MESSAGE_TYPE__)message, cancellationToken);
    }
    #endregion
  }

  /// <summary>
  /// Example method showing snippet structure for lifecycle routing with response receptors.
  /// Uses IServiceScopeFactory to create a scope for each invocation, enabling resolution
  /// of scoped dependencies (e.g., DbContext, IOrchestratorAgent).
  /// </summary>
  protected async ValueTask LifecycleRoutingResponseExample(
      object message,
      LifecycleStage stage,
      CancellationToken cancellationToken) {
    #region LIFECYCLE_ROUTING_RESPONSE_SNIPPET
    if (messageType == typeof(__MESSAGE_TYPE__) && stage == __LIFECYCLE_STAGE__) {
      using var scope = _scopeFactory.CreateScope();
      // Establish message context from ambient AsyncLocal (ScopeContextAccessor)
      // This enables receptors to access UserId, TenantId via IMessageContext
      global::Whizbang.Core.Security.SecurityContextHelper.EstablishMessageContextForCascade(scope.ServiceProvider);

      // Try keyed service first (generated registrations), fall back to non-keyed (manual/test registrations)
      var receptor = scope.ServiceProvider.GetKeyedService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>>("__RECEPTOR_CLASS__")
                  ?? scope.ServiceProvider.GetRequiredService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>>();
      await receptor.HandleAsync((__MESSAGE_TYPE__)message, cancellationToken);
    }
    #endregion
  }

  /// <summary>
  /// Example method showing snippet structure for receptor registry routing.
  /// Returns a list of ReceptorInfo for a given (messageType, stage) combination.
  /// Used for async receptors with response.
  /// The delegate accepts a scoped IServiceProvider to resolve receptors with scoped dependencies.
  /// </summary>
  protected IReadOnlyList<global::Whizbang.Core.Messaging.ReceptorInfo> ReceptorRegistryRoutingExample(
      Type messageType,
      LifecycleStage stage) {
    #region RECEPTOR_REGISTRY_ROUTING_SNIPPET
    if (messageType == typeof(__MESSAGE_TYPE__) && stage == __LIFECYCLE_STAGE__) {
      return new global::Whizbang.Core.Messaging.ReceptorInfo[] {
        new global::Whizbang.Core.Messaging.ReceptorInfo(
          MessageType: typeof(__MESSAGE_TYPE__),
          ReceptorId: "__RECEPTOR_CLASS__",
          InvokeAsync: async (sp, msg, envelope, callerInfo, ct) => {
            // Try keyed service first (generated registrations), fall back to non-keyed (manual/test registrations)
            var receptor = sp.GetKeyedService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>>("__RECEPTOR_CLASS__")
                        ?? sp.GetRequiredService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>>();
            var result = await receptor.HandleAsync((__MESSAGE_TYPE__)msg, ct);
            // Unwrap Routed<T> if receptor returned a wrapped value for cascade control
            if ((object)result is global::Whizbang.Core.Dispatch.IRouted routedResult) {
              return routedResult.Value;
            }
            return result;
          },
          SyncAttributes: __SYNC_ATTRIBUTES__
        )
      };
    }
    #endregion

    return Array.Empty<global::Whizbang.Core.Messaging.ReceptorInfo>();
  }

  /// <summary>
  /// Example method showing snippet structure for receptor registry routing.
  /// Returns a list of ReceptorInfo for a given (messageType, stage) combination.
  /// Used for void async receptors.
  /// The delegate accepts a scoped IServiceProvider to resolve receptors with scoped dependencies.
  /// </summary>
  protected IReadOnlyList<global::Whizbang.Core.Messaging.ReceptorInfo> ReceptorRegistryVoidRoutingExample(
      Type messageType,
      LifecycleStage stage) {
    #region RECEPTOR_REGISTRY_VOID_ROUTING_SNIPPET
    if (messageType == typeof(__MESSAGE_TYPE__) && stage == __LIFECYCLE_STAGE__) {
      return new global::Whizbang.Core.Messaging.ReceptorInfo[] {
        new global::Whizbang.Core.Messaging.ReceptorInfo(
          MessageType: typeof(__MESSAGE_TYPE__),
          ReceptorId: "__RECEPTOR_CLASS__",
          InvokeAsync: async (sp, msg, envelope, callerInfo, ct) => {
            // Try keyed service first (generated registrations), fall back to non-keyed (manual/test registrations)
            var receptor = sp.GetKeyedService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>>("__RECEPTOR_CLASS__")
                        ?? sp.GetRequiredService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>>();
            await receptor.HandleAsync((__MESSAGE_TYPE__)msg, ct);
            return null;
          },
          SyncAttributes: __SYNC_ATTRIBUTES__
        )
      };
    }
    #endregion

    return Array.Empty<global::Whizbang.Core.Messaging.ReceptorInfo>();
  }

  /// <summary>
  /// Example method showing snippet structure for traced receptor registry routing.
  /// Returns a list of ReceptorInfo for a given (messageType, stage) combination.
  /// Used for async receptors with response and [WhizbangTrace] attribute.
  /// Includes timing capture, ITracer integration, and explicit trace marking.
  /// </summary>
  protected IReadOnlyList<global::Whizbang.Core.Messaging.ReceptorInfo> ReceptorRegistryTracedRoutingExample(
      Type messageType,
      LifecycleStage stage) {
    #region RECEPTOR_REGISTRY_TRACED_ROUTING_SNIPPET
    if (messageType == typeof(__MESSAGE_TYPE__) && stage == __LIFECYCLE_STAGE__) {
      return new global::Whizbang.Core.Messaging.ReceptorInfo[] {
        new global::Whizbang.Core.Messaging.ReceptorInfo(
          MessageType: typeof(__MESSAGE_TYPE__),
          ReceptorId: "__RECEPTOR_CLASS__",
          InvokeAsync: async (sp, msg, envelope, callerInfo, ct) => {
            // Capture timing with debug-aware clock
            var clock = sp.GetService<global::Whizbang.Core.Diagnostics.IDebuggerAwareClock>();
            var startTime = clock?.GetCurrentTimestamp() ?? System.Diagnostics.Stopwatch.GetTimestamp();

            // Get tracer for explicit trace output
            var tracer = sp.GetService<global::Whizbang.Core.Tracing.ITracer>();

            // Begin trace span if tracing is enabled for this handler
            tracer?.BeginHandlerTrace(
                "__RECEPTOR_CLASS__",
                typeof(__MESSAGE_TYPE__).Name,
                __HANDLER_COUNT__,
                __IS_EXPLICIT__);

            object? result = null;
            System.Exception? handlerException = null;
            var status = global::Whizbang.Core.Tracing.HandlerStatus.Success;

            try {
              // Try keyed service first (generated registrations), fall back to non-keyed (manual/test registrations)
              var receptor = sp.GetKeyedService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>>("__RECEPTOR_CLASS__")
                          ?? sp.GetRequiredService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>>();
              result = await receptor.HandleAsync((__MESSAGE_TYPE__)msg, ct);
              // Unwrap Routed<T> if receptor returned a wrapped value for cascade control
              if ((object)result is global::Whizbang.Core.Dispatch.IRouted routedResult) {
                result = routedResult.Value;
              }
            } catch (System.Exception ex) {
              handlerException = ex;
              status = global::Whizbang.Core.Tracing.HandlerStatus.Failed;
              throw;
            } finally {
              // Capture end time
              var endTime = clock?.GetCurrentTimestamp() ?? System.Diagnostics.Stopwatch.GetTimestamp();
              var durationMs = (endTime - startTime) / (double)System.Diagnostics.Stopwatch.Frequency * 1000;

              // End trace span
              tracer?.EndHandlerTrace(
                  "__RECEPTOR_CLASS__",
                  typeof(__MESSAGE_TYPE__).Name,
                  status,
                  durationMs,
                  startTime,
                  endTime,
                  handlerException);
            }
            return result;
          },
          SyncAttributes: __SYNC_ATTRIBUTES__
        )
      };
    }
    #endregion

    return Array.Empty<global::Whizbang.Core.Messaging.ReceptorInfo>();
  }

  /// <summary>
  /// Example method showing snippet structure for traced void receptor registry routing.
  /// Returns a list of ReceptorInfo for a given (messageType, stage) combination.
  /// Used for void async receptors with [WhizbangTrace] attribute.
  /// Includes timing capture, ITracer integration, and explicit trace marking.
  /// </summary>
  protected IReadOnlyList<global::Whizbang.Core.Messaging.ReceptorInfo> ReceptorRegistryTracedVoidRoutingExample(
      Type messageType,
      LifecycleStage stage) {
    #region RECEPTOR_REGISTRY_TRACED_VOID_ROUTING_SNIPPET
    if (messageType == typeof(__MESSAGE_TYPE__) && stage == __LIFECYCLE_STAGE__) {
      return new global::Whizbang.Core.Messaging.ReceptorInfo[] {
        new global::Whizbang.Core.Messaging.ReceptorInfo(
          MessageType: typeof(__MESSAGE_TYPE__),
          ReceptorId: "__RECEPTOR_CLASS__",
          InvokeAsync: async (sp, msg, envelope, callerInfo, ct) => {
            // Capture timing with debug-aware clock
            var clock = sp.GetService<global::Whizbang.Core.Diagnostics.IDebuggerAwareClock>();
            var startTime = clock?.GetCurrentTimestamp() ?? System.Diagnostics.Stopwatch.GetTimestamp();

            // Get tracer for explicit trace output
            var tracer = sp.GetService<global::Whizbang.Core.Tracing.ITracer>();

            // Begin trace span if tracing is enabled for this handler
            tracer?.BeginHandlerTrace(
                "__RECEPTOR_CLASS__",
                typeof(__MESSAGE_TYPE__).Name,
                __HANDLER_COUNT__,
                __IS_EXPLICIT__);

            System.Exception? handlerException = null;
            var status = global::Whizbang.Core.Tracing.HandlerStatus.Success;

            try {
              // Try keyed service first (generated registrations), fall back to non-keyed (manual/test registrations)
              var receptor = sp.GetKeyedService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>>("__RECEPTOR_CLASS__")
                          ?? sp.GetRequiredService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>>();
              await receptor.HandleAsync((__MESSAGE_TYPE__)msg, ct);
            } catch (System.Exception ex) {
              handlerException = ex;
              status = global::Whizbang.Core.Tracing.HandlerStatus.Failed;
              throw;
            } finally {
              // Capture end time
              var endTime = clock?.GetCurrentTimestamp() ?? System.Diagnostics.Stopwatch.GetTimestamp();
              var durationMs = (endTime - startTime) / (double)System.Diagnostics.Stopwatch.Frequency * 1000;

              // End trace span
              tracer?.EndHandlerTrace(
                  "__RECEPTOR_CLASS__",
                  typeof(__MESSAGE_TYPE__).Name,
                  status,
                  durationMs,
                  startTime,
                  endTime,
                  handlerException);
            }
            return null;
          },
          SyncAttributes: __SYNC_ATTRIBUTES__
        )
      };
    }
    #endregion

    return Array.Empty<global::Whizbang.Core.Messaging.ReceptorInfo>();
  }

  /// <summary>
  /// Example method showing snippet structure for "any receptor" routing (non-void).
  /// Used by void LocalInvokeAsync paths to find non-void receptors for cascading.
  /// Returns a type-erased delegate that invokes the receptor and returns the result as object.
  /// </summary>
  protected Func<object, ValueTask<object?>>? AnyRoutingNonVoidExample(object message, Type messageType) {
    #region ANY_SEND_ROUTING_NONVOID_SNIPPET
    if (messageType == typeof(__MESSAGE_TYPE__)) {
      // Check if receptor is registered before returning invoker
      // Use a temporary scope to check registration (try keyed first, fall back to non-keyed)
      using (var checkScope = _scopeFactory.CreateScope()) {
        var checkReceptor = checkScope.ServiceProvider.GetKeyedService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>>("__RECEPTOR_CLASS__")
                         ?? checkScope.ServiceProvider.GetService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>>();
        if (checkReceptor == null) {
          return null;
        }
      }

      [System.Diagnostics.DebuggerStepThrough]
      async ValueTask<object?> InvokeReceptor(object msg) {
        // Create scope for each invocation to properly handle scoped services
        var scope = _scopeFactory.CreateScope();
        try {
          // Establish message context from ambient AsyncLocal (ScopeContextAccessor)
          // This enables receptors to access UserId, TenantId via IMessageContext
          global::Whizbang.Core.Security.SecurityContextHelper.EstablishMessageContextForCascade(scope.ServiceProvider);

          // Await perspective sync if receptor has [AwaitPerspectiveSync] attributes
          __SYNC_AWAIT_CODE__
          // Try keyed service first (generated registrations), fall back to non-keyed (manual/test registrations)
          var receptor = scope.ServiceProvider.GetKeyedService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>>("__RECEPTOR_CLASS__")
                      ?? scope.ServiceProvider.GetRequiredService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>>();
          var typedMsg = (__MESSAGE_TYPE__)msg;
          var result = await receptor.HandleAsync(typedMsg);
          // Unwrap Routed<T> if receptor returned a wrapped value for cascade control
          if ((object)result is global::Whizbang.Core.Dispatch.IRouted routedResult) {
            return routedResult.Value;
          }
          return result;
        } finally {
          if (scope is IAsyncDisposable asyncDisposable) {
            await asyncDisposable.DisposeAsync();
          } else {
            scope.Dispose();
          }
        }
      }

      return InvokeReceptor;
    }
    #endregion

    return null;
  }

  /// <summary>
  /// Example method showing snippet structure for "any receptor" routing (void).
  /// Used by void LocalInvokeAsync paths to find void receptors (fallback when non-void not found).
  /// Returns a type-erased delegate that invokes the receptor and returns null.
  /// </summary>
  protected Func<object, ValueTask<object?>>? AnyRoutingVoidExample(object message, Type messageType) {
    #region ANY_SEND_ROUTING_VOID_SNIPPET
    if (messageType == typeof(__MESSAGE_TYPE__)) {
      // Check if receptor is registered before returning invoker
      // Use a temporary scope to check registration (try keyed first, fall back to non-keyed)
      using (var checkScope = _scopeFactory.CreateScope()) {
        var checkReceptor = checkScope.ServiceProvider.GetKeyedService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>>("__RECEPTOR_CLASS__")
                         ?? checkScope.ServiceProvider.GetService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>>();
        if (checkReceptor == null) {
          return null;
        }
      }

      [System.Diagnostics.DebuggerStepThrough]
      async ValueTask<object?> InvokeReceptor(object msg) {
        // Create scope for each invocation to properly handle scoped services
        var scope = _scopeFactory.CreateScope();
        try {
          // Establish message context from ambient AsyncLocal (ScopeContextAccessor)
          // This enables receptors to access UserId, TenantId via IMessageContext
          global::Whizbang.Core.Security.SecurityContextHelper.EstablishMessageContextForCascade(scope.ServiceProvider);

          // Await perspective sync if receptor has [AwaitPerspectiveSync] attributes
          __SYNC_AWAIT_CODE__
          // Try keyed service first (generated registrations), fall back to non-keyed (manual/test registrations)
          var receptor = scope.ServiceProvider.GetKeyedService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>>("__RECEPTOR_CLASS__")
                      ?? scope.ServiceProvider.GetRequiredService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>>();
          var typedMsg = (__MESSAGE_TYPE__)msg;
          await receptor.HandleAsync(typedMsg);
          return null;  // Void receptor - no result to cascade
        } finally {
          if (scope is IAsyncDisposable asyncDisposable) {
            await asyncDisposable.DisposeAsync();
          } else {
            scope.Dispose();
          }
        }
      }

      return InvokeReceptor;
    }
    #endregion

    return null;
  }
}
