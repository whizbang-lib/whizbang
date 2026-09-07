using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage-focused tests for AutoPopulateDiscoveryGenerator targeting: the scope-alias
/// fallback when PerspectiveScope (or its [JsonPropertyName] alias) can't be resolved, an
/// unrelated attribute never being mistaken for one of the five populate attributes, the
/// fill-if-empty fallback for property types other than Guid/Guid?/string/string?, and
/// assembly-name sanitization for names containing dots/hyphens. Complements
/// AutoPopulateDiscoveryGeneratorTests.cs.
/// </summary>
public class AutoPopulateDiscoveryGeneratorCoverageTests {
  /// <summary>
  /// Runs AutoPopulateDiscoveryGenerator against <paramref name="source"/> in an ISOLATED
  /// compilation that does NOT reference the real Whizbang.Core assembly (unlike
  /// <c>GeneratorTestHelper.RunGenerator</c>, which always adds it -- a cref cannot name that
  /// overload because its signature contains a tuple type). Needed to control whether "Whizbang.Core.Lenses.PerspectiveScope"
  /// resolves at all, and to control the compiling assembly's name.
  /// </summary>
  [RequiresAssemblyFiles()]
  private static GeneratorDriverRunResult _runIsolated(string source, string assemblyName = "IsolatedTestAssembly") {
    var syntaxTree = CSharpSyntaxTree.ParseText(source);
    var assemblyPath = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
    var references = new List<MetadataReference> {
      MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
      MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Runtime.dll")),
      MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Collections.dll")),
      MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.Linq.dll")),
      MetadataReference.CreateFromFile(Path.Combine(assemblyPath, "System.ComponentModel.Primitives.dll")),
    };

    var compilation = CSharpCompilation.Create(
        assemblyName: assemblyName,
        syntaxTrees: [syntaxTree],
        references: references,
        options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    var driver = CSharpGeneratorDriver.Create(new AutoPopulateDiscoveryGenerator());
    driver = (CSharpGeneratorDriver)driver.RunGenerators(compilation);
    return driver.GetRunResult();
  }

  // ==================== Scope-alias resolution fallback ====================

  /// <summary>
  /// If "Whizbang.Core.Lenses.PerspectiveScope" can't be resolved at all (e.g. a consumer
  /// assembly compiled against a Core surface that doesn't expose it), context extraction must
  /// still fall back to the raw "UserId"/"TenantId" property names instead of leaving the
  /// generated extractor with no key to look up at all — the alternative is every
  /// [PopulateFromContext] property on every message silently staying null forever.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ScopeTypeNotResolvable_FallsBackToPropertyNameAliasesAsync() {
    const string source = """
        namespace Whizbang.Core.Attributes {
          public class PopulateTimestampAttribute : System.Attribute {
            public PopulateTimestampAttribute(int kind) { }
          }
        }

        namespace TestNamespace {
          public class SentEvent {
            [Whizbang.Core.Attributes.PopulateTimestamp(0)]
            public System.DateTimeOffset SentAt { get; set; }
          }
        }
        """;

    var result = _runIsolated(source);

    var populator = GeneratorTestHelper.GetGeneratedSource(result, "AutoPopulatePopulator.g.cs");
    await Assert.That(populator).IsNotNull();
    await Assert.That(populator!).Contains("_extractScopeValue(hop, \"UserId\", \"UserId\")")
      .Because("with no PerspectiveScope type to read a [JsonPropertyName] alias from, the resolved alias must equal the fallback property name");
    await Assert.That(populator!).Contains("_extractScopeValue(hop, \"TenantId\", \"TenantId\")");
  }

  /// <summary>
  /// If PerspectiveScope's UserId/TenantId properties exist but carry no usable
  /// [JsonPropertyName] (missing, or present with a blank value), the resolved alias must still
  /// fall back to the property name rather than resolve to an empty/garbage key that can never
  /// match anything in the serialized scope.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_ScopePropertyWithoutUsableJsonAlias_FallsBackToPropertyNameAsync() {
    const string source = """
        namespace System.Text.Json.Serialization {
          public class JsonPropertyNameAttribute : System.Attribute {
            public JsonPropertyNameAttribute(string name) { }
          }
        }

        namespace Whizbang.Core.Lenses {
          public class PerspectiveScope {
            [System.Text.Json.Serialization.JsonPropertyName("")]
            public string? UserId { get; set; }
            [System.Text.Json.Serialization.JsonPropertyName("")]
            public string? TenantId { get; set; }
          }
        }

        namespace Whizbang.Core.Attributes {
          public class PopulateTimestampAttribute : System.Attribute {
            public PopulateTimestampAttribute(int kind) { }
          }
        }

        namespace TestNamespace {
          public class SentEvent {
            [Whizbang.Core.Attributes.PopulateTimestamp(0)]
            public System.DateTimeOffset SentAt { get; set; }
          }
        }
        """;

    var result = _runIsolated(source);

    var populator = GeneratorTestHelper.GetGeneratedSource(result, "AutoPopulatePopulator.g.cs");
    await Assert.That(populator).IsNotNull();
    await Assert.That(populator!).Contains("_extractScopeValue(hop, \"UserId\", \"UserId\")")
      .Because("a [JsonPropertyName(\"\")] is present but carries no usable alias value, so resolution must fall through to the property name");
    await Assert.That(populator!).Contains("_extractScopeValue(hop, \"TenantId\", \"TenantId\")");
  }

  // ==================== Unrelated attributes are ignored ====================

  /// <summary>
  /// If an ordinary, unrelated attribute like [Obsolete] were ever mistaken for one of the five
  /// populate attributes, a completely unrelated property would silently start being overwritten
  /// by the dispatch pipeline on every send — data loss with no attribute anyone applied on
  /// purpose. A sibling property carrying a REAL populate attribute must still be discovered.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_PropertyWithUnrelatedAttribute_ProducesNoRegistrationAsync() {
    const string source = """
        using System;
        using Whizbang.Core.Attributes;

        namespace TestApp;

        public class ArchiveNotice {
          [Obsolete]
          public DateTimeOffset ArchivedAt { get; set; }

          [PopulateTimestamp(TimestampKind.SentAt)]
          public DateTimeOffset SentAt { get; set; }
        }
        """;

    var result = GeneratorTestHelper.RunGenerator<AutoPopulateDiscoveryGenerator>(source);

    var registry = GeneratorTestHelper.GetGeneratedSource(result, "AutoPopulateRegistry.g.cs");
    await Assert.That(registry).IsNotNull();
    await Assert.That(registry!).Contains("PropertyName = \"SentAt\"");
    await Assert.That(registry!).DoesNotContain("ArchivedAt")
      .Because("[Obsolete] is not one of the five populate attributes and must not produce a registration");
  }

  // ==================== Fill-if-empty fallback for other property types ====================

  /// <summary>
  /// Fill-if-empty only knows how to detect "already has a value" for Guid/Guid?/string/string?
  /// targets. A [PopulateFromContext] property of any OTHER type (there is no attribute-level
  /// restriction on target type) must still be populated — falling through to an unconditional
  /// overwrite rather than silently doing nothing on every dispatch because no fill guard exists
  /// for its type.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_ContextPopulatedNonGuidNonStringProperty_OverwritesUnconditionallyAsync() {
    const string source = """
        using Whizbang.Core.Attributes;

        namespace TestApp;

        public class TaggedCommand {
          [PopulateFromContext(ContextKind.UserId)]
          public object Note { get; set; } = new object();
        }
        """;

    var result = GeneratorTestHelper.RunGenerator<AutoPopulateDiscoveryGenerator>(source);

    var populator = GeneratorTestHelper.GetGeneratedSource(result, "AutoPopulatePopulator.g.cs");
    await Assert.That(populator).IsNotNull();
    await Assert.That(populator!).Contains("m.Note = _extractUserId(hop);")
      .Because("a property type with no fill-guard must still be populated via an unconditional assignment, not silently skipped");
  }

  // ==================== Assembly-name sanitization ====================

  /// <summary>
  /// The generated registry/populator class names are derived from the compiling assembly's
  /// name. Real assembly names commonly contain dots (e.g. "Contoso.Orders") and hyphens (e.g.
  /// NuGet-style package names) — emitting either verbatim produces a class name that fails to
  /// compile for every consumer whose assembly name isn't a single bare identifier.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task Generator_AssemblyNameWithDotsAndHyphens_SanitizesToValidIdentifierAsync() {
    const string source = """
        namespace TestNamespace;

        public class PlainType { }
        """;

    var result = _runIsolated(source, "My.Test-Assembly");

    var registry = GeneratorTestHelper.GetGeneratedSource(result, "AutoPopulateRegistry.g.cs");
    await Assert.That(registry).IsNotNull();
    await Assert.That(registry!).Contains("GeneratedAutoPopulateRegistry_My_Test_Assembly")
      .Because("dots and hyphens are not legal in a C# identifier and must be replaced, not dropped or left in place");
    await Assert.That(registry!).Contains("AutoPopulateRegistryInitializer_My_Test_Assembly");
  }
}
