// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// #2250: the generated perspective registry says which read models carry
/// <c>[DangerouslyAllowMixedEphemeralAndSourcedEvents]</c>, so the ephemeral blocker report can tell an operator when one
/// of them has no durable input left and can drop the attribute. The attribute is otherwise compile-time only.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events#read-models-opted-into-mixing</docs>
/// <code-under-test>src/Whizbang.Generators/PerspectiveRunnerRegistryGenerator.cs</code-under-test>
public class PerspectiveRegistryMixedOptInTests {
  private const string SOURCE = """
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  [Ephemeral]
  public record BatchGenerated : IEvent {
    [StreamId]
    public string BatchId { get; init; } = "";
  }

  public record ItemGenerated : IEvent {
    [StreamId]
    public string BatchId { get; init; } = "";
  }

  public record BatchModel {
    [StreamId]
    public string BatchId { get; init; } = "";
  }

  [DangerouslyAllowMixedEphemeralAndSourcedEvents]
  public class BatchPerspective : IPerspectiveFor<BatchModel, BatchGenerated, ItemGenerated> {
    public BatchModel Apply(BatchModel currentData, BatchGenerated @event) => currentData;
    public BatchModel Apply(BatchModel currentData, ItemGenerated @event) => currentData;
  }

  public class PlainPerspective : IPerspectiveFor<BatchModel, ItemGenerated> {
    public BatchModel Apply(BatchModel currentData, ItemGenerated @event) => currentData;
  }
}
""";

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Registry_MarksAPerspectiveThatAllowsMixing_AndOnlyThatOneAsync() {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerRegistryGenerator>(SOURCE);

    var registry = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRunnerRegistry.g.cs");
    await Assert.That(registry).IsNotNull();
    var batch = _entry(registry!, "TestNamespace.BatchPerspective");
    var plain = _entry(registry!, "TestNamespace.PlainPerspective");
    await Assert.That(batch).Contains("AllowsMixedEphemeralAndSourced = true")
      .Because("a read model with the attribute missing from the registry would never be reported as ready to drop it");
    await Assert.That(plain).DoesNotContain("AllowsMixedEphemeralAndSourced");
  }

  // The generated registration of one perspective: from its CLR name to the next registration or the array's end.
  private static string _entry(string registry, string clrTypeName) {
    var start = registry.IndexOf($"\"{clrTypeName}\",", StringComparison.Ordinal);
    var next = registry.IndexOf("new PerspectiveRegistrationInfo(", start, StringComparison.Ordinal);
    return registry[start..(next < 0 ? registry.IndexOf("];", start, StringComparison.Ordinal) : next)];
  }
}
