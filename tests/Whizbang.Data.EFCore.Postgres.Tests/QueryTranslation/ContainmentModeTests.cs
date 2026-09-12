using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Configuration;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;
using Whizbang.Data.EFCore.Postgres.QueryTranslation.Containment;

namespace Whizbang.Data.EFCore.Postgres.Tests.QueryTranslation;

/// <summary>
/// Which mechanism compiles a perspective equality filter into a containment test, if any.
/// </summary>
/// <remarks>
/// <para>
/// Two mechanisms exist while the second is proven. One rewrites the LINQ tree before Entity
/// Framework translates it; the other reshapes what Entity Framework produced. They build the same
/// document from the same rules and differ in what they can see, so a deployment choosing between
/// them is choosing a mechanism rather than a behavior.
/// </para>
/// <para>
/// A third state turns both off, which is what the original switch meant, so the existing
/// configuration keeps working and a rollback stays available without knowing that a second mechanism
/// was ever added.
/// </para>
/// </remarks>
/// <docs>contributors/perspective-query-pipeline</docs>
[Category("Shard1")]
[NotInParallel("ContainmentMode")]
public class ContainmentModeTests {
  [After(Test)]
  public void RestoreDefault() => JsonbContainmentSwitch.Reset();

  /// <summary>
  /// The default is the mechanism that has shipped, not the newer one.
  /// </summary>
  /// <remarks>
  /// Deliberate while the reshape is being proven: nobody's queries change shape because a new
  /// mechanism was added, and the new one earns the default by being green in continuous integration
  /// rather than by being newer.
  /// </remarks>
  [Test]
  public async Task TheDefaultIsTheExpressionTreeMechanismAsync() {
    JsonbContainmentSwitch.Reset();

    await Assert.That(JsonbContainmentSwitch.Mode).IsEqualTo(ContainmentMode.ExpressionTree);
    await Assert.That(JsonbContainmentSwitch.Enabled).IsTrue();
  }

  /// <summary>
  /// The original two-state switch still means what it meant, so existing configuration is unchanged.
  /// </summary>
  [Test]
  [Arguments(true, ContainmentMode.ExpressionTree)]
  [Arguments(false, ContainmentMode.Off)]
  public async Task TheBooleanSwitchStillSelectsTheSameThingAsync(bool enabled, ContainmentMode expected) {
    JsonbContainmentSwitch.Set(enabled);

    await Assert.That(JsonbContainmentSwitch.Mode).IsEqualTo(expected);
    await Assert.That(JsonbContainmentSwitch.Enabled).IsEqualTo(enabled);
  }

  /// <summary>
  /// Enabled means "some mechanism is in force", which is what every consult point actually asks.
  /// </summary>
  [Test]
  [Arguments(ContainmentMode.Off, false)]
  [Arguments(ContainmentMode.ExpressionTree, true)]
  [Arguments(ContainmentMode.TranslatedTree, true)]
  public async Task EnabledMeansSomeMechanismIsInForceAsync(ContainmentMode mode, bool enabled) {
    JsonbContainmentSwitch.SetMode(mode);

    await Assert.That(JsonbContainmentSwitch.Enabled).IsEqualTo(enabled);
  }

  /// <summary>
  /// Each mechanism asks whether it in particular is selected, so exactly one of them ever acts.
  /// </summary>
  /// <remarks>
  /// Both running would compile a filter twice and is the failure this question exists to prevent.
  /// </remarks>
  [Test]
  [Arguments(ContainmentMode.Off, false, false)]
  [Arguments(ContainmentMode.ExpressionTree, true, false)]
  [Arguments(ContainmentMode.TranslatedTree, false, true)]
  public async Task ExactlyOneMechanismActsAsync(
    ContainmentMode mode, bool expressionTree, bool translatedTree) {
    JsonbContainmentSwitch.SetMode(mode);

    await Assert.That(JsonbContainmentSwitch.RewritesExpressionTree).IsEqualTo(expressionTree);
    await Assert.That(JsonbContainmentSwitch.ReshapesTranslatedTree).IsEqualTo(translatedTree);
  }

  /// <summary>Configuration selects a mode by name, so a deployment can choose without a release.</summary>
  [Test]
  public async Task ConfigurationSelectsTheModeAsync() {
    JsonbContainmentSwitch.ApplyRuntimeConfiguration(
      new PerspectiveQueryTranslationOptions { ContainmentMode = ContainmentMode.TranslatedTree });

    await Assert.That(JsonbContainmentSwitch.Mode).IsEqualTo(ContainmentMode.TranslatedTree);
  }

  /// <summary>
  /// Configuration that only says off, the way it was written before a mode existed, still turns
  /// everything off.
  /// </summary>
  [Test]
  public async Task ConfigurationWrittenBeforeModesStillTurnsItOffAsync() {
    JsonbContainmentSwitch.ApplyRuntimeConfiguration(
      new PerspectiveQueryTranslationOptions { UseJsonbContainment = false });

    await Assert.That(JsonbContainmentSwitch.Mode).IsEqualTo(ContainmentMode.Off);
    await Assert.That(JsonbContainmentSwitch.Enabled).IsFalse();
  }
}
