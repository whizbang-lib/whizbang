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
/// Writing <c>Versions</c> together with <c>AcceptAnyVersion = true</c> is WHIZ038; an empty list, or declarations
/// with no version in common, is WHIZ039; a declaration whose list is narrowed by another is WHIZ037.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/WhizbangIdGenerator.cs</code-under-test>
[Category("SourceGenerators")]
public class WhizbangIdVersionsTests {

  private const string STRICT_FROM_GUARD = "if (value.Version != 7";
  private const string ALLOW_LIST_GUARD = "if (value.Version is not (5 or 7)";
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
      "throw new ArgumentException($\"ProductId requires UUIDv5 or UUIDv7 but received version {value.Version}\", parameterName);");
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

    await Assert.That(generated).Contains("if (value.Version is not (5)");
    await Assert.That(generated).Contains("ProductId requires UUIDv5 but received version");
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
  // WHIZ038: AcceptAnyVersion and Versions both written
  // ========================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AcceptAnyVersionAndVersions_ReportWhiz038AtTheAttributeAsync() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId(AcceptAnyVersion = true, Versions = UuidVersions.V5)]
      public readonly partial struct ProductId;
      """;

    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);
    var whiz038 = result.Diagnostics.Where(d => d.Id == "WHIZ038").ToList();

    await Assert.That(whiz038).Count().IsEqualTo(1);
    await Assert.That(whiz038[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(whiz038[0].GetMessage(CultureInfo.InvariantCulture)).Contains("ProductId");
    await Assert.That(DiagnosticSourceText.Of(whiz038[0]))
      .IsEqualTo("WhizbangId(AcceptAnyVersion = true, Versions = UuidVersions.V5)");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AcceptAnyVersionFalseWithVersions_ReportsNoWhiz038Async() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId(AcceptAnyVersion = false, Versions = UuidVersions.V7 | UuidVersions.V5)]
      public readonly partial struct ProductId;
      """;

    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    await Assert.That(result.Diagnostics.Where(d => d.Id == "WHIZ038")).IsEmpty();
  }

  // ========================================
  // WHIZ039: no version accepted
  // ========================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task EmptyVersions_ReportWhiz039Async() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId(Versions = UuidVersions.None)]
      public readonly partial struct ProductId;
      """;

    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);
    var whiz039 = result.Diagnostics.Where(d => d.Id == "WHIZ039").ToList();

    await Assert.That(whiz039).Count().IsEqualTo(1);
    await Assert.That(whiz039[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(whiz039[0].GetMessage(CultureInfo.InvariantCulture)).Contains("ProductId");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task RoutesWithNoVersionInCommon_ReportWhiz039Async() {
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

    await Assert.That(result.Diagnostics.Where(d => d.Id == "WHIZ039")).Count().IsEqualTo(1);
    await Assert.That(result.Diagnostics.Where(d => d.Id == "WHIZ037")).IsEmpty();
  }

  // ========================================
  // WHIZ037 / WHIZ029: declarations disagree; the id takes the versions they have in common
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

    await Assert.That(result.Diagnostics.Where(d => d.Id is "WHIZ037" or "WHIZ029" or "WHIZ039")).IsEmpty();
    await Assert.That(generated).Contains(ALLOW_LIST_GUARD);
  }

  /// <summary>
  /// The type lists v7 and v5 and a property's attribute keeps the default: the id takes the strictest common set
  /// (v7) whichever route is found first, and WHIZ037 points at the list that was narrowed.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task RoutesThatDisagree_TakeTheCommonVersionsAndReportWhiz037AtTheNarrowedListAsync() {
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
    var whiz037 = result.Diagnostics.Where(d => d.Id == "WHIZ037").ToList();

    await Assert.That(generated).Contains(STRICT_FROM_GUARD);
    await Assert.That(whiz037).Count().IsEqualTo(1);
    await Assert.That(whiz037[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
    await Assert.That(whiz037[0].GetMessage(CultureInfo.InvariantCulture)).Contains("ProductId");
    await Assert.That(whiz037[0].GetMessage(CultureInfo.InvariantCulture)).Contains("UUIDv7");
    await Assert.That(DiagnosticSourceText.Of(whiz037[0]))
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
    await Assert.That(result.Diagnostics.Where(d => d.Id == "WHIZ037")).IsEmpty();
  }
}
