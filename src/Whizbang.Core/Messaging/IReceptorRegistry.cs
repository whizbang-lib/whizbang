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
/// <tests>tests/Whizbang.Core.Tests/Messaging/ReceptorRegistryRuntimeRegistrationTests.cs:Register_VoidReceptor_IsReturnedByGetReceptorsForAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ReceptorRegistryRuntimeRegistrationTests.cs:Unregister_VoidReceptor_RemovesFromRegistryAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/WhizbangReceptorRegistryQueryAdapterTests.cs:GeneratedRegistry_RuntimeRegister_AnswersHasRuntimeConsumerForAsync</tests>
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
  /// True when a RUNTIME-registered receptor consumes the given CLR type name (the storage form
  /// <c>TypeNameFormatter.Format</c> writes, or a bare FullName). The receive/inbox discard gates
  /// consult this alongside the source-generated tables — runtime-registered control-plane
  /// receptors (integrity checkpoints/manifests, rebuild commands) are otherwise invisible to
  /// them and their messages get silently discarded as "no consumer". Default: false (registries
  /// without runtime registration support change nothing).
  /// </summary>
  /// <param name="clrTypeName">The payload CLR type name as carried on the wire/rows.</param>
  /// <docs>internals/message-discard-policy</docs>
  bool HasRuntimeConsumerFor(string clrTypeName) => false;

  /// <summary>
  /// Unregisters a previously registered void receptor.
  /// </summary>
  /// <typeparam name="TMessage">The message type to unregister from.</typeparam>
  /// <param name="receptor">The receptor instance to remove.</param>
  /// <param name="stage">The lifecycle stage from which to remove the receptor.</param>
  /// <returns>True if the receptor was found and removed; false otherwise.</returns>
  bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage;

  /// <summary>
  /// Unregisters a previously registered response receptor.
  /// </summary>
  /// <typeparam name="TMessage">The message type to unregister from.</typeparam>
  /// <typeparam name="TResponse">The response type the receptor returns.</typeparam>
  /// <param name="receptor">The receptor instance to remove.</param>
  /// <param name="stage">The lifecycle stage from which to remove the receptor.</param>
  /// <returns>True if the receptor was found and removed; false otherwise.</returns>
  bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage;

  /// <summary>
  /// True if any runtime-registered receptor exists for the given message-type string at any
  /// lifecycle stage. Lets compile-time gates (the receive-boundary drop gate, lifecycle
  /// short-circuits) honor runtime registrations without requiring a <see cref="Type"/> at
  /// the call site — the gates only have an envelope-type string before deserialization.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Default implementation returns <c>false</c>: test doubles and stub registries that
  /// don't care about this path keep working. The source-generated
  /// <c>GeneratedReceptorRegistry</c> overrides with a real lookup that normalizes the
  /// runtime registry's <see cref="Type"/> keys to strings and compares.
  /// </para>
  /// <para>
  /// "Any stage" semantics — the drop gate wants to know whether <em>anything</em> on this
  /// service will react to the message type. Per-stage runtime checks live alongside the
  /// per-stage compile-time <c>HasReceptors</c> on <see cref="IReceptorRegistryQuery"/>.
  /// </para>
  /// </remarks>
  /// <param name="messageType">Message-type string, typically from
  /// <c>EnvelopeTypeNameHelper.ExtractInnerTypeName</c>. May be assembly-qualified or
  /// FullName-only; the implementation normalizes.</param>
  bool HasAnyRuntimeReceptors(string messageType) => false;
}
