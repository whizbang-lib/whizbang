// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// <c>[WhizbangId(Versions = ...)]</c> generates an id whose checked entry points (<c>From</c>, <c>Parse</c>, the
/// explicit cast and the JSON converter) take exactly the listed UUID versions (#2516). An id that leaves
/// <c>Versions</c> at its default, or writes <c>Versions = UuidVersions.V7</c>, generates the same code as before.
/// Writing <c>Versions</c> together with <c>AcceptAnyVersion = true</c> is WHIZ039; an empty list, or declarations
/// with no version in common, is WHIZ043; a declaration whose list is narrowed by another is WHIZ038.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/WhizbangIdGenerator.cs</code-under-test>
[Category("SourceGenerators")]
public class WhizbangIdVersionsTests {

  private const string STRICT_FROM_GUARD = "if (value.Version != 7 && !_grandfathers(value))";
  private const string ALLOW_LIST_GUARD = "(value.Version is (5 or 7) && (value.Variant & 0xC) == 0x8) || _grandfathers(value);";
  private const string NON_EMPTY_GUARD = "_requireNonEmpty(";

  [RequiresAssemblyFiles()]
  private static string _generatedId(string source, string typeName = "ProductId") {
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);
    return GeneratorTestHelper.GetGeneratedSource(result, $"{typeName}.g.cs")
      ?? throw new InvalidOperationException($"{typeName}.g.cs was not generated");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ExplicitType_WithAllowList_ChecksTheListedVersionsAsync() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId(Versions = UuidVersions.V7 | UuidVersions.V5)]
      public readonly partial struct ProductId;
      """;

    var generated = _generatedId(source);

    await Assert.That(generated).Contains(ALLOW_LIST_GUARD);
    await Assert.That(generated).Contains(
      "throw new ArgumentException($\"ProductId requires UUIDv5 or UUIDv7 (RFC 9562 variant) but received version {value.Version}, variant {value.Variant:X}\", parameterName);");
    await Assert.That(generated).Contains(
      "public static ProductId From(Guid value) => new(TrackedGuid.FromExternal(_requireAcceptedVersion(value, nameof(value))));");
    await Assert.That(generated).Contains("_requireAcceptedVersion(tracked.Value, nameof(tracked));");
    await Assert.That(generated).DoesNotContain(STRICT_FROM_GUARD);
    await Assert.That(generated).DoesNotContain(NON_EMPTY_GUARD);
    await Assert.That(generated).Contains("public static ProductId New() => new(TrackedGuid.New());");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ExplicitType_WithAllowList_DocCommentsNameTheListAsync() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId(Versions = UuidVersions.V7 | UuidVersions.V5)]
      public readonly partial struct ProductId;
      """;

    var generated = _generatedId(source);

    await Assert.That(generated).Contains("Throws if not UUIDv5 or UUIDv7.");
  }

  /// <summary>Writing the default explicitly generates exactly what leaving it out does.</summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task ExplicitType_WithVersionsV7_GeneratesTheDefaultCodeAsync() {
    const string explicitV7 = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId(Versions = UuidVersions.V7)]
      public readonly partial struct ProductId;
      """;
    const string implicitV7 = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId]
      public readonly partial struct ProductId;
      """;

    await Assert.That(_generatedId(explicitV7)).IsEqualTo(_generatedId(implicitV7));
  }

  /// <summary>
  /// The default rule generates, byte for byte, what the generator produced before <c>Versions</c> existed, apart from
  /// the grandfathered-values hook (#2517) and <c>IsAccepted</c>: <c>Snapshots/WhizbangIdDefault.snapshot.txt</c> is
  /// develop's output for this source, captured before #2516, and the output with the hook's declaration, its helper,
  /// its two guard clauses and <c>IsAccepted</c> taken out is that snapshot. Anything else that changes the default
  /// output fails here.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task DefaultRule_IsByteIdenticalToTheOutputBeforeVersions_ApartFromTheHookAsync() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId]
      public readonly partial struct ProductId;
      """;
    var generated = _generatedId(source).Replace("\r\n", "\n", StringComparison.Ordinal);
    var hookStart = generated.IndexOf("  /// <summary>\n  /// Implement to let the checked entry points", StringComparison.Ordinal);
    var hookEnd = generated.IndexOf("    return grandfathered;\n  }\n\n", StringComparison.Ordinal) + "    return grandfathered;\n  }\n\n".Length;

    const string IS_ACCEPTED_START = "\n  /// <summary>\n  /// Whether <c>From</c> accepts";
    const string IS_ACCEPTED_END = "public static bool IsAccepted(Guid value) => value.Version == 7 || _grandfathers(value);\n";
    var acceptedStart = generated.IndexOf(IS_ACCEPTED_START, StringComparison.Ordinal);
    var acceptedEnd = generated.IndexOf(IS_ACCEPTED_END, StringComparison.Ordinal) + IS_ACCEPTED_END.Length;

    await Assert.That(hookStart).IsGreaterThan(0);
    await Assert.That(acceptedStart).IsGreaterThan(0);
    await Assert.That(hookStart).IsEqualTo(acceptedEnd).Because("IsAccepted comes just before the hook");
    var withoutHook = (generated[..acceptedStart] + generated[hookEnd..])
      .Replace(" && !_grandfathers(tracked.Value)", "", StringComparison.Ordinal)
      .Replace(" && !_grandfathers(value)", "", StringComparison.Ordinal);

    await Assert.That(withoutHook).IsEqualTo(_snapshot(DEFAULT_SNAPSHOT).Replace("\r\n", "\n", StringComparison.Ordinal));
  }

  private const string DEFAULT_SNAPSHOT = "Whizbang.Generators.Tests.Snapshots.WhizbangIdDefault.snapshot.txt";

  private static string _snapshot(string resource) {
    using var stream = typeof(WhizbangIdVersionsTests).Assembly.GetManifestResourceStream(resource)
      ?? throw new InvalidOperationException($"The snapshot resource {resource} is missing");
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task SingleNonV7Version_ChecksThatVersionAsync() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId(Versions = UuidVersions.V5)]
      public readonly partial struct ProductId;
      """;

    var generated = _generatedId(source);

    await Assert.That(generated).Contains("(value.Version is (5) && (value.Variant & 0xC) == 0x8) || _grandfathers(value);");
    await Assert.That(generated).Contains("    if (!IsAccepted(value))");
    await Assert.That(generated).Contains("ProductId requires UUIDv5 (RFC 9562 variant) but received version");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PropertyDiscovery_HonorsVersionsAsync() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      public partial struct ProductId;
      public class CreateProduct {
        [WhizbangId(Versions = UuidVersions.V7 | UuidVersions.V5)]
        public ProductId Id { get; set; }
      }
      """;

    await Assert.That(_generatedId(source)).Contains(ALLOW_LIST_GUARD);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ParameterDiscovery_HonorsVersionsAsync() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      public partial struct ProductId;
      public record CreateProduct([WhizbangId(Versions = UuidVersions.V7 | UuidVersions.V5)] ProductId Id);
      """;

    await Assert.That(_generatedId(source)).Contains(ALLOW_LIST_GUARD);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task WithAllowList_GeneratedCodeCompilesAsync() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId(Versions = UuidVersions.V7 | UuidVersions.V5 | UuidVersions.V8)]
      public readonly partial struct ProductId;
      """;

    var (_, output) = GeneratorTestHelper.RunGeneratorWithFullReferences(new WhizbangIdGenerator(), source);
    var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

    await Assert.That(errors).IsEmpty();
  }

  // ========================================
  // WHIZ039: AcceptAnyVersion and Versions both written
  // ========================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AcceptAnyVersionAndVersions_ReportWhiz039AtTheAttributeAsync() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId(AcceptAnyVersion = true, Versions = UuidVersions.V5)]
      public readonly partial struct ProductId;
      """;

    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);
    var whiz039 = result.Diagnostics.Where(d => d.Id == "WHIZ039").ToList();

    await Assert.That(whiz039).Count().IsEqualTo(1);
    await Assert.That(whiz039[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(whiz039[0].GetMessage(CultureInfo.InvariantCulture)).Contains("ProductId");
    await Assert.That(DiagnosticSourceText.Of(whiz039[0]))
      .IsEqualTo("WhizbangId(AcceptAnyVersion = true, Versions = UuidVersions.V5)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AcceptAnyVersionFalseWithVersions_ReportsNoWhiz039Async() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId(AcceptAnyVersion = false, Versions = UuidVersions.V7 | UuidVersions.V5)]
      public readonly partial struct ProductId;
      """;

    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    await Assert.That(result.Diagnostics.Where(d => d.Id == "WHIZ039")).IsEmpty();
  }

  // ========================================
  // WHIZ043: no version accepted
  // ========================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task EmptyVersions_ReportWhiz043Async() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId(Versions = UuidVersions.None)]
      public readonly partial struct ProductId;
      """;

    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);
    var whiz043 = result.Diagnostics.Where(d => d.Id == "WHIZ043").ToList();

    await Assert.That(whiz043).Count().IsEqualTo(1);
    await Assert.That(whiz043[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(whiz043[0].GetMessage(CultureInfo.InvariantCulture)).Contains("ProductId");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task RoutesWithNoVersionInCommon_ReportWhiz043Async() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId(Versions = UuidVersions.V5)]
      public readonly partial struct ProductId;
      public class CreateProduct {
        [WhizbangId]
        public ProductId Id { get; set; }
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    await Assert.That(result.Diagnostics.Where(d => d.Id == "WHIZ043")).Count().IsEqualTo(1);
    await Assert.That(result.Diagnostics.Where(d => d.Id == "WHIZ038")).IsEmpty();
  }

  /// <summary>
  /// A bit of <c>Versions</c> outside versions 1 to 8 names no version: WHIZ044 is reported at the attribute, and the
  /// valid bits still decide the rule. A value with no valid bit at all also accepts no version (WHIZ043).
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  [Arguments("UuidVersions.V7 | (UuidVersions)(1 << 9)", "0x200", false)]
  [Arguments("(UuidVersions)1", "0x1", true)]
  public async Task VersionsWithBitsOutsideOneToEight_ReportWhiz044Async(string versions, string bits, bool acceptsNone) {
    var source = $$"""
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId(Versions = {{versions}})]
      public readonly partial struct ProductId;
      """;

    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);
    var whiz044 = result.Diagnostics.Where(d => d.Id == "WHIZ044").ToList();

    await Assert.That(whiz044).Count().IsEqualTo(1);
    await Assert.That(whiz044[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(whiz044[0].GetMessage(CultureInfo.InvariantCulture)).Contains(bits);
    await Assert.That(DiagnosticSourceText.Of(whiz044[0])).IsEqualTo($"WhizbangId(Versions = {versions})");
    await Assert.That(result.Diagnostics.Any(d => d.Id == "WHIZ043")).IsEqualTo(acceptsNone);
  }

  /// <summary>
  /// With no version in common, WHIZ043 points at a declaration that wrote <c>Versions</c>, not at one that took the
  /// default, whichever is found first.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task RoutesWithNoVersionInCommon_ReportWhiz043AtADeclarationThatWroteVersionsAsync() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId]
      public readonly partial struct ProductId;
      public class CreateProduct {
        [WhizbangId(Versions = UuidVersions.V5)]
        public ProductId Id { get; set; }
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);
    var whiz043 = result.Diagnostics.Where(d => d.Id == "WHIZ043").ToList();

    await Assert.That(whiz043).Count().IsEqualTo(1);
    await Assert.That(DiagnosticSourceText.Of(whiz043[0])).IsEqualTo("WhizbangId(Versions = UuidVersions.V5)");
  }

  /// <summary>
  /// An ignored <c>AcceptAnyVersion</c> opt-in is reported even when the lists have no version in common: WHIZ029 names
  /// the rule the id falls back to (UUIDv7), next to WHIZ043.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task AcceptAnyVersionAgainstListsWithNothingInCommon_StillReportsWhiz029Async() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId(AcceptAnyVersion = true)]
      public readonly partial struct ProductId;
      public class CreateProduct {
        [WhizbangId(Versions = UuidVersions.V5)]
        public ProductId Id { get; set; }
      }
      public record RenameProduct([WhizbangId(Versions = UuidVersions.V4)] ProductId Id);
      """;

    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);
    var whiz029 = result.Diagnostics.Where(d => d.Id == "WHIZ029").ToList();

    await Assert.That(result.Diagnostics.Where(d => d.Id == "WHIZ043")).Count().IsEqualTo(1);
    await Assert.That(whiz029).Count().IsEqualTo(1);
    await Assert.That(whiz029[0].GetMessage(CultureInfo.InvariantCulture)).Contains("UUIDv7");
    await Assert.That(DiagnosticSourceText.Of(whiz029[0])).IsEqualTo("WhizbangId(AcceptAnyVersion = true)");
  }

  /// <summary>
  /// Two declarations of the same kind, found in source order, resolve the same either way round: the id takes the
  /// common versions and WHIZ038 points at the list that was narrowed, whichever comes first.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  [Arguments(true)]
  [Arguments(false)]
  public async Task RoutesThatDisagree_ResolveTheSameWhicheverIsFoundFirstAsync(bool widerFirst) {
    const string wider = """
      public class CreateProduct {
        [WhizbangId(Versions = UuidVersions.V7 | UuidVersions.V5)]
        public ProductId Id { get; set; }
      }
      """;
    const string narrower = """
      public class RenameProduct {
        [WhizbangId(Versions = UuidVersions.V7)]
        public ProductId Id { get; set; }
      }
      """;
    var source = $"""
      using Whizbang.Core;
      namespace MyApp.Domain;
      {(widerFirst ? wider : narrower)}
      {(widerFirst ? narrower : wider)}
      """;

    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "ProductId.g.cs");
    var whiz038 = result.Diagnostics.Where(d => d.Id == "WHIZ038").ToList();

    await Assert.That(generated).Contains(STRICT_FROM_GUARD);
    await Assert.That(whiz038).Count().IsEqualTo(1);
    await Assert.That(DiagnosticSourceText.Of(whiz038[0])).IsEqualTo("WhizbangId(Versions = UuidVersions.V7 | UuidVersions.V5)");
  }

  // ========================================
  // WHIZ038 / WHIZ029: declarations disagree; the id takes the versions they have in common
  // ========================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task RoutesThatAgreeOnVersions_ReportNothingAsync() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId(Versions = UuidVersions.V7 | UuidVersions.V5)]
      public readonly partial struct ProductId;
      public class CreateProduct {
        [WhizbangId(Versions = UuidVersions.V5 | UuidVersions.V7)]
        public ProductId Id { get; set; }
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "ProductId.g.cs");

    await Assert.That(result.Diagnostics.Where(d => d.Id is "WHIZ038" or "WHIZ029" or "WHIZ043")).IsEmpty();
    await Assert.That(generated).Contains(ALLOW_LIST_GUARD);
  }

  /// <summary>
  /// The type lists v7 and v5 and a property's attribute keeps the default: the id takes the strictest common set
  /// (v7) whichever route is found first, and WHIZ038 points at the list that was narrowed.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task RoutesThatDisagree_TakeTheCommonVersionsAndReportWhiz038AtTheNarrowedListAsync() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      public class CreateProduct {
        [WhizbangId]
        public ProductId Id { get; set; }
      }
      [WhizbangId(Versions = UuidVersions.V7 | UuidVersions.V5)]
      public readonly partial struct ProductId;
      """;

    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "ProductId.g.cs");
    var whiz038 = result.Diagnostics.Where(d => d.Id == "WHIZ038").ToList();

    await Assert.That(generated).Contains(STRICT_FROM_GUARD);
    await Assert.That(whiz038).Count().IsEqualTo(1);
    await Assert.That(whiz038[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
    await Assert.That(whiz038[0].GetMessage(CultureInfo.InvariantCulture)).Contains("ProductId");
    await Assert.That(whiz038[0].GetMessage(CultureInfo.InvariantCulture)).Contains("UUIDv7");
    await Assert.That(DiagnosticSourceText.Of(whiz038[0]))
      .IsEqualTo("WhizbangId(Versions = UuidVersions.V7 | UuidVersions.V5)");
  }

  /// <summary>An <c>AcceptAnyVersion</c> opt-in against a list resolves to the list, and WHIZ029 says so.</summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task AcceptAnyVersionAgainstAList_TakesTheListAndReportsWhiz029Async() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId(AcceptAnyVersion = true)]
      public readonly partial struct ProductId;
      public class CreateProduct {
        [WhizbangId(Versions = UuidVersions.V7 | UuidVersions.V5)]
        public ProductId Id { get; set; }
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "ProductId.g.cs");
    var whiz029 = result.Diagnostics.Where(d => d.Id == "WHIZ029").ToList();

    await Assert.That(generated).Contains(ALLOW_LIST_GUARD);
    await Assert.That(whiz029).Count().IsEqualTo(1);
    await Assert.That(whiz029[0].GetMessage(CultureInfo.InvariantCulture)).Contains("UUIDv5 or UUIDv7");
    await Assert.That(result.Diagnostics.Where(d => d.Id == "WHIZ038")).IsEmpty();
  }
}
