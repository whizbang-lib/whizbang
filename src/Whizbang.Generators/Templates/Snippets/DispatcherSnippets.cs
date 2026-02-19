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
      // Use a temporary scope to check registration
      using (var checkScope = _scopeFactory.CreateScope()) {
        var checkReceptor = checkScope.ServiceProvider.GetService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>>();
        if (checkReceptor == null) {
          return null;
        }
      }

      [System.Diagnostics.DebuggerStepThrough]
      async ValueTask<TResult> InvokeReceptor(object msg) {
        // Create scope for each invocation to properly handle scoped services
        var scope = _scopeFactory.CreateScope();
        try {
          var receptor = scope.ServiceProvider.GetRequiredService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>>();
          var typedMsg = (__MESSAGE_TYPE__)msg;
          var result = await receptor.HandleAsync(typedMsg);
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
  /// </summary>
  protected Func<object, Task>? UntypedPublishRoutingExample(Type eventType) {
    #region UNTYPED_PUBLISH_ROUTING_SNIPPET
    if (eventType == typeof(__MESSAGE_TYPE__)) {
      [System.Diagnostics.DebuggerStepThrough]
      async Task PublishToReceptorsUntyped(object evt) {
        // Create scope for each invocation to properly handle scoped services
        var scope = _scopeFactory.CreateScope();
        try {
          var receptors = scope.ServiceProvider.GetServices<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, object>>();
          var voidReceptors = scope.ServiceProvider.GetServices<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>>();
          var typedEvt = (__MESSAGE_TYPE__)evt;
          foreach (var receptor in receptors) {
            await receptor.HandleAsync(typedEvt);
          }
          foreach (var voidReceptor in voidReceptors) {
            await voidReceptor.HandleAsync(typedEvt);
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
  /// </summary>
  public void ReceptorRegistrationExample(IServiceCollection services) {
    #region RECEPTOR_REGISTRATION_SNIPPET
    services.AddTransient<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>, __RECEPTOR_CLASS__>();
    #endregion
  }

  /// <summary>
  /// Example method showing snippet structure for void receptor registration.
  /// </summary>
  public void VoidReceptorRegistrationExample(IServiceCollection services) {
    #region VOID_RECEPTOR_REGISTRATION_SNIPPET
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
      // Use a temporary scope to check registration
      using (var checkScope = _scopeFactory.CreateScope()) {
        var checkReceptor = checkScope.ServiceProvider.GetService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>>();
        if (checkReceptor == null) {
          return null;
        }
      }

      [System.Diagnostics.DebuggerStepThrough]
      async ValueTask InvokeReceptor(object msg) {
        // Create scope for each invocation to properly handle scoped services
        var scope = _scopeFactory.CreateScope();
        try {
          var receptor = scope.ServiceProvider.GetRequiredService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>>();
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
      // Use a temporary scope to check registration
      using (var checkScope = _scopeFactory.CreateScope()) {
        var checkReceptor = checkScope.ServiceProvider.GetService<__SYNC_RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>>();
        if (checkReceptor == null) {
          return null;
        }
      }

      [System.Diagnostics.DebuggerStepThrough]
      TResult InvokeReceptor(object msg) {
        // Create scope for each invocation to properly handle scoped services
        using var scope = _scopeFactory.CreateScope();
        var receptor = scope.ServiceProvider.GetRequiredService<__SYNC_RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>>();
        var typedMsg = (__MESSAGE_TYPE__)msg;
        return (TResult)(object)receptor.Handle(typedMsg)!;
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
      // Use a temporary scope to check registration
      using (var checkScope = _scopeFactory.CreateScope()) {
        var checkReceptor = checkScope.ServiceProvider.GetService<__SYNC_RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>>();
        if (checkReceptor == null) {
          return null;
        }
      }

      [System.Diagnostics.DebuggerStepThrough]
      void InvokeReceptor(object msg) {
        // Create scope for each invocation to properly handle scoped services
        using var scope = _scopeFactory.CreateScope();
        var receptor = scope.ServiceProvider.GetRequiredService<__SYNC_RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>>();
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
  /// </summary>
  public void SyncReceptorRegistrationExample(IServiceCollection services) {
    #region SYNC_RECEPTOR_REGISTRATION_SNIPPET
    services.AddTransient<__SYNC_RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>, __RECEPTOR_CLASS__>();
    #endregion
  }

  /// <summary>
  /// Example method showing snippet structure for void sync receptor registration.
  /// </summary>
  public void VoidSyncReceptorRegistrationExample(IServiceCollection services) {
    #region VOID_SYNC_RECEPTOR_REGISTRATION_SNIPPET
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
  /// </summary>
  protected async ValueTask LifecycleRoutingVoidExample(
      object message,
      LifecycleStage stage,
      CancellationToken cancellationToken) {
    #region LIFECYCLE_ROUTING_VOID_SNIPPET
    if (messageType == typeof(__MESSAGE_TYPE__) && stage == __LIFECYCLE_STAGE__) {
      var receptor = _serviceProvider.GetRequiredService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>>();
      await receptor.HandleAsync((__MESSAGE_TYPE__)message, cancellationToken);
    }
    #endregion
  }

  /// <summary>
  /// Example method showing snippet structure for lifecycle routing with response receptors.
  /// </summary>
  protected async ValueTask LifecycleRoutingResponseExample(
      object message,
      LifecycleStage stage,
      CancellationToken cancellationToken) {
    #region LIFECYCLE_ROUTING_RESPONSE_SNIPPET
    if (messageType == typeof(__MESSAGE_TYPE__) && stage == __LIFECYCLE_STAGE__) {
      var receptor = _serviceProvider.GetRequiredService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>>();
      await receptor.HandleAsync((__MESSAGE_TYPE__)message, cancellationToken);
    }
    #endregion
  }

  // Placeholder field to allow snippets to compile (used by lifecycle routing)
  protected IServiceProvider _serviceProvider => null!;

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
          InvokeAsync: async (sp, msg, ct) => {
            var receptor = sp.GetRequiredService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>>();
            return await receptor.HandleAsync((__MESSAGE_TYPE__)msg, ct);
          }
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
          InvokeAsync: async (sp, msg, ct) => {
            var receptor = sp.GetRequiredService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>>();
            await receptor.HandleAsync((__MESSAGE_TYPE__)msg, ct);
            return null;
          }
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
      // Use a temporary scope to check registration
      using (var checkScope = _scopeFactory.CreateScope()) {
        var checkReceptor = checkScope.ServiceProvider.GetService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>>();
        if (checkReceptor == null) {
          return null;
        }
      }

      [System.Diagnostics.DebuggerStepThrough]
      async ValueTask<object?> InvokeReceptor(object msg) {
        // Create scope for each invocation to properly handle scoped services
        var scope = _scopeFactory.CreateScope();
        try {
          var receptor = scope.ServiceProvider.GetRequiredService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__, __RESPONSE_TYPE__>>();
          var typedMsg = (__MESSAGE_TYPE__)msg;
          var result = await receptor.HandleAsync(typedMsg);
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
      // Use a temporary scope to check registration
      using (var checkScope = _scopeFactory.CreateScope()) {
        var checkReceptor = checkScope.ServiceProvider.GetService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>>();
        if (checkReceptor == null) {
          return null;
        }
      }

      [System.Diagnostics.DebuggerStepThrough]
      async ValueTask<object?> InvokeReceptor(object msg) {
        // Create scope for each invocation to properly handle scoped services
        var scope = _scopeFactory.CreateScope();
        try {
          var receptor = scope.ServiceProvider.GetRequiredService<__RECEPTOR_INTERFACE__<__MESSAGE_TYPE__>>();
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
