using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage-focused tests for PerspectiveRunnerGenerator targeting: StreamGroup membership
/// parsing (an empty key, a key containing the '|' delimiter, and the Announce named argument),
/// the Guid?/unsupported-type branches of the [StreamId] key-init expression builder, and the
/// Apply-overload return-type classifier's defensive skips (too-few parameters, an event not
/// declared by any implemented interface, and a 2-tuple whose second element isn't ModelAction).
/// Complements PerspectiveRunnerGeneratorTests.cs.
/// </summary>
/// <tests>src/Whizbang.Generators/PerspectiveRunnerGenerator.cs</tests>
public class PerspectiveRunnerGeneratorCoverageTests {

  #region StreamGroup membership parsing

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_StreamGroupWithEmptyKey_SkippedButOtherMembershipsStillRegisterAsync() {
    // An empty StreamGroup key can never match a real group key elsewhere in the service, so it
    // must never reach the spec string (or the registry) at all. If it silently became a group of
    // its own, every perspective that forgot to fill in a key would start evicting together under
    // a shared "" bucket instead of the omission being harmless.
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record EmptyKeyEvent : IEvent {
    public string Id { get; init; } = "";
  }

  public record EmptyKeyModel {
    [StreamId]
    public string Id { get; init; } = "";
  }

  [StreamGroup("")]
  [StreamGroup("kept")]
  public class EmptyKeyPerspective : IPerspectiveFor<EmptyKeyModel, EmptyKeyEvent> {
    public EmptyKeyModel Apply(EmptyKeyModel currentData, EmptyKeyEvent @event) => currentData;
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "EmptyKeyPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource!)
      .Contains("PerspectiveStreamGroupRegistry.Register(typeof(global::TestNamespace.EmptyKeyModel), \"kept\", true, true, false)")
      .Because("the membership with a real key must still register normally.");
    await Assert.That(runnerSource!)
      .DoesNotContain("Register(typeof(global::TestNamespace.EmptyKeyModel), \"\",")
      .Because("an empty StreamGroup key must never reach the registry as a group of its own.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_StreamGroupAnnounceFalse_RegistersAnnounceOffAsync() {
    // Announce=false must reach the registry as an explicit "false" — a satellite perspective
    // that should only ever follow (never broadcast its OWN evictions) must not silently inherit
    // the "announce on" default and start broadcasting anyway.
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record SatelliteEvent : IEvent {
    public string ThreadId { get; init; } = "";
  }

  public record SatelliteModel {
    [StreamId]
    public string ThreadId { get; init; } = "";
  }

  [StreamGroup("satellite-group", Announce = false)]
  public class SatellitePerspective : IPerspectiveFor<SatelliteModel, SatelliteEvent> {
    public SatelliteModel Apply(SatelliteModel currentData, SatelliteEvent @event) => currentData;
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "SatellitePerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource!)
      .Contains("PerspectiveStreamGroupRegistry.Register(typeof(global::TestNamespace.SatelliteModel), \"satellite-group\", false, true, false)")
      .Because("Announce=false must be read from the named argument, not left at the true default.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_StreamGroupKeyContainingPipe_SilentlyDropsThatMembershipAsync() {
    // Each membership is encoded as "key|announce|follow|bridge" and later re-split on '|'. A key
    // that itself contains '|' desyncs that split (5 parts instead of 4), so the whole membership
    // is dropped with NO diagnostic — a perspective can believe it joined a group and silently
    // never evict with it. This documents that current (dangerous) behavior; a well-formed
    // sibling membership must still register, proving the drop is isolated to the bad key and not
    // a crash of the whole registration pass.
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record PipeKeyEvent : IEvent {
    public string ThreadId { get; init; } = "";
  }

  public record PipeKeyModel {
    [StreamId]
    public string ThreadId { get; init; } = "";
  }

  [StreamGroup("safe")]
  [StreamGroup("weird|key")]
  public class PipeKeyPerspective : IPerspectiveFor<PipeKeyModel, PipeKeyEvent> {
    public PipeKeyModel Apply(PipeKeyModel currentData, PipeKeyEvent @event) => currentData;
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "PipeKeyPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource!)
      .Contains("PerspectiveStreamGroupRegistry.Register(typeof(global::TestNamespace.PipeKeyModel), \"safe\", true, true, false)")
      .Because("the well-formed sibling membership must still register.");
    await Assert.That(runnerSource!).DoesNotContain("weird")
      .Because("a '|' inside the key desyncs the pipe-delimited encoding, so the membership is silently dropped instead of registered or diagnosed.");
  }

  #endregion

  #region [StreamId] key-init expression builder

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_NullableGuidStreamKey_AssignsStreamIdDirectlyAsync() {
    // A Guid? stream key must be assigned the raw streamId (Guid widens implicitly to Guid?) just
    // like a plain Guid. If this regressed to falling through to the unsupported-type path, every
    // freshly created model would silently start life with its key unset.
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record NullableKeyEvent : IEvent {
    public Guid WidgetId { get; init; }
  }

  public record NullableKeyModel {
    [StreamId]
    public Guid? WidgetId { get; init; }
    public string Status { get; init; } = "";
  }

  public class NullableKeyPerspective : IPerspectiveFor<NullableKeyModel, NullableKeyEvent> {
    public NullableKeyModel Apply(NullableKeyModel currentData, NullableKeyEvent @event) => currentData;
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "NullableKeyPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource!).Contains("new global::TestNamespace.NullableKeyModel { WidgetId = streamId }")
      .Because("Guid? must be initialized the same direct way as Guid — no reflection, no unset key.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_UnsupportedStreamKeyType_LeavesKeyUnsetInEmptyModelAsync() {
    // An int-typed [StreamId] property has no safe conversion from the runner's Guid streamId (no
    // Guid/Guid?/string match, no static From(Guid) factory), so CreateEmptyModel must leave it
    // out of the object initializer entirely rather than guess a value or throw at generation
    // time.
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record SequenceKeyEvent : IEvent {
    public int Sequence { get; init; }
  }

  public record SequenceKeyModel {
    [StreamId]
    public int Sequence { get; init; }
    public string Status { get; init; } = "";
  }

  public class SequenceKeyPerspective : IPerspectiveFor<SequenceKeyModel, SequenceKeyEvent> {
    public SequenceKeyModel Apply(SequenceKeyModel currentData, SequenceKeyEvent @event) => currentData;
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "SequenceKeyPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource!).Contains("new global::TestNamespace.SequenceKeyModel { }")
      .Because("no supported conversion exists from Guid to int, so the key must be left unset rather than guessed.");
    // Scoped to the initializer: the bare identifier appears elsewhere in the generated file
    // (prose in comments, unrelated members), so forbidding it outright fails for the wrong reason.
    await Assert.That(runnerSource!).DoesNotContain("SequenceKeyModel { Sequence =")
      .Because("guessing a value for a key the generator cannot convert would silently write every "
             + "row under the wrong identity, which reads as data loss rather than a codegen bug.");
  }

  #endregion

  #region Apply-overload return-type classifier

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_ApplyOverloadWithTooFewParameters_IsSkippedWithoutCrashingAsync() {
    // Apply methods are matched by NAME only, so a perspective may carry an unrelated helper
    // method also called "Apply" (e.g. a fluent builder-style helper). Return-type classification
    // must skip anything with fewer than the (model, event) parameters instead of indexing into
    // Parameters[1] and crashing generation for the whole compilation.
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record HelperEvent : IEvent {
    public string Id { get; init; } = "";
  }

  public record HelperModel {
    [StreamId]
    public string Id { get; init; } = "";
  }

  public class HelperPerspective : IPerspectiveFor<HelperModel, HelperEvent> {
    // Unrelated overload sharing the "Apply" name but not the (model, event) shape.
    public HelperModel Apply(HelperModel currentData) => currentData;

    public HelperModel Apply(HelperModel currentData, HelperEvent @event) => currentData;
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "HelperPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource!)
      .Contains("return (perspective.Apply(currentModel!, typedEvent), global::Whizbang.Core.Perspectives.ModelAction.None);")
      .Because("the real (model, event) overload must still classify and generate normally despite the unrelated single-parameter overload sharing its name.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_ApplyOverloadForUndeclaredEventType_IsSkippedWithoutLeakingIntoCodegenAsync() {
    // A class can carry an Apply overload for an event it doesn't list in any IPerspectiveFor<...>
    // interface (e.g. left over from a refactor, or shared with a sibling perspective through a
    // common base class). Return-type classification must ignore it: it must not appear in the
    // generated switch, and it must not stop the declared event's own case from generating.
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record DeclaredEvent : IEvent {
    public string Id { get; init; } = "";
  }

  public record UndeclaredEvent : IEvent {
    public string Id { get; init; } = "";
  }

  public record LeftoverModel {
    [StreamId]
    public string Id { get; init; } = "";
  }

  public class LeftoverPerspective : IPerspectiveFor<LeftoverModel, DeclaredEvent> {
    public LeftoverModel Apply(LeftoverModel currentData, DeclaredEvent @event) => currentData;

    // Not part of any IPerspectiveFor<...> interface on this class.
    public LeftoverModel Apply(LeftoverModel currentData, UndeclaredEvent @event) => currentData;
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "LeftoverPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource!).Contains("case global::TestNamespace.DeclaredEvent typedEvent:")
      .Because("the declared event's case must still generate normally.");
    await Assert.That(runnerSource!).DoesNotContain("UndeclaredEvent")
      .Because("an Apply overload for an event outside this perspective's declared interfaces must not leak into the generated runner.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PerspectiveRunnerGenerator_TupleReturnWithNonModelActionSecondElement_FallsThroughToModelCaseAsync() {
    // Only a 2-tuple whose SECOND element is exactly ModelAction is recognized as the hybrid
    // (TModel?, ModelAction) return shape. A 2-tuple with any other second element type must not
    // match that shape: it falls through to the plain "Model" classification, which assumes Apply
    // returns TModel directly — here Apply actually returns a tuple, so the generated case
    // silently wraps a tuple where TModel is expected instead of surfacing a diagnostic about the
    // mismatched Apply signature.
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public record MislabeledEvent : IEvent {
    public string Id { get; init; } = "";
  }

  public record MislabeledModel {
    [StreamId]
    public string Id { get; init; } = "";
  }

  public class MislabeledPerspective : IPerspectiveFor<MislabeledModel, MislabeledEvent> {
    public (MislabeledModel, string) Apply(MislabeledModel currentData, MislabeledEvent @event) =>
        (currentData, "note");
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    var runnerSource = GeneratorTestHelper.GetGeneratedSource(result, "MislabeledPerspectiveRunner.g.cs");
    await Assert.That(runnerSource).IsNotNull();
    await Assert.That(runnerSource!)
      .Contains("return (perspective.Apply(currentModel!, typedEvent), global::Whizbang.Core.Perspectives.ModelAction.None);")
      .Because("a 2-tuple whose second element isn't ModelAction must be classified as the plain Model case, not the Tuple case.");
  }

  #endregion
}
