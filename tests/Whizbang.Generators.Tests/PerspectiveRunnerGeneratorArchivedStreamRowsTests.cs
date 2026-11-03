// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The perspective-runner generator carries <c>[ArchivedStreamRows]</c> into a <c>[ModuleInitializer]</c> registration
/// keyed by the perspective's CLR type name (#1739), and every runner's probe for a missing row asks the policy registry
/// under that same name, so a configured policy on a perspective that declares none reaches the probe too.
/// </summary>
/// <docs>fundamentals/events/archive-store#perspective-rows</docs>
/// <code-under-test>src/Whizbang.Generators/PerspectiveRunnerGenerator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/Templates/PerspectiveRunnerTemplate.cs</code-under-test>
public class PerspectiveRunnerGeneratorArchivedStreamRowsTests {
  private const string REGISTER = "global::Whizbang.Core.Archival.ArchivedStreamRowPolicies.Register(";
  private const string PROBE = "global::Whizbang.Core.Archival.ArchivedStreamRowPolicies.RemovesRows(_archivedStreamRowsName)";

  private static string _source(string attribute, string className = "OrderPerspective", string? outer = null) {
    var perspective = $$"""
  {{attribute}}
  public class {{className}} : IPerspectiveFor<OrderModel, OrderPlaced> {
    public OrderModel Apply(OrderModel currentData, OrderPlaced @event) => currentData;
  }
""";
    var body = outer is null ? perspective : $$"""
  public static class {{outer}} {
{{perspective}}
  }
""";
    return $$"""
using System;
using Whizbang.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Attributes;
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

{{body}}
}
""";
  }

  private static string _runner(string source, string runnerName = "OrderPerspectiveRunner.g.cs") {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    return GeneratorTestHelper.GetGeneratedSource(result, runnerName)!;
  }

  [Test]
  [RequiresAssemblyFiles()]
  [Arguments("ArchivedStreamRowPolicy.Keep", 0)]
  [Arguments("ArchivedStreamRowPolicy.Drop", 1)]
  [Arguments("ArchivedStreamRowPolicy.Archive", 2)]
  public async Task ADeclaredPolicy_IsRegisteredUnderThePerspectivesClrTypeNameAsync(string policy, int value) {
    var runner = _runner(_source($"[ArchivedStreamRows({policy})]"));

    await Assert.That(runner).Contains("[global::System.Runtime.CompilerServices.ModuleInitializer]");
    await Assert.That(runner).Contains(
      $"{REGISTER}_archivedStreamRowsName, (global::Whizbang.Core.Archival.ArchivedStreamRowPolicy){value});")
      .Because("an explicit Keep is registered too: it outvotes another perspective of the same model that drops");
    await Assert.That(runner).Contains("private const string _archivedStreamRowsName = \"TestNamespace.OrderPerspective\";")
      .Because("cursors and snapshots are keyed by the perspective's CLR type name, and the SQL matches on that form");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task APerspectiveThatDeclaresNothing_RegistersNothing_ButItsProbeStillAsksTheRegistryAsync() {
    var runner = _runner(_source(attribute: ""));

    await Assert.That(runner).DoesNotContain(REGISTER)
      .Because("absent stays distinct from a declared Keep");
    await Assert.That(runner).Contains(PROBE)
      .Because("configuration can give any perspective a policy, so every runner's probe asks for it by name");
    await Assert.That(runner).Contains("private const string _archivedStreamRowsName = \"TestNamespace.OrderPerspective\";");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task TheProbe_AsksTheRegistryBesideTheRowRetentionCheckAsync() {
    var runner = _runner(_source("[ArchivedStreamRows(ArchivedStreamRowPolicy.Drop)]"));

    const string canEvict = "(global::Whizbang.Core.Perspectives.PerspectiveRowEviction.CanEvict(typeof(global::TestNamespace.OrderModel))";
    var evictionCheck = runner.IndexOf(canEvict, StringComparison.Ordinal);
    var between = runner[(evictionCheck + canEvict.Length)..runner.IndexOf(PROBE, StringComparison.Ordinal)];

    await Assert.That(evictionCheck).IsGreaterThan(0);
    await Assert.That(between.Trim()).IsEqualTo("||")
      .Because("a row an archive removed is missing exactly like an evicted one, and is rebuilt the same way");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ANestedPerspective_IsNamedInItsClrFormAsync() {
    var runner = _runner(_source("[ArchivedStreamRows(ArchivedStreamRowPolicy.Archive)]", "InnerPerspective", outer: "Outer"),
      "InnerPerspectiveRunner.g.cs");

    await Assert.That(runner).Contains("private const string _archivedStreamRowsName = \"TestNamespace.Outer+InnerPerspective\";");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task TheAttribute_WrittenByItsFullName_IsReadTooAsync() {
    var runner = _runner(_source(
      "[Whizbang.Core.Attributes.ArchivedStreamRowsAttribute(Whizbang.Core.Archival.ArchivedStreamRowPolicy.Drop)]"));

    await Assert.That(runner).Contains($"{REGISTER}_archivedStreamRowsName, (global::Whizbang.Core.Archival.ArchivedStreamRowPolicy)1);");
  }
}
