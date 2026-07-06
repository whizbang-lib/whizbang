using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for PerspectiveInvokerGenerator.
/// Verifies invoker generation includes IPerspectiveWithActionsFor perspectives.
/// </summary>
[Category("SourceGenerators")]
public class PerspectiveInvokerGeneratorTests {

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_IPerspectiveWithActionsFor_GeneratesInvokerAsync() {
    // Arrange — Perspective using only IPerspectiveWithActionsFor
    const string source = """
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestApp;

public record DeletedEvent : IEvent {
  [StreamId]
  public Guid Id { get; init; }
}

public record OrderModel {
  [StreamId]
  public Guid Id { get; init; }
}

public class OrderPurgePerspective : IPerspectiveWithActionsFor<OrderModel, DeletedEvent> {
  public ApplyResult<OrderModel> Apply(OrderModel current, DeletedEvent @event)
    => ApplyResult<OrderModel>.Purge();
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveInvokerGenerator>(source);

    // Assert — Invoker must be generated for WithActionsFor perspective
    var code = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveInvoker.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code).Contains("DeletedEvent")
      .Because("IPerspectiveWithActionsFor events must be included in perspective invoker");
  }

  /// <summary>
  /// A class with base types but NO perspective interface must be skipped
  /// (early null in _extractPerspectiveInfo — the "no IPerspectiveBase" arm), and the generator
  /// must fall through to the empty-invoker path (_generateEmptyInvoker).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_NoPerspectives_GeneratesEmptyInvokerAsync() {
    // Arrange — a class that satisfies the syntactic predicate (has a base list)
    // but implements no perspective interface at all.
    const string source = """
using System;

namespace TestApp;

public interface IMarker { }

public class PlainService : IMarker {
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveInvokerGenerator>(source);

    // Assert — empty invoker still produced with the "no perspectives" routing marker
    var code = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveInvoker.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("No perspectives discovered")
      .Because("with no perspectives, _generateEmptyInvoker emits the empty-routing marker");
    await Assert.That(code).Contains("TestAssembly.Generated")
      .Because("the empty invoker uses the assembly-specific namespace");
  }

  /// <summary>
  /// An abstract perspective class must be skipped (IsAbstract early-null arm in _extractPerspectiveInfo),
  /// which — when it is the only perspective — leaves the generator on the empty-invoker path.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_AbstractPerspective_SkippedAsync() {
    // Arrange — abstract class implementing a perspective interface; must be ignored.
    const string source = """
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestApp;

public record AbstractEvent : IEvent {
  [StreamId]
  public Guid Id { get; init; }
}

public class AbstractModel {
  [StreamId]
  public Guid Id { get; init; }
}

public abstract class AbstractPerspective : IPerspectiveFor<AbstractModel, AbstractEvent> {
  public abstract AbstractModel Apply(AbstractModel current, AbstractEvent @event);
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveInvokerGenerator>(source);

    // Assert — abstract class ignored, so the empty-invoker path is taken.
    var code = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveInvoker.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("No perspectives discovered")
      .Because("abstract perspectives are not instantiable and must be excluded from routing");
  }

  /// <summary>
  /// A class implementing ONLY the marker base <c>IPerspectiveFor&lt;TModel&gt;</c> (single type
  /// argument, no event) must be skipped — the "only marker interface" arm of _extractPerspectiveInfo.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_MarkerOnlyPerspective_SkippedAsync() {
    // Arrange — implements the 1-arg marker IPerspectiveFor<TModel> but no TEvent variant.
    const string source = """
using Whizbang.Core.Perspectives;
using System;

namespace TestApp;

public class MarkerModel {
  public Guid Id { get; init; }
}

public class MarkerOnlyPerspective : IPerspectiveFor<MarkerModel> {
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveInvokerGenerator>(source);

    // Assert — no event-bearing perspective, so empty invoker is generated.
    var code = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveInvoker.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("No perspectives discovered")
      .Because("a class implementing only the marker IPerspectiveFor<TModel> exposes no events to route");
  }
}
