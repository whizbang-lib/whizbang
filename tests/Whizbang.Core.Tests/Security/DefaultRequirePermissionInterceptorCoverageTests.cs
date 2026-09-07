using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Security;
using Whizbang.Core.Security.Attributes;

namespace Whizbang.Core.Tests.Security;

/// <summary>
/// Coverage for the strictness-ordering branches <see cref="ReceptorInterceptorTests"/> never
/// reaches: <see cref="DeniedAction.Quarantine"/> outranking <see cref="DeniedAction.Throw"/>, and
/// an out-of-range <see cref="DeniedAction"/> value never winning over a real, known one. The
/// primary suite's only multi-denial case (<c>MixedSeverityReceptor</c>) compares DropQuiet against
/// DeadLetter — Quarantine and Throw never get compared, and the enum's discard arm never runs at
/// all. If the strictest-action-wins comparison silently mis-orders any of these, a receptor that
/// should quarantine or dead-letter on a missing permission could instead drop the message quietly.
/// </summary>
public class DefaultRequirePermissionInterceptorCoverageTests {

  private static ImmutableScopeContext _emptyScope() =>
    new(new SecurityExtraction {
      Scope = new Whizbang.Core.Lenses.PerspectiveScope(),
      Roles = new HashSet<string>(),
      Permissions = new HashSet<Permission>(),
      SecurityPrincipals = new HashSet<SecurityPrincipalId>(),
      Claims = new Dictionary<string, string>(),
      Source = "Test",
    }, shouldPropagate: true);

  [RequirePermission("x", OnDenied = DeniedAction.Throw)]
  [RequirePermission("y", OnDenied = DeniedAction.Quarantine)]
  private sealed class _throwThenQuarantineReceptor { }

  [RequirePermission("x", OnDenied = (DeniedAction)99)]
  [RequirePermission("y", OnDenied = DeniedAction.DropQuiet)]
  private sealed class _unknownActionReceptor { }

  /// <summary>What breaks: Quarantine (3) must outrank Throw (2) so a "must quarantine"
  /// requirement is never silently relaxed to a bare throw by a peer attribute on the same
  /// receptor.</summary>
  [Test]
  public async Task MultipleDenials_QuarantineOutranksThrowAsync() {
    var interceptor = new DefaultRequirePermissionInterceptor();

    var result = await interceptor.CanInvokeAsync(
      typeof(_throwThenQuarantineReceptor), null!, _emptyScope(), CancellationToken.None);

    await Assert.That(result.Allow).IsFalse();
    await Assert.That(result.OnDenied).IsEqualTo(DeniedAction.Quarantine)
      .Because("Quarantine (3) must outrank Throw (2) — the strictest peer attribute's action must win");
  }

  /// <summary>What breaks: an <see cref="DeniedAction"/> value outside the known enum must not win
  /// the strictness comparison over a real, known action — falling back to the weakest ranking
  /// keeps a corrupt/unmapped value from silently overriding an operator's real requirement.</summary>
  [Test]
  public async Task MultipleDenials_UnknownActionFallsBackToLeastStrictAsync() {
    var interceptor = new DefaultRequirePermissionInterceptor();

    var result = await interceptor.CanInvokeAsync(
      typeof(_unknownActionReceptor), null!, _emptyScope(), CancellationToken.None);

    await Assert.That(result.Allow).IsFalse();
    await Assert.That(result.OnDenied).IsEqualTo(DeniedAction.DropQuiet)
      .Because("an out-of-range OnDenied value must rank below every real action, not silently win the comparison");
  }
}
