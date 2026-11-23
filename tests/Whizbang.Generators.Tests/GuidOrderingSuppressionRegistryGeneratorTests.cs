// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// #2528: the generator registers every type that carries <c>[SuppressGuidOrderingValidation]</c> by its stored name
/// (<c>Namespace.Type, Assembly</c>), so the time-ordering check reads no attribute by reflection and a type generated
/// code cannot name is honored all the same.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/GuidOrderingSuppressionRegistryGenerator.cs</code-under-test>
public class GuidOrderingSuppressionRegistryGeneratorTests {
  private const string GENERATED = "GuidOrderingSuppressions.g.cs";
  private const string REGISTER = "global::Whizbang.Core.Validation.GuidOrderingSuppressions.Register(";

  private const string SOURCE = """
    using Whizbang.Core;

    namespace MyApp;

    [SuppressGuidOrderingValidation]
    public record LegacyImported : IEvent;

    [SuppressGuidOrderingValidation]
    public readonly record struct LegacyStruct(int Value);

    [SuppressGuidOrderingValidation]
    public sealed record Wrapper<T>(T Value) : IEvent;

    public record Unattributed : IEvent;

    public record DerivedFromLegacy : LegacyImported;

    public static class Holder {
      [SuppressGuidOrderingValidation]
      private sealed record Hidden : IEvent;

      [SuppressGuidOrderingValidation]
      public static void LegacyMethod() { }
    }
    """;

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_RegistersEachAttributedTypeByItsStoredNameAsync() {
    var result = GeneratorTestHelper.RunGenerator<GuidOrderingSuppressionRegistryGenerator>(SOURCE);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, GENERATED);

    await Assert.That(generated).IsNotNull();
    await Assert.That(generated!).Contains("[global::System.Runtime.CompilerServices.ModuleInitializer]");
    await Assert.That(generated).Contains(REGISTER + "\"MyApp.LegacyImported, TestAssembly\");");
    await Assert.That(generated).Contains(REGISTER + "\"MyApp.LegacyStruct, TestAssembly\");");
    await Assert.That(generated).Contains(REGISTER + "\"MyApp.Wrapper`1, TestAssembly\");")
      .Because("a generic type registers its definition, which every construction's name reduces to");
    await Assert.That(generated).Contains(REGISTER + "\"MyApp.Holder+Hidden, TestAssembly\");")
      .Because("registration is by name, so a private nested type needs no reachable typeof");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_SkipsUnattributedDerivedAndMethodTargetsAsync() {
    var result = GeneratorTestHelper.RunGenerator<GuidOrderingSuppressionRegistryGenerator>(SOURCE);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, GENERATED)!;

    await Assert.That(generated).DoesNotContain("Unattributed");
    await Assert.That(generated).DoesNotContain("DerivedFromLegacy")
      .Because("the attribute is not inherited");
    await Assert.That(generated).DoesNotContain("LegacyMethod")
      .Because("a method carrying the attribute is a compile-time opt-out only (WHIZ037), not a message type");
    await Assert.That(result.Diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_NoAttributedType_GeneratesNothingAsync() {
    var result = GeneratorTestHelper.RunGenerator<GuidOrderingSuppressionRegistryGenerator>("""
      using Whizbang.Core;
      namespace MyApp;
      public record Plain : IEvent;
      """);

    await Assert.That(GeneratorTestHelper.GetGeneratedSource(result, GENERATED)).IsNull();
  }
}
