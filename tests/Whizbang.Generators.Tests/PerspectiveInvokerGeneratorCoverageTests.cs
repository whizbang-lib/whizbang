using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage-focused test for <see cref="PerspectiveInvokerGenerator"/>, complementing
/// <c>tests/Whizbang.Generators.Tests/PerspectiveInvokerGeneratorTests.cs</c>. That file's
/// <c>Generator_MarkerOnlyPerspective_SkippedAsync</c> implements <c>IPerspectiveFor&lt;TModel&gt;</c>
/// (the 1-arg marker interface), but that interface does not itself extend
/// <c>IPerspectiveBase&lt;TModel&gt;</c> — so the class's <c>AllInterfaces</c> contains no
/// <c>IPerspectiveBase</c>-named interface at all, and it actually exercises the earlier
/// "<c>perspectiveInterfaces.Count == 0</c>" arm, not the "only the marker interface, no TEvent
/// variant" arm a few lines later. This test reaches that later arm directly, by implementing the
/// base marker <c>IPerspectiveBase&lt;TModel&gt;</c> itself (which <c>IPerspectiveFor&lt;TModel,
/// TEvent&gt;</c> does extend, but the 1-arg form of it does not).
/// </summary>
[Category("SourceGenerators")]
public class PerspectiveInvokerGeneratorCoverageTests {

  /// <summary>
  /// <c>IPerspectiveBase&lt;TModel&gt;</c> is documented "do not implement directly," but nothing
  /// stops a consumer from doing it (it is public, and only IDE-hidden via
  /// <c>[EditorBrowsable(Never)]</c>). If the "no TEvent-bearing interface" guard regressed,
  /// <c>perspectiveInterface</c> would stay null and the very next line
  /// (<c>perspectiveInterface.TypeArguments</c>) would throw a <c>NullReferenceException</c> and
  /// crash the generator, instead of the class being silently excluded from routing.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_DirectMarkerBaseImplementation_SkippedAsync() {
    // Arrange — implements the 1-arg IPerspectiveBase<TModel> marker directly, not through
    // IPerspectiveFor<TModel, TEvent> (which is what actually carries the TEvent-bearing interface).
    const string source = """
using Whizbang.Core.Perspectives;
using System;

namespace TestApp;

public class DirectMarkerModel {
  public Guid Id { get; init; }
}

public class DirectMarkerPerspective : IPerspectiveBase<DirectMarkerModel> {
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<PerspectiveInvokerGenerator>(source);

    // Assert — no event-bearing perspective interface, so the empty-invoker path is taken.
    var code = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveInvoker.g.cs");
    await Assert.That(code).IsNotNull();
    await Assert.That(code!).Contains("No perspectives discovered")
      .Because("a class implementing only the 1-arg IPerspectiveBase<TModel> marker directly exposes no TEvent-bearing interface to route, and must not crash the generator");
  }
}
