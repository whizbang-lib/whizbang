using System;
using System.Collections.Generic;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Registry for dynamically registering and unregistering lifecycle receptors at runtime.
/// Primarily used in integration tests to register completion handlers after hosts start.
/// </summary>
/// <remarks>
/// <para>
/// While most receptors are discovered at compile-time via the <see cref="FireAtAttribute"/>,
/// the runtime registry allows tests to dynamically register receptors for synchronization.
/// </para>
/// <para>
/// <strong>Test Pattern:</strong> Wait for perspective processing to complete:
/// </para>
/// <code>
/// // In test code
/// var completionSource = new TaskCompletionSource&lt;bool&gt;();
/// var receptor = new PerspectiveCompletionReceptor&lt;ProductCreatedEvent&gt;(completionSource);
///
/// var registry = host.Services.GetRequiredService&lt;ILifecycleReceptorRegistry&gt;();
/// registry.Register(receptor);
///
/// try {
///   // Dispatch command that will eventually trigger perspective processing
///   await dispatcher.SendAsync(createProductCommand);
///
///   // Wait for perspective processing to complete (deterministic, no polling!)
///   await completionSource.Task.WaitAsync(TimeSpan.FromSeconds(15));
///
///   // Now verify perspective data
///   var product = await productLens.GetByIdAsync(createProductCommand.ProductId);
///   Assert.That(product).IsNotNull();
/// } finally {
///   registry.Unregister(receptor);
/// }
/// </code>
/// </remarks>
/// <docs>testing/lifecycle-synchronization</docs>
public interface ILifecycleReceptorRegistry {
  /// <summary>
  /// Registers a receptor to be invoked at a specific lifecycle stage for a message type.
  /// </summary>
  /// <typeparam name="TMessage">The message type to register for.</typeparam>
  /// <param name="receptor">The receptor instance to invoke.</param>
  /// <param name="stage">The lifecycle stage at which to invoke the receptor.</param>
  void Register<TMessage>(object receptor, LifecycleStage stage) where TMessage : IMessage;

  /// <summary>
  /// Unregisters a previously registered receptor.
  /// </summary>
  /// <typeparam name="TMessage">The message type to unregister from.</typeparam>
  /// <param name="receptor">The receptor instance to remove.</param>
  /// <param name="stage">The lifecycle stage from which to remove the receptor.</param>
  /// <returns>True if the receptor was found and removed; false otherwise.</returns>
  bool Unregister<TMessage>(object receptor, LifecycleStage stage) where TMessage : IMessage;

  /// <summary>
  /// Gets all registered receptors for a specific message type and lifecycle stage.
  /// </summary>
  /// <param name="messageType">The message type to query.</param>
  /// <param name="stage">The lifecycle stage to query.</param>
  /// <returns>List of registered receptor instances (empty if none registered).</returns>
  IReadOnlyList<object> GetReceptors(Type messageType, LifecycleStage stage);
}
