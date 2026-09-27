using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Priority;

namespace Whizbang.Core.Tests.Priority;

/// <summary>
/// Namespace classification rules: the most specific namespace must be consulted first, so the rules are kept
/// longest first, and classifying a namespace again replaces its earlier priority rather than adding a second,
/// conflicting rule for it.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Priority/PriorityOptions.cs</code-under-test>
[Category("Core")]
[Category("Priority")]
public class PriorityOptionsTests {
  private static readonly string[] _longestFirst = ["Orders.Billing.Refunds", "Orders.Billing", "Orders"];

  [Test]
  public async Task ClassifyNamespace_SeveralNamespaces_KeepsTheLongestFirstAsync() {
    var options = new PriorityOptions()
      .ClassifyNamespace("Orders", 300)
      .ClassifyNamespace("Orders.Billing.Refunds", 100)
      .ClassifyNamespace("Orders.Billing", 200);

    var namespaces = options.NamespaceRules.Select(r => r.Namespace).ToList();

    await Assert.That(namespaces).IsEquivalentTo(_longestFirst, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a message in Orders.Billing.Refunds must meet its own rule before the broader Orders.Billing and Orders rules");
  }

  [Test]
  public async Task ClassifyNamespace_SameNamespaceAgain_ReplacesTheEarlierPriorityAsync() {
    var options = new PriorityOptions()
      .ClassifyNamespace("Orders.Billing", 200)
      .ClassifyNamespace("Orders", 300)
      .ClassifyNamespace("Orders.Billing", 50);

    await Assert.That(options.NamespaceRules.Count).IsEqualTo(2)
      .Because("reclassifying a namespace replaces its rule; two rules for one namespace would leave the winner to chance");
    await Assert.That(options.NamespaceRules.Single(r => r.Namespace == "Orders.Billing").Priority).IsEqualTo(50);
    await Assert.That(options.NamespaceRules.Single(r => r.Namespace == "Orders").Priority).IsEqualTo(300)
      .Because("replacing one namespace's rule leaves every other namespace's rule as it was");
  }
}
