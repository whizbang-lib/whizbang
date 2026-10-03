using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Issue #1002: a Split model's promoted fields live only in their columns, so the runner strips them before the
/// write and copies the columns back into a model it loads. A class can set an <c>init</c>-only property only while
/// an instance is being created, so assigning <c>model.X = default!</c> did not compile (CS8852). A Split class with
/// an <c>init</c>-only promoted field is now stripped into a copy and loaded through one: a new instance from its
/// parameterless constructor, every property the document reads carried over, the promoted fields set. A class that
/// cannot be copied that way is WHIZ808 rather than a compile error in generated code.
/// </summary>
/// <tests>src/Whizbang.Generators/PerspectiveRunnerGenerator.cs</tests>
/// <tests>src/Whizbang.Generators.Shared/Models/ModelCopy.cs</tests>
[Category("SourceGenerators")]
public class PerspectiveRunnerSplitInitOnlyTests {
  private const string SOURCE = """
    #nullable enable
    using System;
    using System.Collections.Generic;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;

    namespace TestNamespace;

    public class TicketEvent : IEvent {
      public Guid Id { get; set; }
    }

    public class TicketBase {
      public string? BaseNote { get; set; }
      public static int Shared { get; set; }
    }

    [PerspectiveStorage(FieldStorageMode.Split)]
    public class InitClassModel : TicketBase {
      [StreamId]
      public Guid Id { get; set; }

      [PhysicalField]
      public string Status { get; init; } = "";

      [PhysicalField]
      public int Priority { get; set; }

      public string? Note { get; internal set; }

      public string Display => Status + Note;

      internal string Hidden { get; set; } = "";

      public string this[int i] => Status;
    }

    [PerspectiveStorage(FieldStorageMode.Split)]
    public class StoresAGetOnlyValue {
      [StreamId]
      public Guid Id { get; set; }

      [PhysicalField]
      public string Status { get; init; } = "";

      public List<string> Tags { get; } = new();
    }

    [PerspectiveStorage(FieldStorageMode.Split)]
    public class HasAPrivateSetter {
      [StreamId]
      public Guid Id { get; set; }

      [PhysicalField]
      public string Status { get; init; } = "";

      public string Owner { get; private set; } = "";
    }

    [PerspectiveStorage(FieldStorageMode.Split)]
    public class NeedsArguments {
      public NeedsArguments(Guid id) { Id = id; }

      [StreamId]
      public Guid Id { get; set; }

      [PhysicalField]
      public string Status { get; init; } = "";
    }

    [PerspectiveStorage(FieldStorageMode.Split)]
    public abstract class AbstractModel {
      [StreamId]
      public Guid Id { get; set; }

      [PhysicalField]
      public string Status { get; init; } = "";

      [PhysicalField]
      public int Priority { get; set; }
    }

    public class InitClassPerspective : IPerspectiveFor<InitClassModel, TicketEvent> {
      public InitClassModel Apply(InitClassModel currentData, TicketEvent @event) => currentData;
    }

    public class StoresAGetOnlyValuePerspective : IPerspectiveFor<StoresAGetOnlyValue, TicketEvent> {
      public StoresAGetOnlyValue Apply(StoresAGetOnlyValue currentData, TicketEvent @event) => currentData;
    }

    public class HasAPrivateSetterPerspective : IPerspectiveFor<HasAPrivateSetter, TicketEvent> {
      public HasAPrivateSetter Apply(HasAPrivateSetter currentData, TicketEvent @event) => currentData;
    }

    public class NeedsArgumentsPerspective : IPerspectiveFor<NeedsArguments, TicketEvent> {
      public NeedsArguments Apply(NeedsArguments currentData, TicketEvent @event) => currentData;
    }

    public class AbstractModelPerspective : IPerspectiveFor<AbstractModel, TicketEvent> {
      public AbstractModel Apply(AbstractModel currentData, TicketEvent @event) => currentData;
    }
    """;

  private static GeneratorDriverRunResult _run() => GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(SOURCE);

  private static string _runner(string perspective) =>
    GeneratorTestHelper.GetGeneratedSource(_run(), $"{perspective}Runner.g.cs") ?? "";

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ASplitClassWithAnInitOnlyPromotedField_IsStrippedIntoACopyAsync() {
    var runner = _runner("InitClassPerspective");

    await Assert.That(runner).Contains(
      "model = new global::TestNamespace.InitClassModel { Id = model.Id, Status = default!, Priority = default!, Note = model.Note, BaseNote = model.BaseNote };")
      .Because("the copy carries every property the document reads, its own then its base's, with the promoted fields stripped");
    await Assert.That(runner).DoesNotContain("model.Status = default!")
      .Because("assigning an init-only property of an instance that exists is CS8852");
    await Assert.That(runner).DoesNotContain("Display =").Because("a computed property is not carried");
    await Assert.That(runner).DoesNotContain("Hidden =").Because("a property the document does not read is not carried");
    await Assert.That(runner).DoesNotContain("Shared =");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ASplitClassStrippedIntoACopy_IsSnapshottedAfterTheWriteAsync() {
    var runner = _runner("InitClassPerspective");

    await Assert.That(runner).Contains("private static JsonDocument? SnapshotBeforeWrite(global::TestNamespace.InitClassModel model) => null;")
      .Because("the write strips a copy, so the instance the runner applied keeps its fields for the snapshot after the write");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ASplitClassWithAnInitOnlyPromotedField_IsLoadedThroughACopyAsync() {
    var runner = _runner("InitClassPerspective");

    await Assert.That(runner).Contains(
      "static (model, read) => new global::TestNamespace.InitClassModel { Id = model.Id, "
      + "Status = read.Read<string>(\"status\"), Priority = read.Read<int>(\"priority\"), Note = model.Note, BaseNote = model.BaseNote }")
      .Because("the columns go into a copy of the model the document loaded");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AnUncopyableSplitClass_IsWHIZ808Async() {
    var result = _run();
    var messages = result.Diagnostics.Where(d => d.Id == "WHIZ808")
      .Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).ToList();

    await Assert.That(messages).Count().IsEqualTo(4);
    await Assert.That(result.Diagnostics.First(d => d.Id == "WHIZ808").Severity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(messages).Contains(m => m.Contains("TestNamespace.StoresAGetOnlyValue", StringComparison.Ordinal)
      && m.Contains("Tags is a get-only property that stores a value", StringComparison.Ordinal));
    await Assert.That(messages).Contains(m => m.Contains("the setter of Owner is not public or internal", StringComparison.Ordinal));
    await Assert.That(messages).Contains(m => m.Contains("TestNamespace.NeedsArguments", StringComparison.Ordinal)
      && m.Contains("no public or internal parameterless constructor", StringComparison.Ordinal));
    await Assert.That(messages).Contains(m => m.Contains("TestNamespace.AbstractModel", StringComparison.Ordinal)
      && m.Contains("it is abstract", StringComparison.Ordinal));

    var runner = GeneratorTestHelper.GetGeneratedSource(result, "AbstractModelPerspectiveRunner.g.cs") ?? "";
    await Assert.That(runner).Contains("model.Priority = default!;");
    await Assert.That(runner).DoesNotContain("model.Status = default!")
      .Because("the field the build cannot strip is reported, not assigned into code that would not compile");
    await Assert.That(runner).Contains("static (model, read) => { model.Priority = read.Read<int>(\"priority\"); return model; }");
    await Assert.That(runner).Contains("SnapshotBeforeWrite(global::TestNamespace.AbstractModel model) => ToSnapshotJson(model);")
      .Because("a class the build could not copy is still stripped in place, so it is snapshotted before the write");
  }
}
