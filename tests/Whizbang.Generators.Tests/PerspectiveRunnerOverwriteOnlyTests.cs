// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The runner of a perspective marked <c>[OverwriteOnly]</c> knows it is overwrite-only and which of its events can decide
/// the row alone (#1869): the ones whose Apply returns the model. The marker is the developer's: a perspective whose
/// Applies happen to ignore their model is not treated as overwrite-only without it (owner decision, 2026-10-29).
/// </summary>
/// <docs>fundamentals/perspectives/rewind#overwrite-only</docs>
[Category("Generators")]
public class PerspectiveRunnerOverwriteOnlyTests {
  private const string TEMPLATE = """
#nullable enable
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record GrantedEvent : IEvent {
    [StreamId] public Guid Id { get; init; }
  }

  public record NotedEvent : IEvent {
    [StreamId] public Guid Id { get; init; }
  }

  public record RevokedEvent : IEvent {
    [StreamId] public Guid Id { get; init; }
  }

  public record GrantsModel {
    [StreamId] public Guid Id { get; init; }
  }

  __MARKER__
  public class GrantsPerspective :
      IPerspectiveFor<GrantsModel, GrantedEvent, NotedEvent>,
      IPerspectiveWithActionsFor<GrantsModel, RevokedEvent> {
    public GrantsModel Apply(GrantsModel currentData, GrantedEvent @event) => new() { Id = @event.Id };

    public GrantsModel? Apply(GrantsModel currentData, NotedEvent @event) => null;

    public ApplyResult<GrantsModel> Apply(GrantsModel currentData, RevokedEvent @event) => ApplyResult<GrantsModel>.Purge();
  }
}
""";

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AMarkedPerspective_IsOverwriteOnly_AndItsModelReturningAppliesDecideTheRowAloneAsync() {
    var runner = _runnerFor("[OverwriteOnly]");

    await Assert.That(runner).Contains("private static readonly bool _isOverwriteOnly = true;");
    await Assert.That(runner).Contains(
      "DecidesTheRowAlone(global::Whizbang.Core.IEvent @event) => @event is global::TestNamespace.GrantedEvent or global::TestNamespace.NotedEvent;")
      .Because("an Apply that returns the model (or null for no change) builds the row from its event; one returning an ApplyResult can purge");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AnUnmarkedPerspective_IsNotOverwriteOnly_EvenWhenNoApplyReadsItsModelAsync() {
    var runner = _runnerFor("");

    await Assert.That(runner).Contains("private static readonly bool _isOverwriteOnly = false;")
      .Because("the marker is explicit; the generator never infers it (owner decision, 2026-10-29)");
    await Assert.That(runner).Contains("DecidesTheRowAlone(global::Whizbang.Core.IEvent @event) => false;");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AMarkedPerspectiveWhoseAppliesAllReturnActions_DecidesNothingAloneAsync() {
    var source = TEMPLATE
      .Replace("__MARKER__", "[OverwriteOnly]")
      .Replace("public GrantsModel Apply(GrantsModel currentData, GrantedEvent @event) => new() { Id = @event.Id };",
        "public (GrantsModel?, ModelAction) Apply(GrantsModel currentData, GrantedEvent @event) => (new() { Id = @event.Id }, ModelAction.None);")
      .Replace("public GrantsModel? Apply(GrantsModel currentData, NotedEvent @event) => null;",
        "public ModelAction Apply(GrantsModel currentData, NotedEvent @event) => ModelAction.None;");
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    var runner = GeneratorTestHelper.GetGeneratedSource(result, "GrantsPerspectiveRunner.g.cs")!;

    await Assert.That(runner).Contains("private static readonly bool _isOverwriteOnly = true;");
    await Assert.That(runner).Contains("DecidesTheRowAlone(global::Whizbang.Core.IEvent @event) => false;")
      .Because("with no Apply that returns the model, every rewind folds the stream in full");
  }

  private static string _runnerFor(string marker) {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(TEMPLATE.Replace("__MARKER__", marker));
    return GeneratorTestHelper.GetGeneratedSource(result, "GrantsPerspectiveRunner.g.cs")!;
  }
}
