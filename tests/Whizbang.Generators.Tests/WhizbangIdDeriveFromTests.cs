// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Every generated strong id has <c>DeriveFrom(handling, purpose)</c> (#2604): the generator supplies it on every route
/// (a struct, a property, a parameter; with or without <c>AcceptAnyVersion</c>), so no author has to remember to add it.
/// A pinned id (<c>[PinnedId]</c> on the struct, or <c>[WhizbangId(PinnedId = ...)]</c> on any route) is baked into the
/// type as a constant and is the only type input of the derivation, so a rename keeps the derived ids. An unpinned id's
/// <c>DeriveFrom</c> throws and does not compile at a call site (WHIZ118). Routes that pin one id differently are WHIZ123.
/// </summary>
/// <code-under-test>src/Whizbang.Generators/WhizbangIdGenerator.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/WhizbangIdDeclaration.cs</code-under-test>
[Category("SourceGenerators")]
public class WhizbangIdDeriveFromTests {
  private const string PIN = "3f2c8a1e-5b7d-4c9e-a1f0-6d2b8e4c7a91";
  private const string OTHER_PIN = "7b1d9e2f-0c3a-4e5b-8f6d-1a2b3c4d5e6f";
  private const string DERIVE_FROM_SIGNATURE =
    "public static ProductId DeriveFrom(global::Whizbang.Core.Messaging.Handling handling, string purpose)";

  [RequiresAssemblyFiles()]
  private static string _generatedId(string source, string typeName = "ProductId") {
    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);
    return GeneratorTestHelper.GetGeneratedSource(result, $"{typeName}.g.cs")
      ?? throw new InvalidOperationException($"{typeName}.g.cs was not generated");
  }

  /// <summary>Every way an id can be declared, pinned and not, with and without AcceptAnyVersion.</summary>
  public static IEnumerable<Func<string>> EveryDeclaration() {
    yield return () => "[WhizbangId] public readonly partial struct ProductId;";
    yield return () => "[WhizbangId(AcceptAnyVersion = true)] public readonly partial struct ProductId;";
    yield return () => $"[WhizbangId, Whizbang.Core.Attributes.PinnedId(\"{PIN}\")] public readonly partial struct ProductId;";
    yield return () => $"[WhizbangId(PinnedId = \"{PIN}\")] public readonly partial struct ProductId;";
    yield return () => "public class CreateProduct { [WhizbangId] public ProductId Id { get; set; } }";
    yield return () => $"public class CreateProduct {{ [WhizbangId(PinnedId = \"{PIN}\")] public ProductId Id {{ get; set; }} }}";
    yield return () => "public record CreateProduct([WhizbangId] ProductId Id);";
    yield return () => $"public record CreateProduct([WhizbangId(PinnedId = \"{PIN}\", AcceptAnyVersion = true)] ProductId Id);";
  }

  [Test]
  [MethodDataSource(nameof(EveryDeclaration))]
  [RequiresAssemblyFiles()]
  public async Task EveryGeneratedId_HasDeriveFromAsync(string declaration) {
    var generated = _generatedId($"using Whizbang.Core;\nnamespace MyApp.Domain;\n{declaration}");

    await Assert.That(generated).Contains(DERIVE_FROM_SIGNATURE)
      .Because("the generator supplies DeriveFrom on every [WhizbangId] type, whatever route declares it");
  }

  [Test]
  [MethodDataSource(nameof(EveryDeclaration))]
  [RequiresAssemblyFiles()]
  public async Task EveryGeneratedId_CompilesWithItsDeriveFromAsync(string declaration) {
    var (_, output) = GeneratorTestHelper.RunGeneratorWithFullReferences(
      new WhizbangIdGenerator(), $"using Whizbang.Core;\nnamespace MyApp.Domain;\n{declaration}");

    await Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PinnedOnTheStruct_BakesThePinAndDerivesFromItAsync() {
    var generated = _generatedId($$"""
      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      namespace MyApp.Domain;
      [WhizbangId]
      [PinnedId("{{PIN.ToUpperInvariant()}}")]
      public readonly partial struct ProductId;
      """);

    await Assert.That(generated).Contains($"public const string PinnedId = \"{PIN}\";")
      .Because("the pin is normalized to its lowercase hyphenated form, so its spelling takes no part in the ids");
    await Assert.That(generated).Contains(
      "new(TrackedGuid.FromExternal(global::Whizbang.Core.Messaging.StrongIdDerivation.Derive(handling, PinnedId, purpose, \"ProductId\")))");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task PinnedOnAProperty_BakesThePinAsync() {
    var generated = _generatedId($$"""
      using Whizbang.Core;
      namespace MyApp.Domain;
      public class CreateProduct {
        [WhizbangId(PinnedId = "{{PIN}}")]
        public ProductId Id { get; set; }
      }
      """);

    await Assert.That(generated).Contains($"public const string PinnedId = \"{PIN}\";");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Unpinned_DeriveFromThrowsAndHasNoPinAsync() {
    var generated = _generatedId("""
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId]
      public readonly partial struct ProductId;
      """);

    await Assert.That(generated).DoesNotContain("public const string PinnedId");
    await Assert.That(generated).Contains("throw global::Whizbang.Core.Messaging.StrongIdDerivation.NotPinned(\"ProductId\");");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task APinThatIsNotAGuid_LeavesTheIdUnpinnedAsync() {
    // WHIZ112 reports the value; the id stays unpinned rather than deriving from a value the analyzer refuses
    var generated = _generatedId("""
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId(PinnedId = "not-a-guid")]
      public readonly partial struct ProductId;
      """);

    await Assert.That(generated).DoesNotContain("public const string PinnedId");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task OneRoutePins_ThePinAppliesWithoutWhiz123Async() {
    var source = $$"""
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId]
      public readonly partial struct ProductId;
      public class CreateProduct {
        [WhizbangId(PinnedId = "{{PIN}}")]
        public ProductId Id { get; set; }
      }
      public record UpdateProduct([WhizbangId(PinnedId = "{{PIN.ToUpperInvariant()}}")] ProductId Id);
      """;

    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "ProductId.g.cs");

    await Assert.That(result.Diagnostics.Where(d => d.Id == "WHIZ123")).IsEmpty()
      .Because("one pin, spelled in two cases, is one pin");
    await Assert.That(generated).Contains($"public const string PinnedId = \"{PIN}\";");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task RoutesThatPinDifferently_ReportWhiz123AndKeepTheLowestPinAsync() {
    var source = $$"""
      using Whizbang.Core;
      namespace MyApp.Domain;
      public class CreateProduct {
        [WhizbangId(PinnedId = "{{OTHER_PIN}}")]
        public ProductId Id { get; set; }
      }
      public record UpdateProduct([WhizbangId(PinnedId = "{{PIN}}")] ProductId Id);
      """;

    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);
    var generated = GeneratorTestHelper.GetGeneratedSource(result, "ProductId.g.cs");
    var whiz123 = result.Diagnostics.Where(d => d.Id == "WHIZ123").ToList();

    await Assert.That(whiz123).Count().IsEqualTo(1);
    await Assert.That(whiz123[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(whiz123[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains(OTHER_PIN);
    await Assert.That(generated).Contains($"public const string PinnedId = \"{PIN}\";")
      .Because("the kept pin does not depend on the order the routes are discovered in");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task TwoNamesWithOnePin_DeriveThroughTheSameInput_SoARenameKeepsTheIdsAsync() {
    var before = _generatedId($"using Whizbang.Core;\nnamespace MyApp.Domain;\n[WhizbangId(PinnedId = \"{PIN}\")] public readonly partial struct ProductId;");
    var after = _generatedId(
      $"using Whizbang.Core;\nnamespace MyApp.Catalog;\n[WhizbangId(PinnedId = \"{PIN}\")] public readonly partial struct ItemId;", "ItemId");

    await Assert.That(before).Contains($"public const string PinnedId = \"{PIN}\";");
    await Assert.That(after).Contains($"public const string PinnedId = \"{PIN}\";");
    await Assert.That(after).Contains("StrongIdDerivation.Derive(handling, PinnedId, purpose, \"ItemId\")")
      .Because("the type's name reaches the derivation only for its exception messages, which take no part in the id");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task DeriveFromOutsideAHandling_ThrowsNamingTheTypeAsync() {
    var observed = _runProbe($$"""
      using Whizbang.Core;
      using Whizbang.Core.Attributes;
      namespace App;
      [WhizbangId]
      [PinnedId("{{PIN}}")]
      public readonly partial struct ProductId;
      [WhizbangId]
      public readonly partial struct LooseId;
      public static class Probe {
        public static string Run() {
          var pinned = "";
          var loose = "";
          try { ProductId.DeriveFrom(Whizbang.Core.Messaging.Handling.Current, "p"); } catch (System.InvalidOperationException e) { pinned = e.Message; }
      #pragma warning disable WHIZ118
          try { LooseId.DeriveFrom(Whizbang.Core.Messaging.Handling.Current, "p"); } catch (System.NotSupportedException e) { loose = e.Message; }
      #pragma warning restore WHIZ118
          return pinned + "|" + loose;
        }
      }
      """);

    await Assert.That(observed).Contains("ProductId.DeriveFrom was called outside a receptor handling");
    await Assert.That(observed).Contains("LooseId.DeriveFrom needs a pinned id");
  }

  [RequiresAssemblyFiles()]
  private static string _runProbe(string source) {
    var (_, output) = GeneratorTestHelper.RunGeneratorWithFullReferences(new WhizbangIdGenerator(), source, "DeriveFromProbe");
    using var image = new MemoryStream();
    var emitted = output.Emit(image);
    if (!emitted.Success) {
      throw new InvalidOperationException(
        "The probe does not compile: "
        + string.Join(Environment.NewLine, emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
    }
    var context = new System.Runtime.Loader.AssemblyLoadContext("DeriveFromProbe", isCollectible: true);
    try {
      var assembly = context.LoadFromStream(new MemoryStream(image.ToArray()));
      return (string)assembly.GetType("App.Probe", throwOnError: true)!.GetMethod("Run")!.Invoke(null, null)!;
    } finally {
      context.Unload();
    }
  }
}
