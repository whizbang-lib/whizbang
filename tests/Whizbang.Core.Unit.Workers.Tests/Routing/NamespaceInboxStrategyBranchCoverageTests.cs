// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Branch backfill for <see cref="NamespaceInboxStrategy.IsControlClassSubscription"/>: every way a
/// subscription can fail to carry a <c>true</c> control-class marker reads as not control-class.
/// </summary>
public class NamespaceInboxStrategyBranchCoverageTests {

  [Test]
  public async Task IsControlClassSubscription_NullSubscription_IsFalseAsync() {
    await Assert.That(NamespaceInboxStrategy.IsControlClassSubscription(null)).IsFalse()
      .Because("no subscription is no control-class subscription; a provisioner must not enable sessionless mode for it");
  }

  [Test]
  public async Task IsControlClassSubscription_NoMetadata_IsFalseAsync() {
    await Assert.That(NamespaceInboxStrategy.IsControlClassSubscription(new InboxSubscription("inbox.orders"))).IsFalse();
  }

  [Test]
  public async Task IsControlClassSubscription_MetadataWithoutTheMarker_IsFalseAsync() {
    var subscription = new InboxSubscription("inbox.orders", Metadata: new Dictionary<string, object> { ["Other"] = true });
    await Assert.That(NamespaceInboxStrategy.IsControlClassSubscription(subscription)).IsFalse()
      .Because("another metadata entry set to true is not the control-class marker");
  }

  [Test]
  public async Task IsControlClassSubscription_MarkerThatIsNotBooleanTrue_IsFalseAsync() {
    var subscription = new InboxSubscription(
      "inbox.orders",
      Metadata: new Dictionary<string, object> { [NamespaceInboxStrategy.ControlClassMetadataKey] = "true" });
    await Assert.That(NamespaceInboxStrategy.IsControlClassSubscription(subscription)).IsFalse()
      .Because("only the boolean true marks the class; a string that reads like true does not");
  }
}
