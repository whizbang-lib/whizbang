// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Every generated runner registers its perspective's write mode (#2246) under the perspective's CLR type name, the name
/// work items carry: the mode its <c>[PerspectiveWriteMode]</c> declares, or null to take the service's. The worker reads
/// it to choose each pair's path without resolving the runner, and a perspective with no generated runner, never
/// registered, is written per stream.
/// </summary>
/// <docs>fundamentals/perspectives/write-modes#selection</docs>
/// <code-under-test>src/Whizbang.Generators/PerspectiveRunnerGenerator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/Templates/PerspectiveRunnerTemplate.cs</code-under-test>
public class PerspectiveWriteModeGeneratorTests {
  private const string REGISTER = "global::Whizbang.Core.Perspectives.PerspectiveWriteModes.Register(_archivedStreamRowsName, ";

  private static string _runner(string attribute) {
    var source = $$"""
using System;
using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record OrderPlaced : IEvent {
    [StreamId]
    public Guid OrderId { get; init; }
  }

  public record OrderModel {
    [StreamId]
    public Guid OrderId { get; init; }
  }

  {{attribute}}
  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderPlaced> {
    public OrderModel Apply(OrderModel currentData, OrderPlaced @event) => currentData;
  }
}
""";
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    return GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs")!;
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task APerspectiveThatDeclaresNoMode_IsRegisteredToTakeTheServicesAsync() {
    var runner = _runner("");

    await Assert.That(runner).Contains("internal static void _registerWriteMode() =>");
    await Assert.That(runner).Contains(REGISTER + "null);")
      .Because("a generated runner prepares writes, so it is registered; with no declaration it takes the service's mode");
    await Assert.That(runner).Contains("private const string _archivedStreamRowsName = \"TestNamespace.OrderPerspective\";")
      .Because("the mode is keyed by the name work items carry");
  }

  [Test]
  [RequiresAssemblyFiles()]
  [Arguments("PerspectiveWriteMode.SetBased", 0)]
  [Arguments("PerspectiveWriteMode.PerStream", 1)]
  public async Task ADeclaredMode_IsRegisteredAsync(string mode, int value) {
    var runner = _runner($"[PerspectiveWriteMode({mode})]");

    await Assert.That(runner).Contains(REGISTER + $"(global::Whizbang.Core.Perspectives.PerspectiveWriteMode){value});");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task TheRunner_ExposesTheReadItsRunMakes_ForTheBatchAsync() {
    var runner = _runner("");

    await Assert.That(runner).Contains("public async Task<IReadOnlyList<MessageEnvelope<IEvent>>?> ReadEventsAsync(");
    await Assert.That(runner).Contains("var events = await ReadEventsAsync(streamId, lastProcessedEventId, cancellationToken);")
      .Because("RunAsync is the read the batch makes followed by the apply, so the two paths read the same events");
  }
}
