// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The message registry and the pinned-type ledger are JSON embedded in the consumer's assembly for an MSBuild
/// target to extract. Each is generated only when its switch is on: <c>WhizbangEmitMessageRegistry</c> (off by
/// default in the package, an IDE-tooling opt-in) and <c>WhizbangIncludePinnedTypeLedger</c> (on by default, the
/// committed rename lockfile). A switch that is off leaves no JSON in the binary at all. A project that sets
/// neither (one that loads the generators without the package's props) keeps both.
/// </summary>
/// <docs>operations/performance/build-cost</docs>
[Category("SourceGenerators")]
[Category("BuildCost")]
public class EmbeddedJsonSwitchTests {
  private const string REGISTRY_PROPERTY = "build_property.WhizbangEmitMessageRegistry";
  private const string LEDGER_PROPERTY = "build_property.WhizbangIncludePinnedTypeLedger";

  private const string SOURCE = """
    using Whizbang.Core;
    using Whizbang.Core.Attributes;
    namespace TestApp;

    [PinnedId("11111111-1111-1111-1111-111111111111")]
    public record OrderPlacedEvent : IEvent;
    """;

  [Test]
  [Arguments("false", false)]
  [Arguments("False", false)]
  [Arguments(" false ", false)]
  [Arguments("true", true)]
  [Arguments("", true)]
  [RequiresAssemblyFiles]
  public async Task MessageRegistry_FollowsItsSwitchAsync(string value, bool emitted) {
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(SOURCE, new Dictionary<string, string> { [REGISTRY_PROPERTY] = value });

    await Assert.That(GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs") is not null).IsEqualTo(emitted);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task MessageRegistry_SwitchNotSet_IsEmittedAsync() {
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(SOURCE, new Dictionary<string, string>());

    await Assert.That(GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs")).IsNotNull();
  }

  [Test]
  [Arguments("false", false)]
  [Arguments("FALSE", false)]
  [Arguments("true", true)]
  [RequiresAssemblyFiles]
  public async Task PinnedTypeLedger_FollowsItsSwitchAsync(string value, bool emitted) {
    var result = GeneratorTestHelper.RunGenerator<PinnedTypeLedgerGenerator>(SOURCE, new Dictionary<string, string> { [LEDGER_PROPERTY] = value });

    await Assert.That(GeneratorTestHelper.GetGeneratedSource(result, "PinnedTypeLedger.g.cs") is not null).IsEqualTo(emitted);
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task PinnedTypeLedger_RegistryOff_IsStillEmittedAsync() {
    var result = GeneratorTestHelper.RunGenerator<PinnedTypeLedgerGenerator>(SOURCE, new Dictionary<string, string> { [REGISTRY_PROPERTY] = "false" });

    await Assert.That(GeneratorTestHelper.GetGeneratedSource(result, "PinnedTypeLedger.g.cs")).IsNotNull()
        .Because("the ledger is a committed correctness lockfile and does not depend on the tooling registry");
  }
}
