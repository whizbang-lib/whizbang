using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage-focused tests for PerspectiveRunnerRegistryGenerator targeting: the collective-event
/// side-channel's own attribute/parameter guards (a coincidentally-attributed method, a
/// zero-parameter <c>[CollectiveApplyFor]</c> method), the perspective-shape guards (an abstract
/// class, a class implementing an unrelated interface, a model missing <c>[StreamId]</c>), and the
/// <c>IGlobalPerspectiveFor</c> (multi-stream) event-type extraction loop, which no existing test
/// exercises at all.
/// Complements PerspectiveRunnerRegistryGeneratorTests.cs.
/// </summary>
/// <tests>src/Whizbang.Generators/PerspectiveRunnerRegistryGenerator.cs</tests>
public class PerspectiveRunnerRegistryGeneratorCoverageTests {

  #region Collective-event side channel guards

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_CollectiveHandlerMethodMissingAttribute_EventNotIncludedInAllEventTypesAsync() {
    // A method carrying some unrelated attribute still satisfies the generator's cheap syntactic
    // predicate (AttributeLists.Count > 0). If the semantic attribute-name check were skipped or
    // broken, the parameter type of an unrelated, [Obsolete]-marked method would leak into
    // _allEventTypes — meaning the polymorphic deserializer would advertise an event type nothing
    // ever applies, silently bloating the envelope set.
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record RealEvent : IEvent {
    [StreamId]
    public Guid Id { get; init; }
  }

  public record RealModel {
    [StreamId]
    public Guid Id { get; init; }
  }

  public class RealPerspective : IPerspectiveFor<RealModel, RealEvent> {
    public RealModel Apply(RealModel currentData, RealEvent @event) => currentData;
  }

  public record IgnoredEvent : IEvent {
    [StreamId]
    public Guid Id { get; init; }
  }

  public class UnrelatedHandler {
    // Has an attribute (so the syntactic predicate matches), but it isn't [CollectiveApplyFor].
    [Obsolete]
    public void NotACollectiveHandler(IgnoredEvent e) { }
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerRegistryGenerator>(source);

    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRunnerRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();
    await Assert.That(registrySource!).Contains("typeof(global::TestNamespace.RealEvent)")
      .Because("The real perspective's event must still be registered normally.");
    await Assert.That(registrySource).DoesNotContain("typeof(global::TestNamespace.IgnoredEvent)")
      .Because("A method whose only attribute is unrelated to [CollectiveApplyFor] must not surface its " +
        "parameter type as a collective event — only genuinely attributed handlers may do that.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_CollectiveHandlerMethodWithZeroParameters_DoesNotCrashGenerationAsync() {
    // A [CollectiveApplyFor] method with no parameters has no event type to extract at all. The
    // generator must reject it quietly (no event type contributed) rather than throwing on
    // Parameters[0], which would abort generation for the whole compilation — including perspectives
    // that have nothing to do with this handler.
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record RealEvent : IEvent {
    [StreamId]
    public Guid Id { get; init; }
  }

  public record RealModel {
    [StreamId]
    public Guid Id { get; init; }
  }

  public class RealPerspective : IPerspectiveFor<RealModel, RealEvent> {
    public RealModel Apply(RealModel currentData, RealEvent @event) => currentData;
  }

  public class ZeroParamHandler {
    [CollectiveApplyFor]
    public void NoParameters() { }
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerRegistryGenerator>(source);

    await Assert.That(result.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)).IsFalse()
      .Because("A zero-parameter [CollectiveApplyFor] method must not abort generation for the whole compilation.");
    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRunnerRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();
    await Assert.That(registrySource!).Contains("typeof(global::TestNamespace.RealEvent)")
      .Because("Generation of unrelated perspectives must proceed normally despite the malformed handler.");
  }

  #endregion

  #region Perspective-shape guards

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_AbstractPerspectiveClass_ExcludedFromRegistryAsync() {
    // An abstract class can never be constructed by the DI container, so a runner registration
    // pointing at it would fail to resolve at runtime. The generator must skip it rather than
    // emitting a GetRequiredService<T>() call for a type nothing can instantiate.
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record EventA : IEvent {
    [StreamId]
    public Guid Id { get; init; }
  }

  public record EventB : IEvent {
    [StreamId]
    public Guid Id { get; init; }
  }

  public record Model {
    [StreamId]
    public Guid Id { get; init; }
  }

  public abstract class AbstractPerspective : IPerspectiveFor<Model, EventA> {
    public abstract Model Apply(Model currentData, EventA @event);
  }

  public class ConcretePerspective : IPerspectiveFor<Model, EventB> {
    public Model Apply(Model currentData, EventB @event) => currentData;
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerRegistryGenerator>(source);

    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRunnerRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();
    await Assert.That(registrySource!).Contains("\"TestNamespace.ConcretePerspective\"")
      .Because("The concrete perspective must still be registered.");
    await Assert.That(registrySource).DoesNotContain("\"TestNamespace.AbstractPerspective\" =>")
      .Because("An abstract perspective class cannot be resolved via GetRequiredService<T>() and must be excluded.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ClassImplementingUnrelatedInterface_NotTreatedAsPerspectiveAsync() {
    // A class can implement some unrelated interface (satisfying the generator's cheap
    // BaseList.Types.Count > 0 syntactic filter) without being a perspective at all. The semantic
    // check must fall through cleanly instead of mis-registering it.
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record RealEvent : IEvent {
    [StreamId]
    public Guid Id { get; init; }
  }

  public record RealModel {
    [StreamId]
    public Guid Id { get; init; }
  }

  // Implements IDisposable (so BaseList.Types.Count > 0), but no perspective interface at all.
  public class NotAPerspective : IDisposable {
    public void Dispose() { }
  }

  public class RealPerspective : IPerspectiveFor<RealModel, RealEvent> {
    public RealModel Apply(RealModel currentData, RealEvent @event) => currentData;
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerRegistryGenerator>(source);

    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRunnerRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();
    await Assert.That(registrySource!).Contains("\"TestNamespace.RealPerspective\"")
      .Because("The genuine perspective must still be registered.");
    await Assert.That(registrySource).DoesNotContain("\"TestNamespace.NotAPerspective\" =>")
      .Because("A class implementing an unrelated interface must never be mistaken for a perspective.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ModelWithoutStreamIdAttribute_PerspectiveSkippedSilentlyAsync() {
    // Without a [StreamId] property on the model, the generator has no key to route
    // GetRunner(perspectiveName, ...) lookups by, so it must skip the perspective rather than
    // emitting a runner it can never correctly key.
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrphanEvent : IEvent {
    [StreamId]
    public Guid Id { get; init; }
  }

  // No [StreamId] property anywhere on this model.
  public record ModelWithoutStreamId {
    public Guid Id { get; init; }
  }

  public class OrphanPerspective : IPerspectiveFor<ModelWithoutStreamId, OrphanEvent> {
    public ModelWithoutStreamId Apply(ModelWithoutStreamId currentData, OrphanEvent @event) => currentData;
  }

  public record ValidEvent : IEvent {
    [StreamId]
    public Guid Id { get; init; }
  }

  public record ValidModel {
    [StreamId]
    public Guid Id { get; init; }
  }

  public class ValidPerspective : IPerspectiveFor<ValidModel, ValidEvent> {
    public ValidModel Apply(ValidModel currentData, ValidEvent @event) => currentData;
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerRegistryGenerator>(source);

    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRunnerRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();
    await Assert.That(registrySource!).Contains("\"TestNamespace.ValidPerspective\"")
      .Because("A perspective whose model carries [StreamId] must still be registered.");
    await Assert.That(registrySource).DoesNotContain("\"TestNamespace.OrphanPerspective\" =>")
      .Because("A perspective whose model lacks [StreamId] cannot be keyed for lookup and must be skipped.");
  }

  #endregion

  #region IGlobalPerspectiveFor (multi-stream) event-type extraction

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_GlobalMultiStreamPerspective_EventTypesExtractedFromIndexTwoAsync() {
    // IGlobalPerspectiveFor<TModel, TPartitionKey, TEvent1, TEvent2, ...> puts event types starting
    // at index 2 (TModel is 0, TPartitionKey is 1). If this loop regressed to start at index 0 or 1,
    // a multi-stream perspective's events would be missing from GetEventTypes() (breaking the
    // __collective__/lifecycle polymorphic deserialization path), or the model/partition-key types
    // would be wrongly advertised as events.
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record StreamEventA : IEvent {
    [StreamId]
    public Guid Id { get; init; }
  }

  public record StreamEventB : IEvent {
    [StreamId]
    public Guid Id { get; init; }
  }

  public record MultiStreamModel {
    [StreamId]
    public Guid Id { get; init; }
  }

  public class MultiStreamPerspective : IGlobalPerspectiveFor<MultiStreamModel, Guid, StreamEventA, StreamEventB> {
    public Guid GetPartitionKey(StreamEventA eventData) => eventData.Id;
    public Guid GetPartitionKey(StreamEventB eventData) => eventData.Id;
    public MultiStreamModel Apply(MultiStreamModel currentData, StreamEventA eventData) => currentData;
    public MultiStreamModel Apply(MultiStreamModel currentData, StreamEventB eventData) => currentData;
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerRegistryGenerator>(source);

    var registrySource = GeneratorTestHelper.GetGeneratedSource(result, "PerspectiveRunnerRegistry.g.cs");
    await Assert.That(registrySource).IsNotNull();
    await Assert.That(registrySource!).Contains("\"TestNamespace.MultiStreamPerspective\"")
      .Because("A multi-stream (IGlobalPerspectiveFor) perspective must be discovered and registered just like a single-stream one.");

    var allEventTypesSection = registrySource[
        registrySource.IndexOf("_allEventTypes", StringComparison.Ordinal)..registrySource.IndexOf("public IReadOnlyList<Type> GetEventTypes()", StringComparison.Ordinal)];

    await Assert.That(allEventTypesSection).Contains("typeof(global::TestNamespace.StreamEventA)")
      .Because("The first event type argument (index 2) must be extracted.");
    await Assert.That(allEventTypesSection).Contains("typeof(global::TestNamespace.StreamEventB)")
      .Because("The second event type argument (index 3) must also be extracted — the loop must not stop after one iteration.");
    await Assert.That(allEventTypesSection).DoesNotContain("typeof(global::TestNamespace.MultiStreamModel)")
      .Because("TModel (index 0) must never be treated as an event type.");
    await Assert.That(allEventTypesSection).DoesNotContain("typeof(global::System.Guid)")
      .Because("TPartitionKey (index 1) must never be treated as an event type — the loop must start at index 2, not 0 or 1.");
  }

  #endregion
}
