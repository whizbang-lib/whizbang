// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Routing;

namespace Whizbang.Core.Tests.Routing;

/// <summary>
/// Branch backfill for <see cref="NamespaceRoutingStrategy.DefaultTypeToTopic"/>: a type declared
/// outside any namespace has no topic to derive and is rejected loudly.
/// </summary>
public class NamespaceRoutingStrategyBranchCoverageTests {

  [Test]
  public async Task DefaultTypeToTopic_TypeWithoutNamespace_ThrowsAsync() {
    var caught = await Assert.That(() => NamespaceRoutingStrategy.DefaultTypeToTopic(typeof(global::TypeWithoutNamespace)))
      .ThrowsExactly<InvalidOperationException>()
      .Because("a namespace-less type has no topic; routing it to an empty or guessed topic would misdeliver silently");
    await Assert.That(caught!.Message).Contains(typeof(global::TypeWithoutNamespace).Name);
  }

  [Test]
  public async Task ResolveTopic_TypeWithNamespace_IsTheLowercasedNamespaceAsync() {
    var topic = new NamespaceRoutingStrategy().ResolveTopic(typeof(OutboxTestTypes.Orders.Events.OrderCreated), "ignored");
    await Assert.That(topic).IsEqualTo("outboxtesttypes.orders.events");
  }
}
