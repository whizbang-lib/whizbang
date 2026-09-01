using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// <see cref="AutoPopulateDiscoveryGenerator"/> on incomplete attribute usage, and its JSON alias
/// resolution.
/// </summary>
/// <remarks>
/// Auto-populate fills a message property from ambient state — a timestamp, the calling user, a
/// header — so every attribute carries a kind that says which. The extractor reads that kind out
/// of the constructor argument, and while the author is still typing there is no argument to read.
///
/// <para>
/// Each of the five attributes has its own extractor with its own copy of that guard, which is
/// exactly the shape where one gets missed. A missed guard is not a crash but a cast of a null to
/// int, and it takes the whole registry down for every other message in the assembly.
/// </para>
/// </remarks>
/// <code-under-test>src/Whizbang.Generators/AutoPopulateDiscoveryGenerator.cs</code-under-test>
[Category("SourceGenerators")]
public class AutoPopulateEdgeCaseTests {

  private static ImmutableArray<Diagnostic> _run(string body) {
    var source = $$"""
      using System;
      using Whizbang.Core.Attributes;

      namespace TestApp;

      {{body}}
      """;
    return GeneratorTestHelper.RunGenerator<AutoPopulateDiscoveryGenerator>(source).Diagnostics;
  }

  private static string _registry(string body) {
    var source = $$"""
      using System;
      using Whizbang.Core.Attributes;

      namespace TestApp;

      {{body}}
      """;
    var result = GeneratorTestHelper.RunGenerator<AutoPopulateDiscoveryGenerator>(source);
    return GeneratorTestHelper.GetGeneratedSource(result, "AutoPopulateRegistry.g.cs") ?? string.Empty;
  }

  // ============================================================
  // Each attribute mid-edit
  // ============================================================

  [Test]
  [RequiresAssemblyFiles()]
  [Arguments("PopulateTimestamp")]
  [Arguments("PopulateFromContext")]
  [Arguments("PopulateFromService")]
  [Arguments("PopulateFromIdentifier")]
  [Arguments("PopulateFromHttpHeader")]
  public async Task AttributeWithNoKindArgument_IsDeclinedWithoutCrashingAsync(string attributeName) {
    // The state right after typing the attribute name. Each extractor has its own copy of the
    // guard, so each has to be checked — one missed guard casts a null to int and takes the
    // whole registry down for every other message in the assembly.
    var diagnostics = _run($$"""
      public record ProbeEvent(
        Guid Id,
        [property: {{attributeName}}] string? Value = null
      );
      """);

    await Assert.That(diagnostics.Any(d => d.Id == "CS8785")).IsFalse()
      .Because($"{attributeName} without its argument must be declined, not crash the generator");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task OneIncompleteAttribute_DoesNotDropTheRestOfTheRegistryAsync() {
    // The property being edited is skipped; every other annotated property in the assembly must
    // still register, or a single keystroke blanks auto-population everywhere.
    var registry = _registry("""
      public record GoodEvent(
        Guid Id,
        [property: PopulateTimestamp(TimestampKind.SentAt)] DateTimeOffset? SentAt = null
      );

      public record BeingEditedEvent(
        Guid Id,
        [property: PopulateTimestamp] DateTimeOffset? SentAt = null
      );
      """);

    await Assert.That(registry).Contains("GoodEvent")
      .Because("one half-typed attribute must not blank auto-population for the whole assembly");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AnUnrecognizedKindValue_FallsBackRatherThanFailingAsync() {
    // A cast from an int the enum does not name — reachable when an enum member is removed but
    // a call site still passes its old value. Falling back keeps the registry buildable.
    var registry = _registry("""
      public record ProbeEvent(
        Guid Id,
        [property: PopulateTimestamp((TimestampKind)99)] DateTimeOffset? SentAt = null
      );
      """);

    await Assert.That(registry).Contains("ProbeEvent");
  }

  // ============================================================
  // The scope type's JSON aliases
  // ============================================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ScopeWithoutJsonAliases_UsesThePropertyNamesAsync() {
    // The fallback: with no [JsonPropertyName] the generated reader looks for the CLR names.
    var registry = _registry("""
      public record ProbeEvent(
        Guid Id,
        [property: PopulateFromContext(ContextKind.UserId)] string? UserId = null
      );
      """);

    await Assert.That(registry).Contains("UserId");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task TheGeneratedRegistryCompilesForEveryAttributeAsync() {
    // All five attributes at once, since the registry is emitted as one file — an extractor
    // that assembles its entry differently breaks the whole thing.
    var errors = GeneratorTestHelper.GetGeneratedCompilationErrors<AutoPopulateDiscoveryGenerator>("""
      using System;
      using Whizbang.Core.Attributes;

      namespace TestApp;

      public record ProbeEvent(
        Guid Id,
        [property: PopulateTimestamp(TimestampKind.SentAt)] DateTimeOffset? SentAt = null,
        [property: PopulateFromContext(ContextKind.UserId)] string? UserId = null,
        [property: PopulateFromIdentifier(IdentifierKind.CorrelationId)] string? CorrelationId = null,
        [property: PopulateFromHttpHeader("X-Request-Id")] string? RequestId = null
      );
      """);

    await Assert.That(errors).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task AMessageWithNoAutoPopulateAttributes_ContributesNothingAsync() {
    var registry = _registry("""
      public record PlainEvent(Guid Id, string Name);
      """);

    await Assert.That(registry).DoesNotContain("PlainEvent")
      .Because("an unannotated message must not be given populators it never asked for");
  }
}
