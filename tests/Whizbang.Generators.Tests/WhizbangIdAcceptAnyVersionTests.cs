// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// <c>[WhizbangId(AcceptAnyVersion = true)]</c> generates an id that takes any non-empty Guid through every way
/// in: <c>From</c>, <c>Parse</c>, the explicit cast, the JSON converter, the constructor and the <c>Value</c>
/// initializer. Without it, the generated id keeps today's UUIDv7 rule.
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
  public async Task ExplicitType_WithAcceptAnyVersion_ConstructorAndValueAgreeWithFromAsync() {
    const string source = """
      using Whizbang.Core;
      namespace MyApp.Domain;
      [WhizbangId(AcceptAnyVersion = true)]
      public readonly partial struct ProductId;
      """;

    var generated = _generatedId(source);

    await Assert.That(generated).Contains(
      "public Guid Value { get => _tracked.Value; init => _tracked = TrackedGuid.FromExternal(_requireNonEmpty(value, nameof(Value))); }");
    await Assert.That(generated).Contains(
      "public ProductId(Guid Value) => _tracked = TrackedGuid.FromExternal(_requireNonEmpty(Value, nameof(Value)));");
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
}
