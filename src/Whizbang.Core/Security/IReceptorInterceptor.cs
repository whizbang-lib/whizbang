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
/// Wiring into the receptor invocation pipeline is shipped separately — until that lands,
/// consumers can call <see cref="DefaultRequirePermissionInterceptor.CanInvokeAsync"/>
/// directly from custom dispatch code.
/// </para>
/// </remarks>
/// <docs>fundamentals/security/security#receptor-permission-gate</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/ReceptorInterceptorTests.cs</tests>
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
