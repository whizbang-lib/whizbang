// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Observability;
using Whizbang.Core.Security.Attributes;

namespace Whizbang.Core.Security;

/// <summary>
/// Pre-invocation gate for receptors. Implementations decide whether a given receptor type
/// can be invoked for a given message envelope under a given scope context. Multiple
/// interceptors compose — every registered interceptor must allow before the receptor
/// runs. The first denial short-circuits and dictates the <see cref="DeniedAction"/>.
/// </summary>
/// <remarks>
/// <para>
/// The default <see cref="DefaultRequirePermissionInterceptor"/> consults
/// <see cref="RequirePermissionAttribute"/> on the receptor class. Custom interceptors
/// can layer additional cross-cutting policy (rate limiting, IP allowlists, time windows).
/// </para>
/// <para>
/// Every registered interceptor runs immediately before every receptor invocation: local dispatch
/// (<c>SendAsync</c>, <c>LocalInvokeAsync</c>, <c>PublishAsync</c>), the receptors of a message a receptor
/// returned (a cascade), receptors registered at runtime, raw receptors (<c>IRawReceptor</c>), and every
/// lifecycle stage, including inbox delivery and the perspective worker's lifecycle receptors. The interceptor
/// is shown the scope context the receptor would read through <see cref="IScopeContextAccessor"/>. Register an
/// implementation as a singleton (<c>services.AddSingleton&lt;IReceptorInterceptor, MyInterceptor&gt;()</c>);
/// with none registered, no invocation path does any interceptor work.
/// </para>
/// <para>
/// A denied receptor does not run, so nothing it would have returned is appended or cascaded. What else
/// happens follows <see cref="DeniedAction"/>: <see cref="DeniedAction.DropQuiet"/> skips the receptor with a
/// warning; on a local dispatch every other action throws
/// <see cref="Exceptions.ReceptorInvocationDeniedException"/> to the caller; on inbox delivery
/// <see cref="DeniedAction.DeadLetter"/> and <see cref="DeniedAction.Quarantine"/> move the message to the
/// dead-letter store without a retry and <see cref="DeniedAction.Throw"/> fails the attempt for the retry policy
/// (the inbox dispatch worker and the Service Bus consumer ask before they settle the message; a raw receptor's
/// denial is settled at the broker; a denied control-class comparison is dropped, as a failed one is).
/// An interceptor that applies to a synchronous receptor (<c>ISyncReceptor</c>) must complete synchronously.
/// </para>
/// </remarks>
/// <docs>fundamentals/security/security#receptor-permission-gate</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/ReceptorInterceptorTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Security/ReceptorInterceptorPipelineTests.cs:EvaluateFirstDenial_FirstDenies_ShortCircuitsAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Security/ReceptorInterceptorPipelineTests.cs:EvaluateAll_AllInterceptorsRun_EvenWhenFirstDeniesAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Security/ReceptorInterceptorPipelineTests.cs:EmptyPipeline_AlwaysAllowsAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Security/ReceptorInterceptorDispatchTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ReceptorInvokerInterceptorTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/InboxDispatchWorkerReceptorDenialTests.cs</tests>
public interface IReceptorInterceptor {
  /// <summary>
  /// Decide whether the given receptor type may be invoked for the supplied envelope and
  /// scope context. Returns an <see cref="InterceptorResult"/> describing the outcome —
  /// allowed, or denied with an associated <see cref="DeniedAction"/>.
  /// </summary>
  ValueTask<InterceptorResult> CanInvokeAsync(
      Type receptorType,
      IMessageEnvelope envelope,
      IScopeContext? context,
      CancellationToken cancellationToken = default);
}

/// <summary>
/// Outcome of an <see cref="IReceptorInterceptor.CanInvokeAsync"/> call.
/// </summary>
/// <param name="Allow">True to proceed with invocation; false to deny.</param>
/// <param name="OnDenied">
/// When <see cref="Allow"/> is false, the action the invocation pipeline should take.
/// Ignored when <see cref="Allow"/> is true.
/// </param>
/// <param name="Reason">
/// Optional human-readable reason for the decision. Used for diagnostics/logging only;
/// must not influence control flow.
/// </param>
public sealed record InterceptorResult(
    bool Allow,
    DeniedAction OnDenied = DeniedAction.DeadLetter,
    string? Reason = null) {
  /// <summary>Convenience: a permissive result.</summary>
  public static InterceptorResult Allowed { get; } = new(Allow: true);

  /// <summary>Convenience: build a denial with the given action.</summary>
  public static InterceptorResult Deny(DeniedAction action, string? reason = null) =>
    new(Allow: false, OnDenied: action, Reason: reason);
}
