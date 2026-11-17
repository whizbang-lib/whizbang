// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// #2330: <c>[RowCap(PerProperty = nameof(Model.JobId), Max = N)]</c> keeps the newest N rows per value of a model
/// property. The generator registers the cap under the key <c>data:&lt;property&gt;</c>, which the sweep partitions by.
/// </summary>
/// <docs>fundamentals/perspectives/row-retention#per-property-caps</docs>
/// <tests>src/Whizbang.Generators/PerspectiveRunnerGenerator.cs</tests>
public class RowCapPerPropertyGeneratorTests {
  private static string _source(string attribute) => $$"""

using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record SnapshotTaken : IEvent {
    public string Id { get; init; } = "";
  }

  public record JobSnapshotModel {
    [StreamId]
    public string Id { get; init; } = "";
    public string JobId { get; init; } = "";
  }

  {{attribute}}
  public class JobSnapshotPerspective : IPerspectiveFor<JobSnapshotModel, SnapshotTaken> {
    public JobSnapshotModel Apply(JobSnapshotModel currentData, SnapshotTaken @event) => currentData;
  }
}
""";

  private static string? _runner(string attribute) =>
    GeneratorTestHelper.GetGeneratedSource(
      GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(_source(attribute)), "JobSnapshotPerspectiveRunner.g.cs");

  [Test]
  public async Task PerProperty_WithMax_RegistersTheDataKey_AndOutranksPerScopeAsync() {
    var runner = _runner("[RowCap(PerProperty = nameof(JobSnapshotModel.JobId), Max = 3, PerScope = 100)]");

    await Assert.That(runner).IsNotNull();
    await Assert.That(runner)
      .Contains("PerspectiveRowCapRegistry.Register(typeof(global::TestNamespace.JobSnapshotModel), 3, \"data:JobId\")");
  }

  [Test]
  public async Task PerProperty_WithoutMax_FallsBackToTheOtherCapsAsync() {
    var withScope = _runner("[RowCap(PerProperty = \"JobId\", PerTenant = 7)]");
    var alone = _runner("[RowCap(PerProperty = \"JobId\")]");
    var blank = _runner("[RowCap(PerProperty = \" \", Max = 3)]");

    await Assert.That(withScope).Contains("PerspectiveRowCapRegistry.Register(typeof(global::TestNamespace.JobSnapshotModel), 7, \"t\")");
    await Assert.That(alone).DoesNotContain("PerspectiveRowCapRegistry.Register")
      .Because("a property with no Max caps nothing");
    await Assert.That(blank).DoesNotContain("PerspectiveRowCapRegistry.Register")
      .Because("a blank property name partitions by nothing, so it is not a per-property cap");
  }
}
