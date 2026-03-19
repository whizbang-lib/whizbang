using System;
using System.Collections.Generic;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Registry that provides receptor information for AOT-compatible invocation.
/// Source-generated implementations provide compile-time lookup tables for all discovered receptors,
/// plus runtime registration support for integration test synchronization.
/// </summary>
/// <remarks>
/// <para>
/// The source generator categorizes receptors at compile time:
/// </para>
/// <list type="bullet">
/// <item><description>Receptors WITH [FireAt(X)] are registered at stage X only</description></item>
/// <item><description>Receptors WITHOUT [FireAt] are registered at LocalImmediateInline, PreOutboxInline, and PostInboxInline</description></item>
/// </list>
/// <para>
/// Runtime registration via <see cref="Register{TMessage}(IReceptor{TMessage}, LifecycleStage)"/> enables
/// integration tests to dynamically register receptors after hosts start. Runtime-registered receptors
/// are invoked through the same <see cref="IReceptorInvoker"/> pipeline as compile-time receptors,
/// ensuring full security context propagation and event cascading.
/// </para>
/// </remarks>
/// <docs>fundamentals/receptors/lifecycle-receptors</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ReceptorInvokerTests.cs</tests>
public interface IReceptorRegistry {
  /// <summary>
  /// Gets all receptors registered to handle the specified message type at the specified lifecycle stage.
  /// Returns compile-time entries concatenated with any runtime-registered entries.
  /// Returns empty collection if no receptors are registered for the type/stage combination.
  /// </summary>
  /// <param name="messageType">The message type to query.</param>
  /// <param name="stage">The lifecycle stage to query.</param>
  /// <returns>Collection of receptor information for AOT-compatible invocation.</returns>
  IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage);

  /// <summary>
  /// Registers a void receptor at a specific lifecycle stage for runtime invocation.
  /// Primarily used in integration tests for deterministic synchronization.
  /// </summary>
  /// <typeparam name="TMessage">The message type to register for.</typeparam>
  /// <param name="receptor">The receptor instance to invoke.</param>
  /// <param name="stage">The lifecycle stage at which to invoke the receptor.</param>
  /// <remarks>
  /// <para>
  /// <strong>AOT-compatible:</strong> Generic parameters are known at call-site compile time.
  /// Pattern matching and <c>GetType().Name</c> are AOT-safe operations.
  /// </para>
  /// <para>
  /// Runtime-registered receptors flow through <see cref="IReceptorInvoker"/> which provides
  /// full security context, event cascading, and trace correlation.
  /// </para>
  /// </remarks>
  /// <docs>operations/testing/lifecycle-synchronization</docs>
  void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage;

  /// <summary>
  /// Unregisters a previously registered void receptor.
  /// </summary>
  /// <typeparam name="TMessage">The message type to unregister from.</typeparam>
  /// <param name="receptor">The receptor instance to remove.</param>
  /// <param name="stage">The lifecycle stage from which to remove the receptor.</param>
  /// <returns>True if the receptor was found and removed; false otherwise.</returns>
  bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage;

  /// <summary>
  /// Registers a response receptor at a specific lifecycle stage for runtime invocation.
  /// Enables event cascading from runtime-registered receptors.
  /// </summary>
  /// <typeparam name="TMessage">The message type to register for.</typeparam>
  /// <typeparam name="TResponse">The response type the receptor returns.</typeparam>
  /// <param name="receptor">The receptor instance to invoke.</param>
  /// <param name="stage">The lifecycle stage at which to invoke the receptor.</param>
  /// <docs>operations/testing/lifecycle-synchronization</docs>
  void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage;

  /// <summary>
  /// Unregisters a previously registered response receptor.
  /// </summary>
  /// <typeparam name="TMessage">The message type to unregister from.</typeparam>
  /// <typeparam name="TResponse">The response type the receptor returns.</typeparam>
  /// <param name="receptor">The receptor instance to remove.</param>
  /// <param name="stage">The lifecycle stage from which to remove the receptor.</param>
  /// <returns>True if the receptor was found and removed; false otherwise.</returns>
  bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage;
}
