// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Core.Tests.ServiceScope;

/// <summary>Every generated assembly's build-time facts are recorded once per assembly (#2243).</summary>
/// <code-under-test>src/Whizbang.Core/WhizbangServiceAssemblies.cs</code-under-test>
[NotInParallel("WhizbangBackgroundServiceTests")]
public class WhizbangServiceAssembliesTests {
  [Test]
  public async Task Register_RecordsTheFacts_AndARegistrationAgainReplacesThemAsync() {
    using var _ = WhizbangServiceAssemblies.ReplaceForTesting();

    WhizbangServiceAssemblies.Register("Orders", false, ["Shared"]);
    WhizbangServiceAssemblies.Register("Orders", true, ["Shared", "Other"]);

    var facts = WhizbangServiceAssemblies.Snapshot();
    await Assert.That(facts.Count).IsEqualTo(1);
    await Assert.That(facts["Orders"].IsServiceRoot).IsTrue();
    await Assert.That(facts["Orders"].References).IsEquivalentTo(["Shared", "Other"]);
  }

  [Test]
  public async Task ThisTestAssembly_RecordedItsOwnFacts_ThroughItsGeneratedModuleInitializerAsync() {
    var facts = WhizbangServiceAssemblies.Snapshot();

    await Assert.That(facts.ContainsKey("Whizbang.Core.Tests")).IsTrue()
      .Because("the generator emits a module initializer that records every generated assembly's facts");
    await Assert.That(facts["Whizbang.Core.Tests"].IsServiceRoot).IsTrue()
      .Because("a test project is an executable");
    await Assert.That(facts["Whizbang.Core.Tests"].References).Contains("Whizbang.Core");
  }

  [Test]
  public async Task ReplaceForTesting_PutsTheRealFactsBackAsync() {
    var before = WhizbangServiceAssemblies.Snapshot().Count;
    using (WhizbangServiceAssemblies.ReplaceForTesting(new ServiceAssemblyFacts("Only", false, []))) {
      await Assert.That(WhizbangServiceAssemblies.Snapshot().Keys).IsEquivalentTo(["Only"]);
    }

    await Assert.That(WhizbangServiceAssemblies.Snapshot().Count).IsEqualTo(before);
  }

  [Test]
  public async Task Register_ValidatesItsArgumentsAsync() {
    await Assert.That(() => WhizbangServiceAssemblies.Register(null!, false, [])).Throws<ArgumentNullException>();
    await Assert.That(() => WhizbangServiceAssemblies.Register("A", false, null!)).Throws<ArgumentNullException>();
  }
}
