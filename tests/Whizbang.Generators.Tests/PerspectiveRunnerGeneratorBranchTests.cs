// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The perspective runner's less common shapes: a model that is not a named type, an Apply the
/// generator cannot see by name, a value-type member default written as a literal, and a vector
/// field stripped into a copy.
/// </summary>
/// <tests>src/Whizbang.Generators/PerspectiveRunnerGenerator.cs</tests>
[Category("SourceGenerators")]
public class PerspectiveRunnerGeneratorBranchTests {
  private const string REGISTER = "global::Whizbang.Core.Perspectives.PerspectiveMemberDefaultRegistry.Register(";

  private static string _runner(string source, string perspective) =>
    GeneratorTestHelper.GetGeneratedSource(GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source), $"{perspective}Runner.g.cs") ?? "";

  /// <summary>
  /// An array satisfies the model's <c>class</c> constraint but declares no properties, so it can
  /// never carry the <c>[StreamId]</c> a runner keys on: the perspective is reported (WHIZ033) and
  /// gets no runner. This is the contract the generator relies on when it reads every model past
  /// that check as a named type.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task ArrayModel_IsReportedAndGetsNoRunnerAsync() {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>("""
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestNamespace;

      public record TagsChanged : IEvent {
        [StreamId] public Guid Id { get; init; }
      }

      public class TagsPerspective : IPerspectiveFor<string[], TagsChanged> {
        public string[] Apply(string[] currentData, TagsChanged @event) => currentData;
      }
      """);

    await Assert.That(result.Diagnostics.Select(d => d.Id)).Contains("WHIZ033");
    await Assert.That(GeneratorTestHelper.GetGeneratedSource(result, "TagsPerspectiveRunner.g.cs")).IsNull();
  }

  /// <summary>
  /// An event that is a type parameter has no members and no base chain to walk, so the runner has
  /// no stream id for it, and generates without faulting.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task TypeParameterEvent_HasNoStreamIdAndDoesNotFaultAsync() {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>("""
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestNamespace;

      public record OrderModel {
        [StreamId] public Guid Id { get; init; }
      }

      public class GenericPerspective<TEvent> : IPerspectiveFor<OrderModel, TEvent> where TEvent : IEvent {
        public OrderModel Apply(OrderModel currentData, TEvent @event) => currentData;
      }
      """);

    await Assert.That(result.Results.All(r => r.Exception is null)).IsTrue();
    var runner = GeneratorTestHelper.GetGeneratedSource(result, "GenericPerspectiveRunner.g.cs");
    await Assert.That(runner).IsNotNull();
  }

  /// <summary>
  /// An Apply implemented explicitly is not found by its name, so the generator has no return
  /// shape for its event and uses the default: the method returns the model.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task ExplicitlyImplementedApply_IsTreatedAsReturningTheModelAsync() {
    var runner = _runner("""
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestNamespace;

      public record OrderCreated : IEvent {
        [StreamId] public Guid Id { get; init; }
      }

      public record OrderModel {
        [StreamId] public Guid Id { get; init; }
      }

      public class ExplicitPerspective : IPerspectiveFor<OrderModel, OrderCreated> {
        OrderModel IPerspectiveFor<OrderModel, OrderCreated>.Apply(OrderModel currentData, OrderCreated @event) => currentData;
      }
      """, "ExplicitPerspective");

    await Assert.That(runner).Contains(
      "return (perspective.Apply(currentModel!, typedEvent), global::Whizbang.Core.Perspectives.ModelAction.None);");
  }

  /// <summary>
  /// When some Apply methods are visible and one is explicit, the visible ones keep their shape
  /// and the explicit one falls back to returning the model.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task MixedApplyShapes_KeepTheVisibleShapeAndDefaultTheHiddenOneAsync() {
    var runner = _runner("""
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestNamespace;

      public record OrderCreated : IEvent {
        [StreamId] public Guid Id { get; init; }
      }

      public record OrderDeleted : IEvent {
        [StreamId] public Guid Id { get; init; }
      }

      public record OrderModel {
        [StreamId] public Guid Id { get; init; }
      }

      public class MixedPerspective : IPerspectiveFor<OrderModel, OrderCreated, OrderDeleted> {
        OrderModel IPerspectiveFor<OrderModel, OrderCreated>.Apply(OrderModel currentData, OrderCreated @event) => currentData;
        public ModelAction Apply(OrderModel currentData, OrderDeleted @event) => ModelAction.Delete;
      }
      """, "MixedPerspective");

    var createdAt = runner.IndexOf("case global::TestNamespace.OrderCreated typedEvent:", StringComparison.Ordinal);
    var deletedAt = runner.IndexOf("case global::TestNamespace.OrderDeleted typedEvent:", StringComparison.Ordinal);
    await Assert.That(createdAt).IsGreaterThanOrEqualTo(0);
    await Assert.That(deletedAt).IsGreaterThan(createdAt);
    var created = runner[createdAt..deletedAt];
    var deleted = runner[deletedAt..];

    await Assert.That(deleted).Contains("return (currentModel, perspective.Apply(currentModel!, typedEvent));")
      .Because("the visible Apply returns an action, so the current model is kept and its action returned");
    await Assert.That(created).Contains(
      "return (perspective.Apply(currentModel!, typedEvent), global::Whizbang.Core.Perspectives.ModelAction.None);");
  }

  /// <summary>
  /// A value-type member initialized with a literal registers the literal cast to the member's own
  /// type, so it boxes as that type rather than as the literal's.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task ValueTypeMemberWithLiteralDefault_RegistersTheCastLiteralAsync() {
    var runner = _runner("""
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestNamespace;

      public record CounterBumped : IEvent {
        [StreamId] public Guid Id { get; init; }
      }

      public record CounterModel {
        [StreamId] public Guid Id { get; init; }
        public long Count { get; init; } = 5;
      }

      public class CounterPerspective : IPerspectiveFor<CounterModel, CounterBumped> {
        public CounterModel Apply(CounterModel currentData, CounterBumped @event) => currentData;
      }
      """, "CounterPerspective");

    await Assert.That(runner).Contains(REGISTER + "typeof(global::TestNamespace.CounterModel), \"Count\", (long)5)");
  }

  /// <summary>
  /// A Split class with an init-only promoted field is stripped into a copy; a vector field in it
  /// is stripped to an empty array rather than to <c>default!</c>.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task SplitCopyWithVectorField_StripsTheVectorToAnEmptyArrayAsync() {
    var runner = _runner("""
      #nullable enable
      using System;
      using Whizbang.Core;
      using Whizbang.Core.Perspectives;

      namespace TestNamespace;

      public class DocEvent : IEvent {
        public Guid Id { get; set; }
      }

      [PerspectiveStorage(FieldStorageMode.Split)]
      public class DocModel {
        [StreamId]
        public Guid Id { get; set; }

        [PhysicalField]
        public string Status { get; init; } = "";

        [VectorField(3)]
        public float[]? Embedding { get; set; }
      }

      public class DocPerspective : IPerspectiveFor<DocModel, DocEvent> {
        public DocModel Apply(DocModel currentData, DocEvent @event) => currentData;
      }
      """, "DocPerspective");

    await Assert.That(runner).Contains("model = new global::TestNamespace.DocModel {");
    await Assert.That(runner).Contains("Embedding = System.Array.Empty<float>()");
    await Assert.That(runner).Contains("Status = default!");
  }
}
