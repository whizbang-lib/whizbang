// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// <c>[WhizbangId(AcceptAnyVersion = true)]</c> generates an id that takes any non-empty Guid through the checked
/// entry points: <c>From</c>, <c>Parse</c>, the explicit cast and the JSON converter. The constructor and the
/// <c>Value</c> initializer stay unchecked, as on every id: they are the load path for EF Core and the persistence
/// converter. Without the option, the generated id keeps today's UUIDv7 rule. When two <c>[WhizbangId]</c>
/// attributes for the same id disagree on the option, WHIZ029 reports each opt-in that is ignored.
/// </summary>
/// <remarks>
/// Name-derived ids (v5, v8), hash-derived ids and ids issued by an external identity provider (v4) are not v7 and
/// cannot become v7, so a strict id cannot model them. The option is per id type, and <c>New()</c> still produces
/// v7 either way.
/// </remarks>
/// <code-under-test>src/Whizbang.Generators/WhizbangIdGenerator.cs</code-under-test>
[Category("SourceGenerators")]
public class WhizbangIdAcceptAnyVersionTests {

  private const string STRICT_FROM_GUARD = "if (value.Version != 7)";
  private const string STRICT_TRACKED_GUARD = "if (!tracked.IsTimeOrdered)";
  private const string NON_EMPTY_GUARD = "_requireNonEmpty(";

  [RequiresAssemblyFiles()]
  private static string _generatedId(string source, string typeName = "ProductId") {
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);
    return GeneratorTestHelper.GetGeneratedSource(result, $"{typeName}.g.cs")
      ?? throw new InvalidOperationException($"{typeName}.g.cs was not generated");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ExplicitType_WithAcceptAnyVersion_ValidatesNonEmptyInsteadOfVersionAsync() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId(AcceptAnyVersion = true)]
      public readonly partial struct ProductId;
      """;

    var generated = _generatedId(source);

    await Assert.That(generated).DoesNotContain(STRICT_FROM_GUARD);
    await Assert.That(generated).DoesNotContain(STRICT_TRACKED_GUARD);
    await Assert.That(generated).Contains(
      "public static ProductId From(Guid value) => new(TrackedGuid.FromExternal(_requireNonEmpty(value, nameof(value))));");
    await Assert.That(generated).Contains("_requireNonEmpty(tracked.Value, nameof(tracked));");
    await Assert.That(generated).Contains("if (value == Guid.Empty)");
    // New() still produces a framework UUIDv7.
    await Assert.That(generated).Contains("public static ProductId New() => new(TrackedGuid.New());");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ExplicitType_WithAcceptAnyVersion_ConstructorAndValueStayUncheckedAsync() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId(AcceptAnyVersion = true)]
      public readonly partial struct ProductId;
      """;

    var generated = _generatedId(source);

    await Assert.That(generated).Contains(
      "public Guid Value { get => _tracked.Value; init => _tracked = TrackedGuid.FromExternal(value); }");
    await Assert.That(generated).Contains("public ProductId(Guid Value) => _tracked = TrackedGuid.FromExternal(Value);");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ExplicitType_WithoutOption_KeepsTheUuidV7RuleAsync() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId]
      public readonly partial struct ProductId;
      """;

    var generated = _generatedId(source);

    await Assert.That(generated).Contains(STRICT_FROM_GUARD);
    await Assert.That(generated).Contains(STRICT_TRACKED_GUARD);
    await Assert.That(generated).DoesNotContain(NON_EMPTY_GUARD);
    await Assert.That(generated).Contains(
      "public Guid Value { get => _tracked.Value; init => _tracked = TrackedGuid.FromExternal(value); }");
    await Assert.That(generated).Contains("public ProductId(Guid Value) => _tracked = TrackedGuid.FromExternal(Value);");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ExplicitType_WithAcceptAnyVersionFalse_KeepsTheUuidV7RuleAsync() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId(AcceptAnyVersion = false)]
      public readonly partial struct ProductId;
      """;

    var generated = _generatedId(source);

    await Assert.That(generated).Contains(STRICT_FROM_GUARD);
    await Assert.That(generated).DoesNotContain(NON_EMPTY_GUARD);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PropertyDiscovery_HonorsAcceptAnyVersionAsync() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      public partial struct ProductId;
      public class CreateProduct {
        [WhizbangId(AcceptAnyVersion = true)]
        public ProductId Id { get; set; }
      }
      """;

    var generated = _generatedId(source);

    await Assert.That(generated).DoesNotContain(STRICT_FROM_GUARD);
    await Assert.That(generated).Contains(NON_EMPTY_GUARD);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ParameterDiscovery_HonorsAcceptAnyVersionAsync() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      public partial struct ProductId;
      public record CreateProduct([WhizbangId(AcceptAnyVersion = true)] ProductId Id);
      """;

    var generated = _generatedId(source);

    await Assert.That(generated).DoesNotContain(STRICT_FROM_GUARD);
    await Assert.That(generated).Contains(NON_EMPTY_GUARD);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task WithAcceptAnyVersion_GeneratedCodeCompilesAsync() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId(AcceptAnyVersion = true)]
      public readonly partial struct ProductId;
      """;

    var (_, output) = GeneratorTestHelper.RunGeneratorWithFullReferences(new WhizbangIdGenerator(), source);
    var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

    await Assert.That(errors).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task WithAcceptAnyVersion_JsonConverterStillReadsThroughFromAsync() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId(AcceptAnyVersion = true)]
      public readonly partial struct ProductId;
      """;

    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);
    var converter = GeneratorTestHelper.GetGeneratedSource(result, "ProductIdJsonConverter.g.cs");

    await Assert.That(converter).IsNotNull();
    await Assert.That(converter).Contains("ProductId.From(Guid.Parse(");
  }

  // ========================================
  // WHIZ029: discovery routes disagree on AcceptAnyVersion
  // ========================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task RoutesThatAgreeOnAcceptAnyVersion_ReportNoWhiz029Async() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId(AcceptAnyVersion = true)]
      public readonly partial struct ProductId;
      public class CreateProduct {
        [WhizbangId(AcceptAnyVersion = true)]
        public ProductId Id { get; set; }
      }
      public record UpdateProduct([WhizbangId(AcceptAnyVersion = true)] ProductId Id);
      """;

    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "ProductId.g.cs");

    await Assert.That(result.Diagnostics.Where(d => d.Id == "WHIZ029")).IsEmpty();
    await Assert.That(generated).Contains(NON_EMPTY_GUARD);
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task StrictRoutesOnly_ReportNoWhiz029Async() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId]
      public readonly partial struct ProductId;
      public class CreateProduct {
        [WhizbangId]
        public ProductId Id { get; set; }
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    await Assert.That(result.Diagnostics.Where(d => d.Id == "WHIZ029")).IsEmpty();
  }

  /// <summary>
  /// The type opts in and a property's attribute does not: the id keeps the UUIDv7 rule whichever route is
  /// discovered first, and WHIZ029 points at the opt-in that is ignored.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task RoutesThatDisagree_KeepTheUuidV7RuleAndReportWhiz029AtTheOptInAsync() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId(AcceptAnyVersion = true)]
      public readonly partial struct ProductId;
      public class CreateProduct {
        [WhizbangId]
        public ProductId Id { get; set; }
      }
      """;

    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "ProductId.g.cs");
    var whiz029 = result.Diagnostics.Where(d => d.Id == "WHIZ029").ToList();

    await Assert.That(generated).Contains(STRICT_FROM_GUARD);
    await Assert.That(generated).DoesNotContain(NON_EMPTY_GUARD);
    await Assert.That(whiz029).Count().IsEqualTo(1);
    await Assert.That(whiz029[0].Severity).IsEqualTo(DiagnosticSeverity.Warning);
    await Assert.That(whiz029[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("ProductId");
    // Reported at the type's opt-in, not at the property's [WhizbangId].
    await Assert.That(DiagnosticSourceText.Of(whiz029[0])).IsEqualTo("WhizbangId(AcceptAnyVersion = true)");
  }

  /// <summary>
  /// The same disagreement with the routes the other way round (the property opts in, the type does not), and
  /// with two opt-ins: the outcome is the same, and every ignored opt-in is reported.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task RoutesThatDisagree_ReportEveryIgnoredOptInWhateverTheOrderAsync() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      public record UpdateProduct([WhizbangId(AcceptAnyVersion = true)] ProductId Id);
      public class CreateProduct {
        [WhizbangId(AcceptAnyVersion = true)]
        public ProductId Id { get; set; }
      }
      [WhizbangId]
      public readonly partial struct ProductId;
      """;

    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "ProductId.g.cs");

    await Assert.That(generated).Contains(STRICT_FROM_GUARD);
    await Assert.That(result.Diagnostics.Where(d => d.Id == "WHIZ029")).Count().IsEqualTo(2);
  }
}
