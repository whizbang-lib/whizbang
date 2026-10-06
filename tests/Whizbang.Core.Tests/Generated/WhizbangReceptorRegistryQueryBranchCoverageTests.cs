// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Generated;
using Whizbang.Core.Messaging;
using Whizbang.Core.Registry;

namespace Whizbang.Core.Tests.Generated;

/// <summary>
/// Branch coverage for <see cref="WhizbangReceptorRegistryQuery"/>'s type-name normalization of a
/// NESTED message type: the runtime spells it <c>Outer+Inner</c>, the generator stores it as
/// <c>Outer.Inner</c>, and the drop-gate lookup must bridge the two or every nested message is
/// dropped as having no consumer.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Generated/WhizbangReceptorRegistryQuery.cs</code-under-test>
[NotInParallel("ReceptorRegistryQueryAggregation")]
public class WhizbangReceptorRegistryQueryBranchCoverageTests {

  [Before(Test)]
  public void ResetRegistry() {
    AssemblyRegistry<ReceptorRegistryContribution>.ClearForTesting();
    WhizbangReceptorRegistryQuery.ClearCacheForTesting();
  }

  [After(Test)]
  public void TeardownRegistry() => ResetRegistry();

  [Test]
  public async Task NestedTypeRuntimeName_NormalizesThePlusSeparatorToTheStoredDottedFormAsync() {
    AssemblyRegistry<ReceptorRegistryContribution>.Register(new ReceptorRegistryContribution {
      AnyConsumerTypes = ["MyApp.Contracts.Outer.NestedEvent"],
      InboxHandlerTypes = ["MyApp.Contracts.Outer.NestedEvent"],
      StageTypes = new Dictionary<LifecycleStage, IReadOnlyCollection<string>> {
        [LifecycleStage.PreInboxInline] = ["MyApp.Contracts.Outer.NestedEvent"],
      },
    });

    await Assert.That(WhizbangReceptorRegistryQuery.HasAnyConsumer("MyApp.Contracts.Outer+NestedEvent, MyApp.Contracts")).IsTrue()
      .Because("the runtime's + separator for a nested type must match the generator's dotted form");
    await Assert.That(WhizbangReceptorRegistryQuery.HasInboxHandler("MyApp.Contracts.Outer+NestedEvent")).IsTrue();
    await Assert.That(WhizbangReceptorRegistryQuery.HasReceptors(LifecycleStage.PreInboxInline, "MyApp.Contracts.Outer+NestedEvent, MyApp.Contracts")).IsTrue();
    await Assert.That(WhizbangReceptorRegistryQuery.HasAnyConsumer("MyApp.Contracts.Other+NestedEvent, MyApp.Contracts")).IsFalse()
      .Because("normalization converts the separator; it must not loosen the match to any type with the same short name");
  }
}
