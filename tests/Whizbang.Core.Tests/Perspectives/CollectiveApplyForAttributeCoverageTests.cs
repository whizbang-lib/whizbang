using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Targeted coverage for <see cref="CollectiveApplyForAttribute.BatchSize"/> and
/// <see cref="CollectiveApplyForAttribute.StatementTimeoutSeconds"/> — the per-handler override
/// knobs the broader <see cref="CollectiveSpecContractTests"/> suite doesn't exercise (it locks
/// <see cref="CollectiveApplyForAttribute.ScopeHandling"/> and
/// <see cref="CollectiveApplyForAttribute.SpecKind"/> only). Read at compile time by
/// <c>CollectiveApplyDiscoveryGenerator</c> via Roslyn symbols, not by any runtime reflection —
/// but the property contract itself (0 = inherit the global default; positive = override) is
/// exactly what a handler author sets through the attribute syntax, so it must round-trip
/// correctly on the type the generator reads from.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Perspectives/CollectiveApplyForAttribute.cs</code-under-test>
public class CollectiveApplyForAttributeCoverageTests {

  [Test]
  public async Task BatchSizeAndStatementTimeoutSeconds_Default_AreZeroSentinelsAsync() {
    // 0 is the documented "inherit the global CollectiveApplyOptions default" sentinel (attribute
    // arguments can't be nullable). If either defaulted to something else, every handler that
    // never sets these would silently pick up a batch size / timeout the operator never configured.
    var attr = new CollectiveApplyForAttribute();

    await Assert.That(attr.BatchSize).IsEqualTo(0)
      .Because("0 inherits CollectiveApplyOptions.BatchSize rather than forcing a per-handler override");
    await Assert.That(attr.StatementTimeoutSeconds).IsEqualTo(0)
      .Because("0 inherits CollectiveApplyOptions.StatementTimeoutSeconds rather than forcing a per-handler override");
  }

  [Test]
  public async Task BatchSize_ExplicitPositiveValue_OverridesTheGlobalDefaultAsync() {
    // A heavy handler shrinking its batch for briefer lock holds depends on this value surviving
    // exactly as written to the code the generator emits.
    var attr = new CollectiveApplyForAttribute { BatchSize = 50 };

    await Assert.That(attr.BatchSize).IsEqualTo(50);
  }

  [Test]
  public async Task StatementTimeoutSeconds_ExplicitPositiveValue_OverridesTheGlobalDefaultAsync() {
    var attr = new CollectiveApplyForAttribute { StatementTimeoutSeconds = 15 };

    await Assert.That(attr.StatementTimeoutSeconds).IsEqualTo(15);
  }
}
