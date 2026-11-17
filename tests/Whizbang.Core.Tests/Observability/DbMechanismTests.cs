// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// The mechanism a flow of work belongs to: a closed set of names, each with the comment that leads its statements,
/// carried by a scope that follows the flow and restores what it replaced.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Observability/DbMechanism.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Observability/DbMechanismScope.cs</code-under-test>
/// <docs>operations/observability/database-attribution</docs>
[Category("Observability")]
public class DbMechanismTests {
  [Test]
  public async Task TheSet_IsClosed_AndEachNameCarriesItsCommentAsync() {
    var names = DbMechanism.All.Select(m => m.Name).ToList();

    await Assert.That(names).IsEquivalentTo(names.Distinct().ToList()).Because("a tag value names one mechanism");
    await Assert.That(DbMechanism.All).Contains(DbMechanism.Untagged).Because("the gap is a tag value too");
    foreach (var mechanism in DbMechanism.All) {
      await Assert.That(mechanism.SqlComment).IsEqualTo($"/* whizbang:{mechanism.Name} */ ");
      await Assert.That(mechanism.ToString()).IsEqualTo(mechanism.Name);
      await Assert.That(mechanism.Name).Matches("^[a-z_]+$")
        .Because("a name is safe inside a SQL comment and as a metric tag");
    }
  }

  [Test]
  public async Task OutsideAnyScope_NoMechanismIsCurrentAsync() {
    await Assert.That(DbMechanismScope.Current).IsNull();
  }

  [Test]
  public async Task AScope_NamesItsMechanism_AndDisposingItRestoresTheOneBeforeAsync() {
    using (DbMechanismScope.Begin(DbMechanism.Maintenance)) {
      await Assert.That(DbMechanismScope.Current).IsSameReferenceAs(DbMechanism.Maintenance);
      using (DbMechanismScope.Begin(DbMechanism.TableStatistics)) {
        await Task.Yield();
        await Assert.That(DbMechanismScope.Current).IsSameReferenceAs(DbMechanism.TableStatistics)
          .Because("the scope follows the flow through an await");
      }
      await Assert.That(DbMechanismScope.Current).IsSameReferenceAs(DbMechanism.Maintenance)
        .Because("disposing the inner scope restores the outer one");
    }
    await Assert.That(DbMechanismScope.Current).IsNull();
  }

  [Test]
  public async Task AScopeOpenedInAChildFlow_ChangesNothingForItsParentAsync() {
    using (DbMechanismScope.Begin(DbMechanism.Claim)) {
      var inChild = await _openLeftInAChildFlowAsync();

      await Assert.That(inChild).IsSameReferenceAs(DbMechanism.Lease);
      await Assert.That(DbMechanismScope.Current).IsSameReferenceAs(DbMechanism.Claim);
    }
  }

  // An async method runs on a copy of its caller's flow, so what it sets stays in that copy. Left open on purpose: a
  // child flow's scope must not reach back into the parent.
  private static async Task<DbMechanism?> _openLeftInAChildFlowAsync() {
    await Task.Yield();
    _ = DbMechanismScope.Begin(DbMechanism.Lease);
    return DbMechanismScope.Current;
  }

  [Test]
  public async Task Begin_RefusesANullMechanismAsync() {
    await Assert.That(() => DbMechanismScope.Begin(null!)).Throws<ArgumentNullException>();
  }
}
