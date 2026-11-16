// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Threading;
using System.Threading.Tasks;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;


/// <summary>
/// Invokes receptors based on lifecycle stage and [FireAt] attributes.
/// Handles both explicit stage targeting and default stage behavior.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Invocation Rules:</strong>
/// </para>
/// <list type="bullet">
/// <item><description>Receptors with [FireAt(stage)] are invoked at that stage only</description></item>
/// <item><description>Receptors without [FireAt] are invoked at default stages</description></item>
/// </list>
/// <para>
/// <strong>Default Stages (when no [FireAt] attribute):</strong>
/// </para>
/// <list type="bullet">
/// <item><description><see cref="LifecycleStage.LocalImmediateInline"/> - Local dispatch (mediator pattern)</description></item>
/// <item><description><see cref="LifecycleStage.PreOutboxInline"/> - Distributed path sender side</description></item>
/// <item><description><see cref="LifecycleStage.PostInboxInline"/> - Distributed path receiver side</description></item>
/// </list>
/// <para>
/// <strong>Path Exclusivity:</strong>
/// Local and distributed paths are mutually exclusive. A message goes through one path only:
/// </para>
/// <list type="bullet">
/// <item><description>Local path: LocalImmediate only (no persistence)</description></item>
/// <item><description>Distributed path: PreOutbox (sender) + PostInbox (receiver)</description></item>
/// </list>
/// </remarks>
/// <docs>fundamentals/receptors/lifecycle-receptors</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ReceptorInvokerTests.cs:InvokeAsync_ReceptorAtPostInboxInline_ShouldInvokeAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ReceptorInvokerTests.cs:InvokeAsync_ReceptorReturnsEvent_ShouldCascadeEventAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ReceptorInvokerServiceRegistrationTests.cs:ReceptorInvoker_RegisteredAsScoped_HasCorrectLifetimeAsync</tests>
public interface IReceptorInvoker {
  /// <summary>
  /// Invokes all receptors for the message at the specified lifecycle stage.
  /// </summary>
  /// <param name="envelope">The message envelope containing the payload and metadata (hops, security context).</param>
  /// <param name="stage">The lifecycle stage at which to invoke receptors.</param>
  /// <param name="context">Optional context providing metadata about the invocation (stream ID, message source, etc.).</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Task that completes when all receptors have been invoked.</returns>
  /// <remarks>
  /// The invoker extracts the payload from the envelope, establishes security context
  /// (if IMessageSecurityContextProvider is configured), then invokes all registered receptors.
  /// </remarks>
  ValueTask InvokeAsync(
      IMessageEnvelope envelope,
      LifecycleStage stage,
      ILifecycleContext? context = null,
      CancellationToken cancellationToken = default);

  /// <summary>
  /// Asks the registered receptor interceptors, ahead of the stages, whether the receptors this message would run at
  /// <paramref name="stages"/> may run. Returns the first denial that must not pass silently (dead-letter, quarantine
  /// or throw), or an allowed result. A <see cref="Security.Attributes.DeniedAction.DropQuiet"/> denial is not
  /// reported here: it skips that one receptor when it would run.
  /// </summary>
  /// <remarks>
  /// The inbox worker calls this before it completes a row, so a denied message can be dead-lettered or retried
  /// instead of completed. Each receptor is still gated again immediately before it runs. The default implementation,
  /// for invokers that do not gate receptors, allows everything.
  /// </remarks>
  /// <param name="envelope">The message envelope.</param>
  /// <param name="stages">The stages whose receptors are checked.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The first loud denial, or <see cref="Security.InterceptorResult.Allowed"/>.</returns>
  /// <docs>fundamentals/security/security#receptor-permission-gate</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ReceptorInvokerInterceptorTests.cs</tests>
  ValueTask<Security.InterceptorResult> EvaluateAdmissionAsync(
      IMessageEnvelope envelope,
      IReadOnlyList<LifecycleStage> stages,
      CancellationToken cancellationToken = default) =>
    new(Security.InterceptorResult.Allowed);

  /// <summary>
  /// Invokes every receptor for the message at the stage, even when one of them throws: each receptor that fails is
  /// reported to <paramref name="failures"/> and the next one still runs (#2310).
  /// </summary>
  /// <remarks>
  /// <para>
  /// The outbox flush calls this for the Distribute stages, where a batch holds messages from many streams and a
  /// receptor observes a write that happens either way. One receptor's exception there must not keep the other
  /// receptors of the same message from running.
  /// </para>
  /// <para>
  /// A failure outside any receptor (the payload cannot be read, the security context cannot be established) is
  /// thrown, as it is from <see cref="InvokeAsync"/>; the caller isolates it per message. The default implementation,
  /// for an invoker that does not isolate its receptors, is <see cref="InvokeAsync"/>: its first failure is thrown and
  /// the caller still isolates it per message.
  /// </para>
  /// </remarks>
  /// <param name="envelope">The message envelope.</param>
  /// <param name="stage">The lifecycle stage at which to invoke receptors.</param>
  /// <param name="context">Optional context providing metadata about the invocation.</param>
  /// <param name="failures">Receives each receptor that throws, with its exception.</param>
  /// <param name="cancellationToken">Cancellation token. A cancellation is thrown, never reported as a failure.</param>
  /// <docs>fundamentals/lifecycle/lifecycle-stages#distribute-failures</docs>
  /// <tests>tests/Whizbang.Core.Tests/Messaging/ReceptorInvokerFailureIsolationTests.cs</tests>
  ValueTask InvokeEveryReceptorAsync(
      IMessageEnvelope envelope,
      LifecycleStage stage,
      ILifecycleContext? context,
      IReceptorFailureSink failures,
      CancellationToken cancellationToken = default) =>
    InvokeAsync(envelope, stage, context, cancellationToken);
}
