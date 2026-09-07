using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage-focused tests for MessageTagDiscoveryGenerator targeting: the non-public-type skip in
/// tag discovery, and a custom tag attribute's per-instance Exclude flag. Complements
/// MessageTagDiscoveryGeneratorTests.cs.
/// </summary>
/// <tests>src/Whizbang.Generators/MessageTagDiscoveryGenerator.cs</tests>
public class MessageTagDiscoveryGeneratorCoverageTests {

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_InternalTaggedType_ExcludedFromRegistryAsync() {
    // Only PUBLIC types are safe to reference from the generated (public) registry class — an
    // internal type discovered here would either fail to compile in the generated file or leak a
    // test-only type into a real assembly's registry. A non-public [MessageTag] type must be
    // silently skipped while a sibling public type still registers normally.
    const string source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            [SignalTag(Tag = "internal-event")]
            internal record InternalOnlyEvent(Guid Id);

            [SignalTag(Tag = "public-event")]
            public record PublicOnlyEvent(Guid Id);
            """;

    var result = GeneratorTestHelper.RunGenerator<MessageTagDiscoveryGenerator>(source);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTagRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("PublicOnlyEvent")
      .Because("the public tagged type must still register normally.");
    await Assert.That(code!).DoesNotContain("InternalOnlyEvent")
      .Because("a non-public tagged type must never reach the generated (public) registry.");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_CustomTagAttributeWithExcludeTrue_SkipsThatRegistrationAsync() {
    // A custom tag attribute may declare its own Exclude flag (per the generator's own comment:
    // "e.g., system events that shouldn't trigger tag hooks"). Exclude = true must drop just that
    // ONE attribute instance from the registry, not the whole type, and must not disturb a
    // sibling instance on the same type that leaves Exclude at its default.
    const string source = """
            using System;
            using Whizbang.Core.Attributes;

            namespace TestApp;

            public class OpsTagAttribute : MessageTagAttribute {
              public bool Exclude { get; init; }
            }

            [OpsTag(Tag = "silent-heartbeat", Exclude = true)]
            [OpsTag(Tag = "visible-heartbeat")]
            public record HeartbeatEvent(Guid Id);
            """;

    var result = GeneratorTestHelper.RunGenerator<MessageTagDiscoveryGenerator>(source);

    var code = GeneratorTestHelper.GetGeneratedSource(result, "MessageTagRegistry.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("visible-heartbeat")
      .Because("the sibling attribute instance without Exclude must still register.");
    await Assert.That(code!).DoesNotContain("silent-heartbeat")
      .Because("Exclude = true must drop that specific tag attribute instance from the registry.");
  }
}
