// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;

namespace Whizbang.Core.Tests.Archival;

/// <summary>
/// The per-perspective policy for rows of archived streams (#1739): what a perspective declares, what configuration
/// overrides, what the runner consults on a missing row, and the setting the SQL archive reads.
/// </summary>
/// <remarks>
/// Every test registers names of its own, because the registry is process-wide. The tests that apply runtime overrides
/// replace the whole override set, so they never run beside each other.
/// </remarks>
/// <docs>fundamentals/events/archive-store#perspective-rows</docs>
/// <code-under-test>src/Whizbang.Core/Archival/ArchivedStreamRowPolicies.cs</code-under-test>
[NotInParallel("ArchivedStreamRowPoliciesRuntimeConfig")]
public class ArchivedStreamRowPoliciesTests {
  private static string _name(string suffix) => $"Tests.ArchivedRows.{suffix}+{Guid.NewGuid():N}";

  [Test]
  public async Task Resolve_APerspectiveThatDeclaresNothing_KeepsItsRowsAsync() {
    var name = _name("Undeclared");

    await Assert.That(ArchivedStreamRowPolicies.Resolve(name)).IsEqualTo(ArchivedStreamRowPolicy.Keep)
      .Because("Keep is the default: archiving a stream never removes a read model nobody chose to remove");
    await Assert.That(ArchivedStreamRowPolicies.RemovesRows(name)).IsFalse();
    var unset = new ArchivedStreamRowsOptions().Perspectives.GetValueOrDefault(name);
    await Assert.That(unset).IsEqualTo(ArchivedStreamRowPolicy.Keep)
      .Because("an unset value is the safe one");
  }

  [Test]
  public async Task Resolve_ADeclaredPolicy_IsWhatThePerspectiveDeclaredAsync() {
    var dropped = _name("Dropped");
    var archived = _name("Archived");
    var kept = _name("Kept");
    ArchivedStreamRowPolicies.Register(dropped, ArchivedStreamRowPolicy.Drop);
    ArchivedStreamRowPolicies.Register(archived, ArchivedStreamRowPolicy.Archive);
    ArchivedStreamRowPolicies.Register(kept, ArchivedStreamRowPolicy.Keep);

    await Assert.That(ArchivedStreamRowPolicies.Resolve(dropped)).IsEqualTo(ArchivedStreamRowPolicy.Drop);
    await Assert.That(ArchivedStreamRowPolicies.Resolve(archived)).IsEqualTo(ArchivedStreamRowPolicy.Archive);
    await Assert.That(ArchivedStreamRowPolicies.Resolve(kept)).IsEqualTo(ArchivedStreamRowPolicy.Keep);
    await Assert.That(ArchivedStreamRowPolicies.RemovesRows(dropped)).IsTrue()
      .Because("a dropped row is missing when its stream comes back, so the runner must rebuild it from history");
    await Assert.That(ArchivedStreamRowPolicies.RemovesRows(archived)).IsTrue()
      .Because("an archived row is out of its table until its stream comes back, so a missing row is rebuilt, not restarted");
    await Assert.That(ArchivedStreamRowPolicies.RemovesRows(kept)).IsFalse();
  }

  [Test]
  public async Task Register_RefusesABlankName_OrOneTheSettingCannotCarryAsync() {
    await Assert.That(() => ArchivedStreamRowPolicies.Register("", ArchivedStreamRowPolicy.Drop)).Throws<ArgumentException>();
    await Assert.That(() => ArchivedStreamRowPolicies.Register(null!, ArchivedStreamRowPolicy.Drop)).Throws<ArgumentNullException>();
    await Assert.That(() => ArchivedStreamRowPolicies.Register("a=b", ArchivedStreamRowPolicy.Drop)).Throws<ArgumentException>()
      .Because("the stored setting is one name=policy per line, so a name holding = or a line break cannot be stored");
    await Assert.That(() => ArchivedStreamRowPolicies.Register("a\nb", ArchivedStreamRowPolicy.Drop)).Throws<ArgumentException>();
    await Assert.That(() => ArchivedStreamRowPolicies.Register("a\rb", ArchivedStreamRowPolicy.Drop)).Throws<ArgumentException>();
  }

  [Test]
  public async Task ApplyRuntimeConfiguration_AnOverride_OutranksTheDeclaration_AndNullRestoresItAsync() {
    var declared = _name("Declared");
    var undeclared = _name("Undeclared");
    ArchivedStreamRowPolicies.Register(declared, ArchivedStreamRowPolicy.Drop);
    try {
      ArchivedStreamRowPolicies.ApplyRuntimeConfiguration(new Dictionary<string, ArchivedStreamRowPolicy> {
        [declared] = ArchivedStreamRowPolicy.Keep,
        [undeclared] = ArchivedStreamRowPolicy.Archive,
      });

      await Assert.That(ArchivedStreamRowPolicies.Resolve(declared)).IsEqualTo(ArchivedStreamRowPolicy.Keep)
        .Because("configuration outranks the attribute, so an operator can stop a drop without a redeploy");
      await Assert.That(ArchivedStreamRowPolicies.Resolve(undeclared)).IsEqualTo(ArchivedStreamRowPolicy.Archive)
        .Because("configuration can set a policy on a perspective that declares none");
      await Assert.That(ArchivedStreamRowPolicies.RemovesRows(undeclared)).IsTrue();
      await Assert.That(ArchivedStreamRowPolicies.Resolve(_name("NotOverridden"))).IsEqualTo(ArchivedStreamRowPolicy.Keep)
        .Because("an override set that does not name a perspective leaves it to its declaration");

      ArchivedStreamRowPolicies.ApplyRuntimeConfiguration(null);

      await Assert.That(ArchivedStreamRowPolicies.Resolve(declared)).IsEqualTo(ArchivedStreamRowPolicy.Drop);
      await Assert.That(ArchivedStreamRowPolicies.Resolve(undeclared)).IsEqualTo(ArchivedStreamRowPolicy.Keep);

      ArchivedStreamRowPolicies.ApplyRuntimeConfiguration(new Dictionary<string, ArchivedStreamRowPolicy>());

      await Assert.That(ArchivedStreamRowPolicies.Resolve(declared)).IsEqualTo(ArchivedStreamRowPolicy.Drop)
        .Because("an empty override set is the same as none");
    } finally {
      ArchivedStreamRowPolicies.ApplyRuntimeConfiguration(null);
    }
  }

  [Test]
  public async Task Effective_JoinsTheDeclarationsAndTheOverrides_OverridesWinning_SortedByNameAsync() {
    var prefix = $"Tests.ArchivedRows.Effective{Guid.NewGuid():N}.";
    ArchivedStreamRowPolicies.Register(prefix + "B", ArchivedStreamRowPolicy.Drop);
    ArchivedStreamRowPolicies.Register(prefix + "A", ArchivedStreamRowPolicy.Archive);

    var effective = ArchivedStreamRowPolicies.Effective(new Dictionary<string, ArchivedStreamRowPolicy> {
      [prefix + "B"] = ArchivedStreamRowPolicy.Keep,
      [prefix + "C"] = ArchivedStreamRowPolicy.Drop,
    }).Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList();
    var declaredOnly = ArchivedStreamRowPolicies.Effective(null).Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList();

    await Assert.That(effective).IsEquivalentTo([
      new KeyValuePair<string, ArchivedStreamRowPolicy>(prefix + "A", ArchivedStreamRowPolicy.Archive),
      new KeyValuePair<string, ArchivedStreamRowPolicy>(prefix + "B", ArchivedStreamRowPolicy.Keep),
      new KeyValuePair<string, ArchivedStreamRowPolicy>(prefix + "C", ArchivedStreamRowPolicy.Drop),
    ], TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("an explicit Keep is carried too: it outvotes another perspective of the same model that drops");
    await Assert.That(declaredOnly).IsEquivalentTo([
      new KeyValuePair<string, ArchivedStreamRowPolicy>(prefix + "A", ArchivedStreamRowPolicy.Archive),
      new KeyValuePair<string, ArchivedStreamRowPolicy>(prefix + "B", ArchivedStreamRowPolicy.Drop),
    ], TUnit.Assertions.Enums.CollectionOrdering.Matching);
  }

  [Test]
  public async Task SettingFor_WritesOneNameAndPolicyPerLine_UnderItsKeyAsync() {
    var setting = ArchivedStreamRowPolicies.SettingFor([
      new KeyValuePair<string, ArchivedStreamRowPolicy>("Shop.A", ArchivedStreamRowPolicy.Archive),
      new KeyValuePair<string, ArchivedStreamRowPolicy>("Shop.B+Nested", ArchivedStreamRowPolicy.Drop),
      new KeyValuePair<string, ArchivedStreamRowPolicy>("Shop.C", ArchivedStreamRowPolicy.Keep),
    ]);
    var none = ArchivedStreamRowPolicies.SettingFor([]);

    await Assert.That(setting.Key).IsEqualTo("archived_stream_rows")
      .Because("the SQL archive reads its policies from this wh_settings key");
    await Assert.That(setting.Value).IsEqualTo("Shop.A=Archive\nShop.B+Nested=Drop\nShop.C=Keep")
      .Because("the archive splits the lines with string_to_array, which cannot fail, so a value edited by hand never fails an archive");
    await Assert.That(setting.ValueType).IsEqualTo("string");
    await Assert.That(setting.Source).IsEqualTo(ArchivedStreamRowsOptions.SECTION);
    await Assert.That(none.Value).IsEqualTo(string.Empty)
      .Because("the setting is written every time, so a policy removed from the code stops applying");
  }
}
