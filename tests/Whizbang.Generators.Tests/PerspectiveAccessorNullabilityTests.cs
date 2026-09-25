using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The accessors have to compile clean in the consumer's own build, not merely compile.
/// </summary>
/// <remarks>
/// <para>
/// A consumer that treats warnings as errors — which every service in the reference application
/// does — cannot build at all if generated code raises one. The generator already reasons about
/// this for the type an accessor yields, in its own words: a nullable warning there is "their build
/// broken by generated code rather than by anything they wrote". The same has to hold for the
/// lambda the accessor is.
/// </para>
/// <para>
/// A nested path walks through its parent, and a parent that may be null is a dereference of a
/// possibly-null reference (CS8602) unless the emitted lambda says otherwise. Because these lambdas
/// are expression trees read by a query translator rather than executed, the null-forgiving
/// operator changes nothing at runtime — the tree is identical — and it is the whole difference
/// between a consumer building and not.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Generators/PerspectiveAccessorGenerator.cs</code-under-test>
public class PerspectiveAccessorNullabilityTests {

  /// <summary>
  /// A model whose nested parent is nullable, and whose collection carries nullable elements: the
  /// two shapes that broke a consumer build at 0.2451.0-alpha.66.
  /// </summary>
  private const string SOURCE = """
    #nullable enable
    using System;
    using System.Collections.Generic;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;

    namespace MyApp.Perspectives;

    public class SnapshotModel {
      public Guid EmployeeId { get; init; }
      public string? Emplid { get; init; }
    }

    public class AckModel {
      [StreamId]
      public Guid AckId { get; init; }
      public SnapshotModel? Snapshot { get; init; }
      public List<string?> Notes { get; init; } = new();
    }

    public record AckCreated([property: StreamId] Guid AckId) : IEvent;

    public class AckPerspective : IPerspectiveFor<AckModel, AckCreated> {
      public AckModel Apply(AckModel? current, AckCreated @event) =>
        new AckModel { AckId = @event.AckId };
    }
    """;

  /// <summary>
  /// A path through a nullable parent forgives the parent, so only the hop that can be null is
  /// annotated, and a collection keeps the nullability of its elements.
  /// </summary>
  /// <remarks>
  /// Asserted on the emitted text rather than by compiling it: a harness that compiles the output
  /// and looks for the warning reported none while the defect was present, and a test that passes
  /// either way is worse than no test at all.
  /// </remarks>
  [Test]
  public async Task ANestedPathThroughANullableParent_ForgivesTheParentAsync() {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveAccessorGenerator>(SOURCE);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "MyApp_Perspectives_AckModelAccessors.g.cs");

    await Assert.That(generated).IsNotNull();
    await Assert.That(generated).Contains("_m => _m.Snapshot!.EmployeeId")
      .Because("the parent is the hop that may be null, and it is the only one that needs forgiving");
    await Assert.That(generated).Contains("_m => _m.AckId")
      .Because("a top-level path has no parent to forgive, so it stays exactly as it was");
    await Assert.That(generated).DoesNotContain("_m => _m.Snapshot.EmployeeId")
      .Because("the unforgiven form is the one that breaks a consumer that treats warnings as errors");
    // The other half of what broke the consumer: the element annotation belongs to the type the
    // accessor yields, and dropping it makes the assignment itself a nullability mismatch (CS8619).
    await Assert.That(generated).Contains("List<string?>>> Notes")
      .Because("a collection of nullable elements yields exactly that, not a collection of non-null ones");
  }
}
