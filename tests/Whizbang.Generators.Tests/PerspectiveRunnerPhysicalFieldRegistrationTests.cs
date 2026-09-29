using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The perspective runner registers its model's physical fields in <c>PerspectivePhysicalFieldRegistry</c> from a
/// generated <c>[ModuleInitializer]</c>, the same turnkey path the row TTL and row cap take. The collective apply
/// path reads that registration to send a setter or a condition on a <c>[PhysicalField]</c> property to its
/// column, with no reflection over the model.
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
public class PerspectiveRunnerPhysicalFieldRegistrationTests {
  private const string REGISTER = "global::Whizbang.Core.Perspectives.PerspectivePhysicalFieldRegistry.Register(";

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runner_SplitModel_RegistersEachPhysicalField_AsSplitAsync() {
    const string source = """
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record TicketMovedEvent : IEvent {
    public Guid Id { get; init; }
  }

  [PerspectiveStorage(FieldStorageMode.Split)]
  public record TicketModel {
    [StreamId]
    public Guid Id { get; init; }

    [PhysicalField]
    public string Lane { get; init; } = "";

    [PhysicalField(ColumnName = "prio")]
    public int Priority { get; init; }

    [VectorField(3)]
    public float[]? Embedding { get; init; }

    public string Title { get; init; } = "";
  }

  public class TicketPerspective : IPerspectiveFor<TicketModel, TicketMovedEvent> {
    public TicketModel Apply(TicketModel currentData, TicketMovedEvent @event) => currentData;
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    var runner = GeneratorTestHelper.GetGeneratedSource(result, "TicketPerspectiveRunner.g.cs");

    await Assert.That(runner).IsNotNull();
    await Assert.That(runner!).Contains("[global::System.Runtime.CompilerServices.ModuleInitializer]")
      .Because("The registration must run without consumer code, like the TTL and row cap registrations.");
    await Assert.That(runner).Contains(
      REGISTER + "typeof(global::TestNamespace.TicketModel), \"Lane\", \"lane\", global::Whizbang.Core.Perspectives.FieldStorageMode.Split, isVector: false);");
    await Assert.That(runner).Contains(
      REGISTER + "typeof(global::TestNamespace.TicketModel), \"Priority\", \"prio\", global::Whizbang.Core.Perspectives.FieldStorageMode.Split, isVector: false);")
      .Because("A declared ColumnName is the column the collective must write.");
    await Assert.That(runner).Contains(
      REGISTER + "typeof(global::TestNamespace.TicketModel), \"Embedding\", \"embedding\", global::Whizbang.Core.Perspectives.FieldStorageMode.Split, isVector: true);")
      .Because("A vector field is registered and flagged, so the collective path can refuse it with a clear message.");
    await Assert.That(runner).DoesNotContain("\"Title\"")
      .Because("A document-only property has no column and is not registered.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runner_ExtractedModel_RegistersThePhysicalField_AsExtractedAsync() {
    const string source = """
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderUpdatedEvent : IEvent {
    public Guid Id { get; init; }
  }

  [PerspectiveStorage(FieldStorageMode.Extracted)]
  public record OrderModel {
    [StreamId]
    public Guid Id { get; init; }

    [PhysicalField]
    public string Status { get; init; } = "";
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderUpdatedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderUpdatedEvent @event) => currentData;
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    var runner = GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");

    await Assert.That(runner).IsNotNull();
    await Assert.That(runner!).Contains(
      REGISTER + "typeof(global::TestNamespace.OrderModel), \"Status\", \"status\", global::Whizbang.Core.Perspectives.FieldStorageMode.Extracted, isVector: false);");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runner_PhysicalFieldWithoutStorageAttribute_RegistersAsJsonOnlyAsync() {
    // No [PerspectiveStorage]: the model is JsonOnly, and its [PhysicalField] is a column AND a document path.
    const string source = """
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record CodeChangedEvent : IEvent {
    public Guid Id { get; init; }
  }

  public record CodeModel {
    [StreamId]
    public Guid Id { get; init; }

    [PhysicalField]
    public string Code { get; init; } = "";
  }

  public class CodePerspective : IPerspectiveFor<CodeModel, CodeChangedEvent> {
    public CodeModel Apply(CodeModel currentData, CodeChangedEvent @event) => currentData;
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    var runner = GeneratorTestHelper.GetGeneratedSource(result, "CodePerspectiveRunner.g.cs");

    await Assert.That(runner).IsNotNull();
    await Assert.That(runner!).Contains(
      REGISTER + "typeof(global::TestNamespace.CodeModel), \"Code\", \"code\", global::Whizbang.Core.Perspectives.FieldStorageMode.JsonOnly, isVector: false);");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runner_NoPhysicalFields_RegistersNothingAsync() {
    const string source = """
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record NoteAddedEvent : IEvent {
    public Guid Id { get; init; }
  }

  public record NoteModel {
    [StreamId]
    public Guid Id { get; init; }
    public string Text { get; init; } = "";
  }

  public class NotePerspective : IPerspectiveFor<NoteModel, NoteAddedEvent> {
    public NoteModel Apply(NoteModel currentData, NoteAddedEvent @event) => currentData;
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    var runner = GeneratorTestHelper.GetGeneratedSource(result, "NotePerspectiveRunner.g.cs");

    await Assert.That(runner).IsNotNull();
    await Assert.That(runner!).DoesNotContain("PerspectivePhysicalFieldRegistry")
      .Because("A model with no physical fields has nothing to register.");
  }
}
