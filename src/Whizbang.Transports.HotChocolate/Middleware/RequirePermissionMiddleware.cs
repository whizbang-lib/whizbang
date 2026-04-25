using HotChocolate;
using HotChocolate.Resolvers;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Attributes;

namespace Whizbang.Transports.HotChocolate.Middleware;

/// <summary>
/// HotChocolate field middleware that enforces <see cref="RequirePermissionAttribute"/>
/// declarations at GraphQL resolution time. Reads the current request's
/// <see cref="IScopeContext"/> and verifies every required permission is present before
/// invoking the resolver. Failure throws a <see cref="GraphQLException"/> with extension
/// code <c>AUTH_NOT_AUTHORIZED</c>, which clients can pattern-match consistently.
/// </summary>
/// <remarks>
/// <para>
/// Pair with the <c>[UseRequirePermission]</c> attribute or call
/// <c>descriptor.UseRequirePermission()</c> in fluent schema configuration. The
/// middleware itself is operation-aware — when paired with <see cref="ScopeOperation.Read"/>
/// or <see cref="ScopeOperation.Write"/> it inspects the operation type on the resolver
/// context and skips the check when it doesn't apply, so a single attribute can serve
/// queries and mutations correctly.
/// </para>
/// </remarks>
/// <docs>apis/graphql/authorization#require-permission</docs>
/// <tests>tests/Whizbang.Transports.HotChocolate.Tests/Unit/RequirePermissionMiddlewareTests.cs</tests>
public static class RequirePermissionMiddleware {
  /// <summary>
  /// Creates field middleware that enforces every supplied <see cref="RequirePermissionAttribute"/>.
  /// Empty input is a no-op middleware.
  /// </summary>
  public static FieldMiddleware Create(IReadOnlyList<RequirePermissionAttribute> required) {
    if (required.Count == 0) {
      return next => async ctx => await next(ctx);
    }

    return next => async ctx => {
      var operationKind = ctx.Operation.Type switch {
        global::HotChocolate.Language.OperationType.Mutation => ScopeOperation.Write,
        _ => ScopeOperation.Read,
      };

      var scope = ctx.Services.GetService<IScopeContext>();
      var failure = Evaluate(scope, required, operationKind);
      if (failure is not null) {
        throw new GraphQLException(failure);
      }

      await next(ctx);
    };
  }

  /// <summary>
  /// Pure evaluation: given a scope context, a set of required permissions, and the
  /// actual operation kind, returns a <see cref="HotChocolate.IError"/> describing the first
  /// missing permission, or <c>null</c> if every applicable permission is satisfied.
  /// Extracted from <see cref="Create"/> to make the policy directly unit-testable
  /// without standing up a HotChocolate executor.
  /// </summary>
  public static IError? Evaluate(
      IScopeContext? scope,
      IReadOnlyList<RequirePermissionAttribute> required,
      ScopeOperation actualOperation) {
    foreach (var attr in required) {
      if (!_appliesTo(attr.Operation, actualOperation)) {
        continue;
      }
      if (scope is null || !scope.HasPermission(attr.Permission)) {
        return ErrorBuilder.New()
          .SetMessage($"Missing required permission: {attr.Permission.Value}")
          .SetCode("AUTH_NOT_AUTHORIZED")
          .SetExtension("permission", attr.Permission.Value)
          .SetExtension("operation", attr.Operation.ToString())
          .Build();
      }
    }
    return null;
  }

  private static bool _appliesTo(ScopeOperation attributeOp, ScopeOperation actualOp) =>
    attributeOp == ScopeOperation.Any || attributeOp == actualOp;
}
