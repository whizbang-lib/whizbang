using System;
using System.Collections.Generic;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Registry that provides receptor information for AOT-compatible invocation.
/// Source-generated implementations provide compile-time lookup tables for all discovered receptors.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="ILifecycleReceptorRegistry"/> which is for runtime-registered lifecycle receptors,
/// this registry provides compile-time discovered receptor metadata used by <see cref="IReceptorInvoker"/>.
/// </para>
/// <para>
/// The source generator categorizes receptors at compile time:
/// </para>
/// <list type="bullet">
/// <item><description>Receptors WITH [FireAt(X)] are registered at stage X only</description></item>
/// <item><description>Receptors WITHOUT [FireAt] are registered at LocalImmediateInline, PreOutboxInline, and PostInboxInline</description></item>
/// </list>
/// <para>
/// This means no runtime logic is needed to determine when a receptor fires - it's all compile-time categorization.
/// </para>
/// </remarks>
/// <docs>core-concepts/lifecycle-receptors</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ReceptorInvokerTests.cs</tests>
public interface IReceptorRegistry {
  /// <summary>
  /// Gets all receptors registered to handle the specified message type at the specified lifecycle stage.
  /// Returns empty collection if no receptors are registered for the type/stage combination.
  /// </summary>
  /// <param name="messageType">The message type to query.</param>
  /// <param name="stage">The lifecycle stage to query.</param>
  /// <returns>Collection of receptor information for AOT-compatible invocation.</returns>
  IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage);
}
