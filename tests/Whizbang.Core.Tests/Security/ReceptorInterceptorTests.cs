using Whizbang.Core.Observability;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Attributes;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Tests for <see cref="DefaultRequirePermissionInterceptor"/> — the receptor-level
/// permission gate that consults <see cref="RequirePermissionAttribute"/> on receptor
/// classes and returns an <see cref="InterceptorResult"/> with the appropriate
/// <see cref="DeniedAction"/>.
/// </summary>
/// <tests>DefaultRequirePermissionInterceptor,IReceptorInterceptor,DeniedAction</tests>
public class ReceptorInterceptorTests {
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

  // Test receptor types — attributes only, no IReceptor implementation needed.
  // The interceptor just reads the type's attributes via reflection.
  private sealed class UnannotatedReceptor { }

  [RequirePermission("foo:write")]
  private sealed class AnnotatedDeadLetterReceptor { }

  [RequirePermission("foo:write", OnDenied = DeniedAction.DropQuiet)]
  private sealed class AnnotatedDropReceptor { }

  [RequirePermission("foo:write", OnDenied = DeniedAction.Quarantine)]
  private sealed class AnnotatedQuarantineReceptor { }

  [RequirePermission("foo:read")]
  [RequirePermission("foo:write")]
  private sealed class MultiAttributeReceptor { }

  [RequirePermission("a", OnDenied = DeniedAction.DropQuiet)]
  [RequirePermission("b", OnDenied = DeniedAction.DeadLetter)]
  private sealed class MixedSeverityReceptor { }

  // ===== No attributes => allow =====

  [Test]
  public async Task NoAttributes_AlwaysAllowsAsync() {
    var interceptor = new DefaultRequirePermissionInterceptor();
    var result = await interceptor.CanInvokeAsync(
      typeof(UnannotatedReceptor), null!, _scopeWith(), CancellationToken.None);
    var allowed = result.Allow;
    await Assert.That(allowed).IsTrue();
  }

  [Test]
  public async Task NoAttributes_NullScope_StillAllowsAsync() {
    var interceptor = new DefaultRequirePermissionInterceptor();
    var result = await interceptor.CanInvokeAsync(
      typeof(UnannotatedReceptor), null!, null, CancellationToken.None);
    var allowed = result.Allow;
    await Assert.That(allowed).IsTrue();
  }

  // ===== Single attribute, defaults =====

  [Test]
  public async Task PermissionPresent_AllowsAsync() {
    var interceptor = new DefaultRequirePermissionInterceptor();
    var result = await interceptor.CanInvokeAsync(
      typeof(AnnotatedDeadLetterReceptor), null!, _scopeWith("foo:write"), CancellationToken.None);
    var allowed = result.Allow;
    await Assert.That(allowed).IsTrue();
  }

  [Test]
  public async Task PermissionMissing_DeniesWithDeadLetterDefaultAsync() {
    var interceptor = new DefaultRequirePermissionInterceptor();
    var result = await interceptor.CanInvokeAsync(
      typeof(AnnotatedDeadLetterReceptor), null!, _scopeWith(), CancellationToken.None);
    var allowed = result.Allow;
    var action = result.OnDenied;
    await Assert.That(allowed).IsFalse();
    await Assert.That(action).IsEqualTo(DeniedAction.DeadLetter);
  }

  [Test]
  public async Task PermissionMissing_NullScope_DeniesAsync() {
    var interceptor = new DefaultRequirePermissionInterceptor();
    var result = await interceptor.CanInvokeAsync(
      typeof(AnnotatedDeadLetterReceptor), null!, null, CancellationToken.None);
    var allowed = result.Allow;
    await Assert.That(allowed).IsFalse();
  }

  // ===== Per-attribute OnDenied honored =====

  [Test]
  public async Task PermissionMissing_OnDeniedDropQuiet_DeniesWithDropQuietAsync() {
    var interceptor = new DefaultRequirePermissionInterceptor();
    var result = await interceptor.CanInvokeAsync(
      typeof(AnnotatedDropReceptor), null!, _scopeWith(), CancellationToken.None);
    var action = result.OnDenied;
    await Assert.That(action).IsEqualTo(DeniedAction.DropQuiet);
  }

  [Test]
  public async Task PermissionMissing_OnDeniedQuarantine_DeniesWithQuarantineAsync() {
    var interceptor = new DefaultRequirePermissionInterceptor();
    var result = await interceptor.CanInvokeAsync(
      typeof(AnnotatedQuarantineReceptor), null!, _scopeWith(), CancellationToken.None);
    var action = result.OnDenied;
    await Assert.That(action).IsEqualTo(DeniedAction.Quarantine);
  }

  // ===== Multiple attributes (AND) =====

  [Test]
  public async Task MultipleAttributes_AllSatisfied_AllowsAsync() {
    var interceptor = new DefaultRequirePermissionInterceptor();
    var result = await interceptor.CanInvokeAsync(
      typeof(MultiAttributeReceptor), null!, _scopeWith("foo:read", "foo:write"), CancellationToken.None);
    var allowed = result.Allow;
    await Assert.That(allowed).IsTrue();
  }

  [Test]
  public async Task MultipleAttributes_OneMissing_DeniesAsync() {
    var interceptor = new DefaultRequirePermissionInterceptor();
    var result = await interceptor.CanInvokeAsync(
      typeof(MultiAttributeReceptor), null!, _scopeWith("foo:read"), CancellationToken.None);
    var allowed = result.Allow;
    await Assert.That(allowed).IsFalse();
  }

  // ===== Strictness ordering when multiple denials =====

  [Test]
  public async Task MultipleDenials_StrictestActionWinsAsync() {
    // "a" is missing → DropQuiet; "b" is missing → DeadLetter.
    // DeadLetter is strictest and must win so a "must audit" peer requirement isn't
    // silently downgraded to a DropQuiet by another attribute on the same receptor.
    var interceptor = new DefaultRequirePermissionInterceptor();
    var result = await interceptor.CanInvokeAsync(
      typeof(MixedSeverityReceptor), null!, _scopeWith(), CancellationToken.None);
    var allowed = result.Allow;
    var action = result.OnDenied;
    await Assert.That(allowed).IsFalse();
    await Assert.That(action).IsEqualTo(DeniedAction.DeadLetter);
  }

  // ===== InterceptorResult convenience helpers =====

  [Test]
  public async Task InterceptorResult_Allowed_HasAllowTrueAsync() {
    var allowed = InterceptorResult.Allowed;
    var allow = allowed.Allow;
    await Assert.That(allow).IsTrue();
  }

  [Test]
  public async Task InterceptorResult_Deny_RecordsActionAndReasonAsync() {
    var deny = InterceptorResult.Deny(DeniedAction.Quarantine, "test reason");
    var allow = deny.Allow;
    var action = deny.OnDenied;
    var reason = deny.Reason;
    await Assert.That(allow).IsFalse();
    await Assert.That(action).IsEqualTo(DeniedAction.Quarantine);
    await Assert.That(reason).IsEqualTo("test reason");
  }
}
