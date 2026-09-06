using System;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Generators.Utilities;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage-focused tests for <see cref="PinnedIdRegistryGenerator"/>, complementing
/// <c>tests/Whizbang.Generators.Tests/PinnedIdRegistryGeneratorTests.cs</c>. That file exercises
/// the happy paths (events/commands/perspectives with a valid pinned id); these tests target the
/// non-public-type exclusion, the blank-pinned-id exclusion, and the otherwise-unread
/// <c>PinnedIdInfo.Kind</c> property.
/// </summary>
/// <remarks>
/// Line 46 (<c>semanticModel.GetDeclaredSymbol(...) is not INamedTypeSymbol typeSymbol =&gt; return
/// null</c>) is not covered here. As established in earlier coverage rounds, <c>GetDeclaredSymbol</c>
/// always returns a real symbol for a <c>ClassDeclarationSyntax</c>/<c>RecordDeclarationSyntax</c>/
/// <c>StructDeclarationSyntax</c> node — this is a defensive Roslyn-contract guard with no reachable
/// path in a compiling program, not a gap in these tests.
/// </remarks>
public class PinnedIdRegistryGeneratorCoverageTests {
  private static readonly Type _pinnedIdInfoType = typeof(AttributeArgNamingHelper).Assembly
    .GetType("Whizbang.Generators.PinnedIdInfo")
    ?? throw new InvalidOperationException("Whizbang.Generators.PinnedIdInfo not found — check the type's namespace/name.");

  // A non-public pinned type is silently excluded from the generated registry (PinnedIdRegistryGenerator.cs:54):
  // if this regressed and an internal type WERE registered, a consumer that filters IPinnedIdRegistry.GetAll()
  // expecting only publicly-usable message/perspective types would see an entry it cannot construct or reference.
  [Test]
  public async Task Generator_NonPublicPinnedType_ExcludedFromRegistryAsync() {
    const string source = """

      using Whizbang.Core;
      using Whizbang.Core.Attributes;

      namespace MyApp.Events;

      [PinnedId("11111111-1111-1111-1111-111111111111")]
      public record PublicPinnedEvent : IEvent;

      [PinnedId("22222222-2222-2222-2222-222222222222")]
      internal record InternalPinnedEvent : IEvent;

""";

    var result = GeneratorTestHelper.RunGenerator<PinnedIdRegistryGenerator>(source);

    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
    await Assert.That(errors).IsEmpty();

    var registryCode = GeneratorTestHelper.GetGeneratedSource(result, "PinnedIdRegistry.g.cs");
    await Assert.That(registryCode).IsNotNull();
    await Assert.That(registryCode!).Contains("typeof(global::MyApp.Events.PublicPinnedEvent)")
      .Because("the public pinned event must still be registered");
    await Assert.That(registryCode!).DoesNotContain("InternalPinnedEvent")
      .Because("a non-public type must never be exposed through the generated (public) pinned-id registry");
  }

  // A [PinnedId("")] (or whitespace-only) value is treated as absent, so the type is silently
  // excluded (PinnedIdRegistryGenerator.cs:84-85) — exactly like having no [PinnedId] at all. If this
  // regressed and an empty-string id were accepted, GetPinnedId(typeof(...)) would return "" (an
  // invalid identity) instead of null, and every event stored under it would fail the round-trip
  // through a real pinned-id lookup with no compiler error to catch the mistake.
  [Test]
  public async Task Generator_WhitespacePinnedId_ExcludedFromRegistryAsync() {
    const string source = """

      using Whizbang.Core;
      using Whizbang.Core.Attributes;

      namespace MyApp.Events;

      [PinnedId("33333333-3333-3333-3333-333333333333")]
      public record ValidPinnedEvent : IEvent;

      [PinnedId("   ")]
      public record BlankPinnedEvent : IEvent;

""";

    var result = GeneratorTestHelper.RunGenerator<PinnedIdRegistryGenerator>(source);

    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
    await Assert.That(errors).IsEmpty();

    var registryCode = GeneratorTestHelper.GetGeneratedSource(result, "PinnedIdRegistry.g.cs");
    await Assert.That(registryCode).IsNotNull();
    await Assert.That(registryCode!).Contains("typeof(global::MyApp.Events.ValidPinnedEvent)")
      .Because("a type with a genuinely non-blank pinned id must still be registered");
    await Assert.That(registryCode!).DoesNotContain("BlankPinnedEvent")
      .Because("a whitespace-only [PinnedId] value carries no usable identity and must be treated as unpinned");
  }

  // PinnedIdInfo is internal (this project deliberately does not use InternalsVisibleTo for the
  // generators assembly — see src/Whizbang.Generators/AssemblyInfo.cs, it conflicts with PolySharp
  // polyfills), and its Kind is never read anywhere in PinnedIdRegistryGenerator's own code path — only
  // written at discovery time. Reached via reflection here instead of driving the full generator
  // pipeline, matching the pattern established for other internal generator types under this
  // constraint (see TypeNameHelperCoverageTests / CompileTimeMessageClassificationCoverageTests).
  // If this getter ever stopped returning the constructor-supplied value, a future diagnostic or tool
  // that classifies a discovered pinned type (command/event/perspective) would silently read back the
}
