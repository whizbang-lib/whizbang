using System.Reflection;
using Whizbang.Core.Observability;
using Whizbang.Core.Security.Attributes;

namespace Whizbang.Core.Security;

/// <summary>
/// Default <see cref="IReceptorInterceptor"/> that honors <see cref="RequirePermissionAttribute"/>
/// declarations on receptor classes. Each attribute on the receptor type contributes a
/// permission requirement; missing any required permission denies the invocation with the
/// attribute's <see cref="RequirePermissionAttribute.OnDenied"/> action.
/// </summary>
/// <remarks>
/// <para>
/// Multiple <see cref="RequirePermissionAttribute"/> declarations on the same receptor
/// class compose with AND semantics — every required permission must be satisfied. If
/// multiple attributes have different <see cref="RequirePermissionAttribute.OnDenied"/>
/// values, the strictest action (DeadLetter &gt; Quarantine &gt; DropQuiet) wins so that
/// a "must dead-letter" requirement isn't silently downgraded by a peer attribute that
/// would have dropped quiet.
/// </para>
/// </remarks>
/// <docs>fundamentals/security/security#receptor-permission-gate</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/ReceptorInterceptorTests.cs</tests>
public sealed class DefaultRequirePermissionInterceptor : IReceptorInterceptor {
  /// <inheritdoc />
  public ValueTask<InterceptorResult> CanInvokeAsync(
      Type receptorType,
      IMessageEnvelope envelope,
      IScopeContext? context,
      CancellationToken cancellationToken = default) {
    var attrs = receptorType.GetCustomAttributes<RequirePermissionAttribute>(inherit: true).ToList();
    if (attrs.Count == 0) {
      return new ValueTask<InterceptorResult>(InterceptorResult.Allowed);
    }

    DeniedAction? failureAction = null;
    string? failedPermission = null;

    foreach (var attr in attrs) {
      if (context is null || !context.HasPermission(attr.Permission)) {
        var attrAction = attr.OnDenied;
        if (failureAction is null || _strictness(attrAction) > _strictness(failureAction.Value)) {
          failureAction = attrAction;
          failedPermission = attr.Permission.Value;
        }
      }
    }

    if (failureAction is null) {
      return new ValueTask<InterceptorResult>(InterceptorResult.Allowed);
    }

    var reason = $"Missing required permission: {failedPermission}";
    return new ValueTask<InterceptorResult>(InterceptorResult.Deny(failureAction.Value, reason));
  }

  /// <summary>Strictness ordering: higher = more conservative when actions disagree.</summary>
  private static int _strictness(DeniedAction action) => action switch {
    DeniedAction.DeadLetter => 4,
    DeniedAction.Quarantine => 3,
    DeniedAction.Throw => 2,
    DeniedAction.DropQuiet => 1,
    _ => 0,
  };
}
