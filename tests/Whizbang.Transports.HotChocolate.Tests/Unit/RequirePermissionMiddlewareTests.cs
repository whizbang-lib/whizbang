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
}
