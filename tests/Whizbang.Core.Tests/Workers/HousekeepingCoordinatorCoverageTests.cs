using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Coverage for <see cref="HousekeepingCoordinator.IntegrityScope"/>'s equality members —
/// <c>Equals(IntegrityScope)</c>, <c>Equals(object?)</c>, <c>GetHashCode</c>, and the
/// <c>==</c>/<c>!=</c> operators — none of which <see cref="HousekeepingCoordinatorTests"/> or
/// <see cref="HousekeepingIntegrityPriorityTests"/> exercise (both only ever <c>Dispose</c> a scope).
/// </summary>
public class HousekeepingCoordinatorCoverageTests {

  // A scope that was actually GRANTED the integrity slot must equal another grant from the same
  // coordinator; if equality instead said two distinct grants were unequal, code relying on
  // deduplicating or comparing scopes (tests, diagnostics) would see churn that isn't real.
  [Test]
  public async Task Equals_TwoGrantedScopesFromSameCoordinator_AreEqualAsync() {
    var coordinator = new HousekeepingCoordinator();
    using var first = coordinator.BeginIntegrityScope();
    first.Dispose();
    using var second = coordinator.BeginIntegrityScope();

    await Assert.That(first.Granted).IsTrue();
    await Assert.That(second.Granted).IsTrue();
    await Assert.That(first.Equals(second)).IsTrue()
      .Because("both scopes were granted by the SAME coordinator instance; the struct's identity is the owning coordinator plus the granted flag, and two grants from the same owner must compare equal.");
  }

  // A grant from one coordinator must never equal a grant from a different coordinator — if it
  // did, code that uses equality to check "is this the scope I was handed" could be fooled by an
  // unrelated coordinator's scope, which is exactly the "one scope's work claimed under another"
  // failure mode this type exists to prevent.
  [Test]
  public async Task Equals_GrantedScopesFromDifferentCoordinators_AreNotEqualAsync() {
    var coordinatorA = new HousekeepingCoordinator();
    var coordinatorB = new HousekeepingCoordinator();
    using var scopeA = coordinatorA.BeginIntegrityScope();
    using var scopeB = coordinatorB.BeginIntegrityScope();

    await Assert.That(scopeA.Equals(scopeB)).IsFalse()
      .Because("the two scopes are backed by different coordinator instances; ReferenceEquals on the owner must be false regardless of both being Granted.");
  }

  // A REFUSED scope (AlreadyRunning) carries no owner at all. It must never equal the live grant
  // it lost out to — mistaking a refusal for the real holder would let a caller believe it holds
  // the exclusive integrity slot when it does not, reintroducing the overlap the coordinator
  // exists to prevent.
  [Test]
  public async Task Equals_GrantedScopeAndRefusedScope_AreNotEqualAsync() {
    var coordinator = new HousekeepingCoordinator();
    using var granted = coordinator.BeginIntegrityScope();
    using var refused = coordinator.BeginIntegrityScope(); // integrity already running -> AlreadyRunning

    await Assert.That(granted.Granted).IsTrue();
    await Assert.That(refused.Granted).IsFalse();
    await Assert.That(granted.Equals(refused)).IsFalse()
      .Because("a refused scope must never compare equal to the grant that is actually holding the slot.");
  }

  // Two refused/default scopes carry no owner (null) and Granted=false — they are indistinguishable
  // "nothing" values and must compare equal to each other.
  [Test]
  public async Task Equals_DefaultScopeAndRefusedScope_AreEqualAsync() {
    var coordinator = new HousekeepingCoordinator();
    using var holder = coordinator.BeginIntegrityScope(); // holds the slot so the next attempt is refused
    using var refused = coordinator.BeginIntegrityScope();
    var untouched = default(HousekeepingCoordinator.IntegrityScope);

    await Assert.That(refused.Granted).IsFalse();
    await Assert.That(refused.Equals(untouched)).IsTrue()
      .Because("a refused scope and a never-acquired default scope are both the 'holds nothing' value (null owner, Granted=false) and must be indistinguishable.");
  }

  // object.Equals must delegate to the typed Equals when given a boxed IntegrityScope.
  [Test]
  public async Task ObjectEquals_WithBoxedIntegrityScope_DelegatesToTypedEqualsAsync() {
    var coordinator = new HousekeepingCoordinator();
    using var first = coordinator.BeginIntegrityScope();
    first.Dispose();
    using var second = coordinator.BeginIntegrityScope();

    object boxedSecond = second;

    await Assert.That(first.Equals(boxedSecond)).IsTrue()
      .Because("object.Equals(object?) must pattern-match the boxed value back to IntegrityScope and delegate to the typed Equals, not just report false for any non-null argument.");
  }

  // object.Equals must return false — never throw — for null or a value of an unrelated type.
  [Test]
  public async Task ObjectEquals_WithNonIntegrityScopeValue_ReturnsFalseAsync() {
    var coordinator = new HousekeepingCoordinator();
    using var scope = coordinator.BeginIntegrityScope();

    await Assert.That(scope.Equals("not a scope")).IsFalse()
      .Because("the `obj is IntegrityScope other` pattern must fail closed for an unrelated type instead of throwing an InvalidCastException.");
    await Assert.That(scope.Equals(null)).IsFalse()
      .Because("`obj is IntegrityScope` must also fail closed for null.");
  }

  // GetHashCode must agree with Equals: equal instances hash identically, per the
  // Equals/GetHashCode contract every dictionary/HashSet usage depends on.
  [Test]
  public async Task GetHashCode_EqualScopes_ProduceTheSameHashAsync() {
    var coordinator = new HousekeepingCoordinator();
    using var first = coordinator.BeginIntegrityScope();
    first.Dispose();
    using var second = coordinator.BeginIntegrityScope();

    await Assert.That(first.Equals(second)).IsTrue();
    await Assert.That(first.GetHashCode()).IsEqualTo(second.GetHashCode())
      .Because("HashCode.Combine(_owner, Granted) must produce identical hashes for two values that Equals reports as equal — violating this contract would corrupt any Dictionary/HashSet keyed on IntegrityScope.");
  }

  // The == operator must mirror Equals exactly, in both directions.
  [Test]
  public async Task EqualityOperator_MirrorsEqualsAsync() {
    var coordinatorA = new HousekeepingCoordinator();
    var coordinatorB = new HousekeepingCoordinator();
    using var firstFromA = coordinatorA.BeginIntegrityScope();
    firstFromA.Dispose();
    using var secondFromA = coordinatorA.BeginIntegrityScope();
    using var fromB = coordinatorB.BeginIntegrityScope();

    await Assert.That(firstFromA == secondFromA).IsTrue()
      .Because("the == operator is defined as left.Equals(right); it must agree with Equals for two grants from the same coordinator.");
    await Assert.That(firstFromA == fromB).IsFalse()
      .Because("the == operator must also agree with Equals when the scopes come from different coordinators.");
  }

  // The != operator must mirror !Equals exactly, in both directions.
  [Test]
  public async Task InequalityOperator_MirrorsNotEqualsAsync() {
    var coordinatorA = new HousekeepingCoordinator();
    var coordinatorB = new HousekeepingCoordinator();
    using var firstFromA = coordinatorA.BeginIntegrityScope();
    firstFromA.Dispose();
    using var secondFromA = coordinatorA.BeginIntegrityScope();
    using var fromB = coordinatorB.BeginIntegrityScope();

    await Assert.That(firstFromA != secondFromA).IsFalse()
      .Because("the != operator is defined as !left.Equals(right); two grants from the same coordinator must not report 'not equal'.");
    await Assert.That(firstFromA != fromB).IsTrue()
      .Because("scopes from different coordinators must report 'not equal' via the != operator, not just via Equals.");
  }
}
