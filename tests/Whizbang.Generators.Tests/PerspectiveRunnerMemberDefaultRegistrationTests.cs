// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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

  // Constants declared on ANOTHER type. Resolving these would need a semantic model, which this generator does
  // not take, so they stand for "knowable to the compiler but not to the generator".
  public static class Defaults {
    public const Stage Phase = Stage.Active;
    public const int Number = 2;
  }

  public record OrderModel {
    [StreamId]
    public Guid Id { get; init; }

    // A literal initializer on a reference type: the token text is the value.
    public string Status { get; init; } = "Draft";

    // Non-nullable value types with no initializer: the CLR default is the value a rebuild sees.
    public long Ordinal { get; init; }
    public bool IsLive { get; init; }
    public Stage Stage { get; init; }

    // Nullable with no initializer: an absent key already reads as null in both paths, so there is nothing
    // to declare.
    public string? Note { get; init; }
    public long? MaybeOrdinal { get; init; }

    // A non-nullable reference type with no initializer. There is no value to declare and no CLR default
    // worth registering — it would read as null, which the registry records by not registering.
    public string Bare { get; init; }

    // Knowable without being a literal: an enumeration member IS a compile-time constant, and this is the
    // ordinary way a status-like member declares an eligible default.
    public Stage Phase { get; init; } = Stage.Active;

    // The same constant, written as a cast of a literal.
    public Stage Legacy { get; init; } = (Stage)2;

    // A nullable enumeration with an enumeration-member initializer: nullable, but a rebuild still runs the
    // initializer, so an absent key does not read as null.
    public Stage? Maybe { get; init; } = Stage.Retired;

    // Nullable, but the initializer runs on a rebuild, so an absent key does NOT read as null here.
    public string? Label { get; init; } = "none";
    public long? Threshold { get; init; } = 7;

    // `= null` says the member reads as null, which is what not registering it already means.
    public string? Nulled { get; init; } = null;

    // A literal on a value type, and a negated one.
    public long Start { get; init; } = 5;
    public long Offset { get; init; } = -3;

    // A unary operator the rule does not read: `+5` states its value as plainly as `-3` does, but only minus is
    // recognized, so this one is skipped rather than guessed at.
    public long Plus { get; init; } = +5;

    // A unary minus over something that is not a literal — the sign is knowable, the operand is not.
    public long Negated { get; init; } = -Defaults.Number;

    // Not knowable from the declaration alone.
    public string Name { get; init; } = string.Empty;
    public Stage FromConst { get; init; } = Defaults.Phase;
    public Stage Converted { get; init; } = (Stage)Defaults.Number;
    public string Computed { get; init; } = "a" + "b";
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
  public async Task Runner_AnEnumMemberInitializer_RegistersThatMemberAsync() {
    var runner = _runner();

    await Assert.That(runner).Contains(REGISTER + "typeof(global::TestNamespace.OrderModel), \"Phase\", global::TestNamespace.Stage.Active)")
      .Because("An enumeration member is a compile-time constant, so what an absent key reads as is knowable "
        + "exactly — there is no guessing. Skipping it is what leaves a cohort predicate on a status-like member "
        + "filtering on SQL NULL while the rebuild it is meant to agree with sees Active.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runner_ACastOfALiteralInitializer_RegistersThatValueAsync() {
    var runner = _runner();

    await Assert.That(runner).Contains("\"Legacy\", (global::TestNamespace.Stage)2)")
      .Because("`(Stage)2` is the same constant as naming the member; the declaration form it is written in does "
        + "not change what a rebuild produces.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runner_ANullableMemberWithAnInitializer_RegistersItAsync() {
    var runner = _runner();

    await Assert.That(runner).Contains("\"Label\", \"none\")")
      .Because("The nullable skip holds only because an absent key reads as null in BOTH paths. An initializer "
        + "breaks that: a rebuild runs it and the member holds \"none\", so the two paths disagree exactly as they "
        + "do for a non-nullable member.");
    await Assert.That(runner).Contains("\"Threshold\", (long?)7)")
      .Because("Nullable<T> is the same case; the value binds as the member's own type so it compares against the "
        + "column as that type rather than as int.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runner_AnEnumMemberWithoutAnInitializer_StillRegistersItsClrDefaultAsync() {
    var runner = _runner();

    await Assert.That(runner).Contains("\"Stage\", default(global::TestNamespace.Stage))")
      .Because("Widening the rule to knowable constants must not disturb the no-initializer case, which already "
        + "resolved correctly.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runner_ANullableEnumWithAnEnumMemberInitializer_RegistersThatMemberAsync() {
    var runner = _runner();

    await Assert.That(runner).Contains("\"Maybe\", global::TestNamespace.Stage.Retired)")
      .Because("Nullable<Stage> is unwrapped to find the enumeration the member names. The member is registered as "
        + "the enumeration value, which boxes as Stage and binds against the column as Stage does.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runner_ALiteralOnAValueType_RegistersItCastToThatTypeAsync() {
    var runner = _runner();

    await Assert.That(runner).Contains("\"Start\", (long)5)")
      .Because("`= 5` on a long must arrive as a long, not an int, or the registered value binds against the "
        + "column as the wrong type.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runner_ANegatedLiteral_RegistersTheNegatedValueAsync() {
    var runner = _runner();

    await Assert.That(runner).Contains("\"Offset\", (long)(-3))")
      .Because("Unary minus only parses ahead of a numeric literal, so the value stays knowable; dropping the "
        + "sign would register +3 and quietly filter the wrong rows.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runner_AnExplicitNullInitializer_IsNotRegisteredAsync() {
    var runner = _runner();

    await Assert.That(runner).DoesNotContain("\"Nulled\"")
      .Because("`= null` says the member reads as null, which is exactly what no registration means. Registering "
        + "it would also be read as a removal, so the two agree — but saying nothing is the honest form.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runner_ANonNullableReferenceWithNoInitializer_IsNotRegisteredAsync() {
    var runner = _runner();

    await Assert.That(runner).DoesNotContain("\"Bare\"")
      .Because("The declaration names no value and a reference type has no CLR default worth registering: it "
        + "reads as null, which both paths already agree on.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runner_AConstantDeclaredOnAnotherType_IsNotRegisteredAsync() {
    var runner = _runner();

    await Assert.That(runner).DoesNotContain("\"FromConst\"")
      .Because("`Defaults.Phase` is a constant to the compiler but not to this generator: only a member of the "
        + "member's OWN enumeration can be resolved from the type symbol, and binding a semantic model to read "
        + "the rest would cost the discovery step its incremental cacheability.");
    await Assert.That(runner).DoesNotContain("\"Converted\"")
      .Because("A cast does not make its operand knowable — (Stage)Defaults.Number is the same unresolvable "
        + "constant with a cast in front of it.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runner_AUnaryOperatorOtherThanMinus_IsNotRegisteredAsync() {
    var runner = _runner();

    await Assert.That(runner).DoesNotContain("\"Plus\"")
      .Because("Only unary minus is read. `+5` is just as knowable, and recognizing it would be a fine change — "
        + "but until it is read, registering nothing is what keeps the registration honest about what was parsed.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runner_AUnaryMinusOverANonLiteral_IsNotRegisteredAsync() {
    var runner = _runner();

    await Assert.That(runner).DoesNotContain("\"Negated\"")
      .Because("The sign is knowable and the operand is not, so the expression as a whole is not. Negating an "
        + "unresolved constant would register a value no rebuild produces.");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Runner_AConstantFoldedExpression_IsNotRegisteredAsync() {
    var runner = _runner();

    await Assert.That(runner).DoesNotContain("\"Computed\"")
      .Because("\"a\" + \"b\" folds to a constant in the compiler, but reading it here would mean evaluating "
        + "expressions. The line is drawn at what the declaration states outright.");
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
