using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Attributes;
using Whizbang.Transports.HotChocolate.Middleware;

namespace Whizbang.Transports.HotChocolate.Tests.Unit;

/// <summary>
/// Tests for <see cref="RequirePermissionMiddleware.Evaluate"/> — the pure policy
/// extracted from the HotChocolate field-middleware delegate. Exercising it directly
/// keeps the test surface small and avoids spinning up a HotChocolate executor.
/// </summary>
/// <tests>RequirePermissionMiddleware,UseRequirePermissionAttribute</tests>
public class RequirePermissionMiddlewareTests {
  private static ImmutableScopeContext _scopeWith(params string[] permissions) {
    var perms = new HashSet<Permission>(permissions.Select(p => new Permission(p)));
    return new ImmutableScopeContext(new SecurityExtraction {
      Scope = new Whizbang.Core.Lenses.PerspectiveScope(),
      Roles = new HashSet<string>(),
      Permissions = perms,
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "Test",
    }, shouldPropagate: true);
  }

  // ===== No requirements / empty input =====

  [Test]
  public async Task Evaluate_NoRequirements_ReturnsNullAsync() {
    var failure = RequirePermissionMiddleware.Evaluate(
      _scopeWith(), [], ScopeOperation.Read);
    await Assert.That(failure).IsNull();
  }

  [Test]
  public async Task Evaluate_NullScope_WithRequirement_FailsAsync() {
    var failure = RequirePermissionMiddleware.Evaluate(
      null, [new RequirePermissionAttribute("foo")], ScopeOperation.Read);
    await Assert.That(failure).IsNotNull();
    await Assert.That(failure!.Code).IsEqualTo("AUTH_NOT_AUTHORIZED");
  }

  // ===== Single requirement, Any operation =====

  [Test]
  public async Task Evaluate_AnyOperation_PermissionPresent_ReturnsNullAsync() {
    var failure = RequirePermissionMiddleware.Evaluate(
      _scopeWith("foo"), [new RequirePermissionAttribute("foo")], ScopeOperation.Read);
    await Assert.That(failure).IsNull();
  }

  [Test]
  public async Task Evaluate_AnyOperation_PermissionMissing_ReturnsErrorAsync() {
    var failure = RequirePermissionMiddleware.Evaluate(
      _scopeWith(), [new RequirePermissionAttribute("foo")], ScopeOperation.Read);
    await Assert.That(failure).IsNotNull();
    var permission = failure!.Extensions?["permission"];
    await Assert.That(permission).IsEqualTo("foo");
  }

  // ===== Read vs Write operation gating =====

  [Test]
  public async Task Evaluate_ReadAttribute_OnQuery_IsCheckedAsync() {
    var failure = RequirePermissionMiddleware.Evaluate(
      _scopeWith(),
      [new RequirePermissionAttribute("foo") { Operation = ScopeOperation.Read }],
      ScopeOperation.Read);
    await Assert.That(failure).IsNotNull();
  }

  [Test]
  public async Task Evaluate_ReadAttribute_OnMutation_IsSkippedAsync() {
    // Read-only attribute on a Write operation: skipped, no enforcement.
    var failure = RequirePermissionMiddleware.Evaluate(
      _scopeWith(),
      [new RequirePermissionAttribute("foo") { Operation = ScopeOperation.Read }],
      ScopeOperation.Write);
    await Assert.That(failure).IsNull();
  }

  [Test]
  public async Task Evaluate_WriteAttribute_OnQuery_IsSkippedAsync() {
    var failure = RequirePermissionMiddleware.Evaluate(
      _scopeWith(),
      [new RequirePermissionAttribute("foo") { Operation = ScopeOperation.Write }],
      ScopeOperation.Read);
    await Assert.That(failure).IsNull();
  }

  [Test]
  public async Task Evaluate_WriteAttribute_OnMutation_IsCheckedAsync() {
    var failure = RequirePermissionMiddleware.Evaluate(
      _scopeWith(),
      [new RequirePermissionAttribute("foo") { Operation = ScopeOperation.Write }],
      ScopeOperation.Write);
    await Assert.That(failure).IsNotNull();
  }

  // ===== Multiple requirements (AND semantics) =====

  [Test]
  public async Task Evaluate_MultipleAttributes_AllSatisfied_ReturnsNullAsync() {
    var failure = RequirePermissionMiddleware.Evaluate(
      _scopeWith("foo", "bar"),
      [new RequirePermissionAttribute("foo"), new RequirePermissionAttribute("bar")],
      ScopeOperation.Read);
    await Assert.That(failure).IsNull();
  }

  [Test]
  public async Task Evaluate_MultipleAttributes_OneMissing_ReturnsFirstFailureAsync() {
    var failure = RequirePermissionMiddleware.Evaluate(
      _scopeWith("foo"),
      [new RequirePermissionAttribute("foo"), new RequirePermissionAttribute("bar")],
      ScopeOperation.Read);
    await Assert.That(failure).IsNotNull();
    var permission = failure!.Extensions?["permission"];
    await Assert.That(permission).IsEqualTo("bar");
  }

  // ===== Mixed operation attributes on the same field =====

  [Test]
  public async Task Evaluate_ReadAndWriteAttributes_OnMutation_OnlyWriteCheckedAsync() {
    // Class-level Read + method-level Write, evaluating against a Mutation:
    // Read is skipped, Write is enforced.
    var attrs = new[] {
      new RequirePermissionAttribute("foo") { Operation = ScopeOperation.Read },
      new RequirePermissionAttribute("foo:write") { Operation = ScopeOperation.Write }
    };
    var failure = RequirePermissionMiddleware.Evaluate(
      _scopeWith("foo:write"),
      attrs, ScopeOperation.Write);
    await Assert.That(failure).IsNull();
  }

  [Test]
  public async Task Evaluate_ReadAndWriteAttributes_OnQuery_OnlyReadCheckedAsync() {
    var attrs = new[] {
      new RequirePermissionAttribute("foo:read") { Operation = ScopeOperation.Read },
      new RequirePermissionAttribute("foo:write") { Operation = ScopeOperation.Write }
    };
    var failure = RequirePermissionMiddleware.Evaluate(
      _scopeWith("foo:read"),
      attrs, ScopeOperation.Read);
    await Assert.That(failure).IsNull();
  }

  // ===== Error shape =====

  [Test]
  public async Task Evaluate_FailureCarriesPermissionAndOperationExtensionsAsync() {
    var failure = RequirePermissionMiddleware.Evaluate(
      _scopeWith(),
      [new RequirePermissionAttribute("orders:write") { Operation = ScopeOperation.Write }],
      ScopeOperation.Write);
    await Assert.That(failure).IsNotNull();
    var code = failure!.Code;
    var permission = failure.Extensions?["permission"];
    var operation = failure.Extensions?["operation"];
    await Assert.That(code).IsEqualTo("AUTH_NOT_AUTHORIZED");
    await Assert.That(permission).IsEqualTo("orders:write");
    await Assert.That(operation).IsEqualTo("Write");
  }

  // ===== Field middleware end-to-end (Create + [UseRequirePermission]) =====
  //
  // These execute real GraphQL operations through a HotChocolate executor so the
  // FieldMiddleware delegates that Create() returns — and the attribute's
  // OnConfigure/_collectAttributes wiring — are exercised, not just the extracted
  // Evaluate() policy above.

  private static ServiceProvider _buildGuardedServer(IScopeContext? scope) {
    var services = new ServiceCollection();
    if (scope is not null) {
      services.AddSingleton(scope);
    }
    services
      .AddGraphQLServer()
      .AddQueryType<GuardedQuery>()
      .AddMutationType<GuardedMutation>();
    return services.BuildServiceProvider();
  }

  private static ServiceProvider _buildClassGuardedServer(IScopeContext? scope) {
    var services = new ServiceCollection();
    if (scope is not null) {
      services.AddSingleton(scope);
    }
    services
      .AddGraphQLServer()
      .AddQueryType<ClassGuardedQuery>();
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task FieldMiddleware_Query_MissingPermission_ReturnsAuthNotAuthorizedAsync() {
    await using var provider = _buildGuardedServer(_scopeWith("unrelated:perm"));
    var executor = await provider.GetRequestExecutorAsync();

    var result = await executor.ExecuteAsync("{ guardedValue }");

    var json = result.ToJson();
    await Assert.That(json).Contains("AUTH_NOT_AUTHORIZED")
      .Because("The field middleware must convert the Evaluate() failure into a GraphQL error with the stable AUTH_NOT_AUTHORIZED code clients pattern-match on.");
    await Assert.That(json).DoesNotContain("guarded-value")
      .Because("The resolver must never run when the permission check fails.");
  }

  [Test]
  public async Task FieldMiddleware_Query_PermissionPresent_ResolvesFieldAsync() {
    await using var provider = _buildGuardedServer(_scopeWith("doc:read"));
    var executor = await provider.GetRequestExecutorAsync();

    var result = await executor.ExecuteAsync("{ guardedValue }");

    var json = result.ToJson();
    await Assert.That(json).DoesNotContain("errors");
    await Assert.That(json).Contains("guarded-value");
  }

  [Test]
  public async Task FieldMiddleware_NoScopeContextRegistered_FailsClosedAsync() {
    // No IScopeContext in DI at all: ctx.Services.GetService returns null and the
    // middleware must fail closed, exactly like a scope with no permissions.
    await using var provider = _buildGuardedServer(scope: null);
    var executor = await provider.GetRequestExecutorAsync();

    var result = await executor.ExecuteAsync("{ guardedValue }");

    var json = result.ToJson();
    await Assert.That(json).Contains("AUTH_NOT_AUTHORIZED");
  }

  [Test]
  public async Task FieldMiddleware_AttributeWithoutRequirements_PassesThroughAsync() {
    // [UseRequirePermission] with no [RequirePermission] attributes anywhere:
    // Create([]) returns the no-op pass-through delegate — the field resolves even
    // with no scope context registered.
    await using var provider = _buildGuardedServer(scope: null);
    var executor = await provider.GetRequestExecutorAsync();

    var result = await executor.ExecuteAsync("{ openValue }");

    var json = result.ToJson();
    await Assert.That(json).DoesNotContain("errors");
    await Assert.That(json).Contains("open-value");
  }

  [Test]
  public async Task FieldMiddleware_ClassLevelRequirement_MissingPermission_IsEnforcedAsync() {
    // The [RequirePermission] lives on the CLASS, not the resolver method —
    // _collectAttributes must pick it up from the declaring type.
    await using var provider = _buildClassGuardedServer(_scopeWith());
    var executor = await provider.GetRequestExecutorAsync();

    var result = await executor.ExecuteAsync("{ adminValue }");

    var json = result.ToJson();
    await Assert.That(json).Contains("AUTH_NOT_AUTHORIZED");
  }

  [Test]
  public async Task FieldMiddleware_ClassLevelRequirement_Satisfied_ResolvesFieldAsync() {
    await using var provider = _buildClassGuardedServer(_scopeWith("area:admin"));
    var executor = await provider.GetRequestExecutorAsync();

    var result = await executor.ExecuteAsync("{ adminValue }");

    var json = result.ToJson();
    await Assert.That(json).DoesNotContain("errors");
    await Assert.That(json).Contains("admin-value");
  }

  [Test]
  public async Task FieldMiddleware_Mutation_WriteRequirement_MissingPermission_IsEnforcedAsync() {
    // Executing a MUTATION drives the OperationType.Mutation → ScopeOperation.Write
    // arm of the middleware's operation-kind switch.
    await using var provider = _buildGuardedServer(_scopeWith("doc:read"));
    var executor = await provider.GetRequestExecutorAsync();

    var result = await executor.ExecuteAsync("mutation { writeThing }");

    var json = result.ToJson();
    await Assert.That(json).Contains("AUTH_NOT_AUTHORIZED")
      .Because("A Write-scoped requirement must be enforced when the field is reached through a mutation.");
  }

  [Test]
  public async Task FieldMiddleware_Mutation_WriteRequirement_Satisfied_ExecutesAsync() {
    await using var provider = _buildGuardedServer(_scopeWith("doc:write"));
    var executor = await provider.GetRequestExecutorAsync();

    var result = await executor.ExecuteAsync("mutation { writeThing }");

    var json = result.ToJson();
    await Assert.That(json).DoesNotContain("errors");
    await Assert.That(json).Contains("writeThing");
  }
}

/// <summary>
/// Query type whose resolvers carry <c>[UseRequirePermission]</c> declarations —
/// one guarded by a method-level Read requirement, one with the middleware attribute
/// but no requirements (the pass-through arm).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance",
    "CA1822:Mark members as static",
    Justification = "HotChocolate requires instance methods for GraphQL resolvers")]
public class GuardedQuery {
  [UseRequirePermission]
  [RequirePermission("doc:read", Operation = ScopeOperation.Read)]
  public string GetGuardedValue() => "guarded-value";

  [UseRequirePermission]
  public string GetOpenValue() => "open-value";
}

/// <summary>
/// Mutation type with a Write-scoped requirement — drives the Mutation arm of the
/// middleware's operation-kind switch.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance",
    "CA1822:Mark members as static",
    Justification = "HotChocolate requires instance methods for GraphQL resolvers")]
public class GuardedMutation {
  [UseRequirePermission]
  [RequirePermission("doc:write", Operation = ScopeOperation.Write)]
  public bool WriteThing() => true;
}

/// <summary>
/// Query type whose <c>[RequirePermission]</c> lives on the class — exercises the
/// declaring-type half of the attribute collection.
/// </summary>
[RequirePermission("area:admin")]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance",
    "CA1822:Mark members as static",
    Justification = "HotChocolate requires instance methods for GraphQL resolvers")]
public class ClassGuardedQuery {
  [UseRequirePermission]
  public string GetAdminValue() => "admin-value";
}
