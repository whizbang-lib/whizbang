using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage-focused tests for WhizbangIdGenerator targeting the
/// <c>SuppressDuplicateWarning</c> extraction in the type-based and parameter-based discovery
/// paths. Complements WhizbangIdGeneratorTests.cs, whose existing
/// <c>Generator_WithCollisionSuppressed_NoWarningAsync</c> test already exercises the equivalent
/// block in the property-based path.
/// </summary>
/// <remarks>
/// Four lines are not covered here, each because the guard is unreachable from a valid compilation:
/// <list type="bullet">
/// <item>Line 112 (<c>_extractTypeBasedId</c>), line 175 (<c>_extractPropertyBasedId</c>), and line
/// 236 (<c>_extractParameterBasedId</c>) each guard <c>GetDeclaredSymbol(...)</c> returning null or
/// the wrong symbol kind. The predicates for all three pipelines only ever hand the transform a
/// <c>StructDeclarationSyntax</c>, <c>PropertyDeclarationSyntax</c>, or <c>ParameterSyntax</c>
/// respectively, and Roslyn guarantees <c>GetDeclaredSymbol</c> returns a matching, non-null symbol
/// for each of those node kinds. API-guaranteed, not a reachable branch.</item>
/// <item>Line 354 (<c>if (result is null) continue;</c> in <c>_separateValidIdsFromErrors</c>) —
/// the array it iterates is built in <c>Initialize</c> from three pipelines that are each already
/// filtered with <c>.Where(static info =&gt; info is not null)</c> before being collected into
/// <c>allFlat</c>. Every element <c>_separateValidIdsFromErrors</c> sees is therefore already
/// non-null; the null check is dead by construction, tracing back to the upstream filters.</item>
/// </list>
/// </remarks>
public class WhizbangIdGeneratorCoverageTests {
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_TypeBasedSuppressDuplicateWarning_SuppressesCollisionWarningAsync() {
    // Explicit-type discovery must honor SuppressDuplicateWarning just like property/parameter
    // discovery does; otherwise a deliberately duplicated ID name declared via [WhizbangId] on a
    // struct would still trip the WHIZ024 warning it was explicitly told to suppress.
    const string source = """
            using Whizbang.Core;

            namespace MyApp.Domain;

            [WhizbangId(SuppressDuplicateWarning = true)]
            public readonly partial struct GadgetId;

            namespace MyApp.Commands;

            [WhizbangId(Namespace = "MyApp.Commands")]
            public readonly partial struct GadgetId;
            """;

    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    var warning = result.Diagnostics.FirstOrDefault(d => d.Id == "WHIZ024");
    await Assert.That(warning).IsNull()
      .Because("SuppressDuplicateWarning on the type-based declaration must suppress the collision warning");

    await Assert.That(GeneratorTestHelper.GetGeneratedSource(result, "MyAppDomain.GadgetId.g.cs")).IsNotNull();
    await Assert.That(GeneratorTestHelper.GetGeneratedSource(result, "MyAppCommands.GadgetId.g.cs")).IsNotNull();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ParameterBasedSuppressDuplicateWarning_SuppressesCollisionWarningAsync() {
    // Parameter-based discovery (an ID declared via [WhizbangId] on a primary constructor
    // parameter) must also honor SuppressDuplicateWarning; otherwise a consumer using this pattern
    // has no way to silence an intentional cross-namespace name collision.
    const string source = """
            using Whizbang.Core;

            namespace MyApp.Domain;

            [WhizbangId]
            public readonly partial struct WidgetId;

            namespace MyApp.Commands;

            public record CreateWidgetCommand(
              [WhizbangId(Namespace = "MyApp.Commands", SuppressDuplicateWarning = true)] WidgetId Id,
              string Name
            );
            """;

    var result = GeneratorTestHelper.RunGenerator<WhizbangIdGenerator>(source);

    var warning = result.Diagnostics.FirstOrDefault(d => d.Id == "WHIZ024");
    await Assert.That(warning).IsNull()
      .Because("SuppressDuplicateWarning on the parameter-based declaration must suppress the collision warning");

    await Assert.That(GeneratorTestHelper.GetGeneratedSource(result, "MyAppDomain.WidgetId.g.cs")).IsNotNull();
    await Assert.That(GeneratorTestHelper.GetGeneratedSource(result, "MyAppCommands.WidgetId.g.cs")).IsNotNull();
  }
}
