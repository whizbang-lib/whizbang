using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The perspective runner registers its model's declared member defaults in
/// <c>PerspectiveMemberDefaultRegistry</c> from a generated <c>[ModuleInitializer]</c>, the same turnkey path the
/// physical fields take. The collective predicate compiler reads that registration so a document with no key for
/// a member is filtered as the value a rebuild would see, with no reflection over the model (#1044).
/// </summary>
/// <docs>fundamentals/messaging/collective-events</docs>
public class PerspectiveRunnerMemberDefaultRegistrationTests {
  private const string REGISTER = "global::Whizbang.Core.Perspectives.PerspectiveMemberDefaultRegistry.Register(";

  private const string SOURCE = """
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;

namespace TestNamespace {
  public record OrderMovedEvent : IEvent {
    public Guid Id { get; init; }
  }

  public enum Stage { Draft, Active, Retired }

  public record OrderModel {
    [StreamId]
    public Guid Id { get; init; }

    // A literal initializer: the value a rebuild sees when the key is absent.
    public string Status { get; init; } = "Draft";

    // Non-nullable value types with no initializer: the CLR default is the value a rebuild sees.
    public long Ordinal { get; init; }
    public bool IsLive { get; init; }
    public Stage Stage { get; init; }

    // Nullable: an absent key already reads as null in both paths, so there is nothing to declare.
    public string? Note { get; init; }
    public long? MaybeOrdinal { get; init; }

    // Not a literal, so its value is not knowable from the declaration alone.
    public string Name { get; init; } = string.Empty;
  }

  public class OrderPerspective : IPerspectiveFor<OrderModel, OrderMovedEvent> {
    public OrderModel Apply(OrderModel currentData, OrderMovedEvent @event) => currentData;
  }
}
""";

  private static string? _runner() {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(SOURCE);
    return GeneratorTestHelper.GetGeneratedSource(result, "OrderPerspectiveRunner.g.cs");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runner_AModelWithDeclaredDefaults_EmitsAModuleInitializerAsync() {
    var runner = _runner();

    await Assert.That(runner).IsNotNull();
    await Assert.That(runner).Contains("[global::System.Runtime.CompilerServices.ModuleInitializer]");
    await Assert.That(runner).Contains("_registerMemberDefaults()")
      .Because("Self-registration at module load is how the compiler learns the defaults without reflecting over "
        + "the model, which the AOT guarantee forbids.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runner_ALiteralInitializer_RegistersThatLiteralAsync() {
    var runner = _runner();

    await Assert.That(runner).Contains(REGISTER + "typeof(global::TestNamespace.OrderModel), \"Status\", \"Draft\")")
      .Because("`= \"Draft\"` is visible to the generator at compile time, while default(T) at run time reports "
        + "null for it — which is the whole reason the registry exists rather than a reflective lookup.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runner_ANonNullableValueType_RegistersItsClrDefaultAsync() {
    var runner = _runner();

    await Assert.That(runner).Contains("\"Ordinal\", default(long))")
      .Because("A rebuild deserializes the row and Ordinal holds 0, so a predicate on it has to read an absent "
        + "key as 0 rather than as SQL NULL.");
    await Assert.That(runner).Contains("\"IsLive\", default(bool))")
      .Because("The fully qualified display form keeps the language keyword for a primitive, and default(bool) "
        + "boxes to the same value global::System.Boolean would.");
    await Assert.That(runner).Contains("\"Stage\", default(global::TestNamespace.Stage))")
      .Because("An enumeration's default is its zero member, which is a real value a rebuild would compare against.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runner_ANullableMember_IsNotRegisteredAsync() {
    var runner = _runner();

    await Assert.That(runner).DoesNotContain("\"Note\"")
      .Because("A nullable member reads as null for an absent key in both paths, so they already agree. "
        + "Coalescing it would change a predicate that is correct today.");
    await Assert.That(runner).DoesNotContain("\"MaybeOrdinal\"");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runner_ANonLiteralInitializer_IsNotRegisteredAsync() {
    var runner = _runner();

    await Assert.That(runner).DoesNotContain("\"Name\"")
      .Because("Only a literal is knowable from the declaration. Guessing at an expression would register a "
        + "default that differs from the one a rebuild actually produces, which is worse than registering none.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runner_AModelOfNullableMembers_RegistersOnlyItsStreamIdAsync() {
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

    public string? Body { get; init; }
    public int? Rank { get; init; }
  }

  public class NotePerspective : IPerspectiveFor<NoteModel, NoteAddedEvent> {
    public NoteModel Apply(NoteModel currentData, NoteAddedEvent @event) => currentData;
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    var runner = GeneratorTestHelper.GetGeneratedSource(result, "NotePerspectiveRunner.g.cs");

    await Assert.That(runner).IsNotNull();
    await Assert.That(runner).Contains("\"Id\", default(global::System.Guid))")
      .Because("A Guid stream id is a non-nullable value type like any other, so an absent key reads as "
        + "Guid.Empty. The rule is about nullability, not about which member happens to be the identity.");
    await Assert.That(runner).DoesNotContain("\"Body\"");
    await Assert.That(runner).DoesNotContain("\"Rank\"")
      .Because("Both are nullable, so they already agree on both paths and there is nothing to declare.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runner_AMemberInheritedFromAnotherAssembly_IsNotRegisteredAsync() {
    // Standing in for a model that inherits a library base class: those members are metadata symbols with no
    // syntax, so whether they carry an initializer cannot be known and a guess would be worse than nothing.
    const string source = """
using Whizbang.Core;
using Whizbang.Core.Perspectives;
using System;
using System.IO;

namespace TestNamespace {
  public record StreamTouchedEvent : IEvent { public Guid Id { get; init; } }

  public class InheritedModel : MemoryStream {
    [StreamId]
    public Guid Id { get; init; }
    public string? Note { get; init; }
  }

  public class InheritedPerspective : IPerspectiveFor<InheritedModel, StreamTouchedEvent> {
    public InheritedModel Apply(InheritedModel currentData, StreamTouchedEvent @event) => currentData;
  }
}
""";

    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerGenerator>(source);
    var runner = GeneratorTestHelper.GetGeneratedSource(result, "InheritedPerspectiveRunner.g.cs");

    await Assert.That(runner).IsNotNull();
    foreach (var inherited in new[] { "\"Length\"", "\"Position\"", "\"CanRead\"", "\"Capacity\"" }) {
      await Assert.That(runner).DoesNotContain(inherited)
        .Because("Length, Position, CanRead and Capacity are non-nullable value types, so the nullability rule alone "
          + "would register them — they are skipped because their declarations are not in this compilation.");
    }
  }
}
