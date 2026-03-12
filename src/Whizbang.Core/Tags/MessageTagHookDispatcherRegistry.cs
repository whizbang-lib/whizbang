using System.Text.Json;
using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;
using Whizbang.Core.Registry;
using Whizbang.Core.Security;

namespace Whizbang.Core.Tags;

/// <summary>
/// Registry for custom MessageTagAttribute dispatchers.
/// Aggregates dispatchers from all loaded assemblies.
/// </summary>
/// <remarks>
/// <para>
/// The MessageTagDiscoveryGenerator generates dispatchers for custom attribute types.
/// These are registered via ModuleInitializer before application code runs.
/// </para>
/// <para>
/// The <see cref="MessageTagProcessor"/> uses this registry to dispatch hooks
/// for custom attribute types that aren't built-in Whizbang types.
/// </para>
/// </remarks>
/// <docs>core-concepts/message-tags#dispatcher-registry</docs>
public static class MessageTagHookDispatcherRegistry {
  /// <summary>
  /// Gets the number of registered dispatchers.
  /// </summary>
  public static int Count => AssemblyRegistry<IMessageTagHookDispatcher>.Count;

  /// <summary>
  /// Registers a dispatcher with optional priority.
  /// Higher priority dispatchers are tried first.
  /// </summary>
  /// <param name="dispatcher">The dispatcher to register.</param>
  /// <param name="priority">Priority (default 100). Higher values are tried first.</param>
  public static void Register(IMessageTagHookDispatcher dispatcher, int priority = 100) {
    AssemblyRegistry<IMessageTagHookDispatcher>.Register(dispatcher, priority);
  }

  /// <summary>
  /// Attempts to create a typed context for the given attribute type.
  /// Tries each registered dispatcher in priority order.
  /// </summary>
  public static object? TryCreateContext(
      Type attributeType,
      MessageTagAttribute attribute,
      object message,
      Type messageType,
      JsonElement payload,
      IScopeContext? scope,
      LifecycleStage stage) {
    foreach (var dispatcher in AssemblyRegistry<IMessageTagHookDispatcher>.GetOrderedContributions()) {
      var context = dispatcher.TryCreateContext(attributeType, attribute, message, messageType, payload, scope, stage);
      if (context is not null) {
        return context;
      }
    }
    return null;
  }

  /// <summary>
  /// Attempts to dispatch a hook for the given attribute type.
  /// Tries each registered dispatcher in priority order.
  /// </summary>
  public static async ValueTask<JsonElement?> TryDispatchAsync(
      object hookInstance,
      object context,
      Type attributeType,
      CancellationToken ct) {
    foreach (var dispatcher in AssemblyRegistry<IMessageTagHookDispatcher>.GetOrderedContributions()) {
      var result = await dispatcher.TryDispatchAsync(hookInstance, context, attributeType, ct);
      if (result.HasValue) {
        return result;
      }
    }
    return null;
  }
}
