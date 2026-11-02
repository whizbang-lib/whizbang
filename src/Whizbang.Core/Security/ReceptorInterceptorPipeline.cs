// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Observability;
using Whizbang.Core.Security.Attributes;

namespace Whizbang.Core.Security;

/// <summary>
/// Runs every registered <see cref="IReceptorInterceptor"/> in sequence, short-circuiting
/// on the first denial and surfacing the strictest <see cref="DeniedAction"/> encountered.
/// The framework registers one as a singleton and runs it before every receptor invocation;
/// custom dispatch code can use it the same way.
/// </summary>
/// <remarks>
/// <para>
/// Composition: every registered interceptor must allow before the receptor runs. The
/// FIRST denial wins for short-circuit purposes — but if you need to know the strictest
/// applicable action across all interceptors (e.g., one says DropQuiet, another says
/// DeadLetter), use <see cref="EvaluateAllAsync"/> which runs all of them and returns
/// the strictest denial.
/// </para>
/// </remarks>
/// <docs>fundamentals/security/security#receptor-permission-gate</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/ReceptorInterceptorPipelineTests.cs</tests>
/// <remarks>
/// Creates a pipeline from the supplied interceptors. Empty input is a no-op pipeline
/// that always allows.
/// </remarks>
public sealed class ReceptorInterceptorPipeline(IEnumerable<IReceptorInterceptor> interceptors) {
  private readonly IReadOnlyList<IReceptorInterceptor> _interceptors = [.. interceptors];

  /// <summary>
  /// True when no interceptor is registered. The dispatcher and the receptor invoker read this once, when they are
  /// built, and skip the gate entirely for an empty pipeline, so a host without interceptors pays nothing per message.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Security/ReceptorInterceptorGateTests.cs:Active_NoInterceptorsRegistered_IsNullAsync</tests>
  public bool IsEmpty => _interceptors.Count == 0;

  /// <summary>
  /// Runs interceptors in order, short-circuiting on the first denial. Fastest path —
  /// use this when you just need to know whether to invoke and what to do on denial.
  /// </summary>
  public async ValueTask<InterceptorResult> EvaluateFirstDenialAsync(
      Type receptorType,
      IMessageEnvelope envelope,
      IScopeContext? context,
      CancellationToken cancellationToken = default) {
    foreach (var interceptor in _interceptors) {
      var result = await interceptor.CanInvokeAsync(receptorType, envelope, context, cancellationToken).ConfigureAwait(false);
      if (!result.Allow) {
        return result;
      }
    }
    return InterceptorResult.Allowed;
  }

  /// <summary>
  /// Runs every interceptor and returns the strictest denial (DeadLetter > Quarantine >
  /// Throw > DropQuiet) encountered. Use this when peer interceptors can disagree and you
  /// need the safest action — e.g., one interceptor would DropQuiet but another insists
  /// on DeadLetter for audit, the latter must win.
  /// </summary>
  public async ValueTask<InterceptorResult> EvaluateAllAsync(
      Type receptorType,
      IMessageEnvelope envelope,
      IScopeContext? context,
      CancellationToken cancellationToken = default) {
    InterceptorResult? worstDenial = null;
    foreach (var interceptor in _interceptors) {
      var result = await interceptor.CanInvokeAsync(receptorType, envelope, context, cancellationToken).ConfigureAwait(false);
      if (result.Allow) {
        continue;
      }
      if (worstDenial is null || _strictness(result.OnDenied) > _strictness(worstDenial.OnDenied)) {
        worstDenial = result;
      }
    }
    return worstDenial ?? InterceptorResult.Allowed;
  }

  private static int _strictness(DeniedAction action) => action switch {
    DeniedAction.DeadLetter => 4,
    DeniedAction.Quarantine => 3,
    DeniedAction.Throw => 2,
    DeniedAction.DropQuiet => 1,
    _ => 0,
  };
}
