using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Coverage-focused tests for <see cref="MessageRegistryGenerator"/> targeting branches that the
/// primary test suite does not reach: unresolvable dispatcher invocations, the generic-type-argument
/// fallback for null-literal arguments, marker-only perspectives, and the code-docs-map.json /
/// code-tests-map.json enrichment pipeline (driven via the WHIZBANG_DOCS_PATH environment variable).
/// </summary>
public class MessageRegistryGeneratorCoverageTests {
  private const string DOCS_PATH_ENV_VAR = "WHIZBANG_DOCS_PATH";
  private const string DOCS_PATH_PARALLEL_KEY = "WhizbangDocsPathEnvVar";

  // ========================================
  // Dispatcher extraction edge cases
  // ========================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_SendAsyncOnTypeWithoutSuchMember_SkipsUnresolvableInvocationAsync() {
    // Arrange - a SendAsync call whose receiver type has no such member.
    // GetSymbolInfo returns a null Symbol (no candidates), so the generator must
    // skip the invocation instead of crashing (the "is not IMethodSymbol" guard).
    const string source = """

using Whizbang.Core;
using System.Threading.Tasks;

namespace TestNamespace {
  public class CovUnresolvedCommand : ICommand {
    public string Value { get; set; } = "";
  }

  public class UnresolvedCallSiteService {
    public async Task RunAsync() {
      var target = "not-a-dispatcher";
      // string has no SendAsync member and no extension method is in scope,
      // so this invocation never resolves to an IMethodSymbol.
      await target.SendAsync(new CovUnresolvedCommand());
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - registry is still generated, message is discovered, but the
    // unresolvable call site is not registered as a dispatcher.
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("CovUnresolvedCommand");
    await Assert.That(generatedSource).DoesNotContain("UnresolvedCallSiteService");
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_PublishAsyncWithNullLiteralAndExplicitGeneric_InfersFromTypeArgumentAsync() {
    // Arrange - a null literal has no type (GetTypeInfo(...).Type is null), so the
    // generator must fall back to the method's generic type argument to determine
    // the message type. Note default(TEvent) does NOT exercise this path because a
    // default expression carries the type itself.
    const string source = """

using Whizbang.Core;
using System.Threading.Tasks;

namespace TestNamespace {
  public class CovFallbackEvent : IEvent {
    public string Value { get; set; } = "";
  }

  public class NullLiteralPublisher {
    private readonly IDispatcher _dispatcher;

    public NullLiteralPublisher(IDispatcher dispatcher) {
      _dispatcher = dispatcher;
    }

    public async Task RunAsync() {
      // Explicit generic argument + null literal: argument type is null,
      // forcing the TypeArguments[0] fallback.
      await _dispatcher.PublishAsync<CovFallbackEvent>(null);
    }
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - dispatcher is registered against the generic type argument.
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).Contains("CovFallbackEvent");
    await Assert.That(generatedSource).Contains("NullLiteralPublisher");
    await Assert.That(generatedSource).Contains("RunAsync");
    await Assert.That(generatedSource).Contains("\"\"dispatchers\"\":");
  }

  // ========================================
  // Perspective extraction edge cases
  // ========================================

  [Test]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_MarkerOnlyPerspective_SkipsPerspectiveWithoutEventsAsync() {
    // Arrange - a class implementing only the base marker IPerspectiveFor<TModel>
    // (single type argument, no event types). The generator must skip it because
    // perspectives with zero event types cannot appear in the registry.
    const string source = """

using Whizbang.Core;
using Whizbang.Core.Perspectives;

namespace TestNamespace {
  public class CovMarkerModel {
    public string Id { get; set; } = "";
  }

  public class MarkerOnlyPerspective : IPerspectiveFor<CovMarkerModel> {
  }
}
""";

    // Act
    var result = GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);

    // Assert - registry generated, marker-only perspective excluded.
    var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
    await Assert.That(generatedSource).IsNotNull();
    await Assert.That(generatedSource).DoesNotContain("MarkerOnlyPerspective");
  }

  // ========================================
  // Docs / tests map enrichment (WHIZBANG_DOCS_PATH-driven)
  // ========================================

  [Test]
  [NotInParallel(DOCS_PATH_PARALLEL_KEY)]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_DocsRepoWithMaps_EnrichesRegistryWithDocsUrlAndTestsAsync() {
    // Arrange - a fake docs repository with valid code-docs-map.json and
    // code-tests-map.json. Property names are PascalCase to match the generator's
    // case-sensitive System.Text.Json deserialization contracts.
    const string source = """

using Whizbang.Core;

namespace TestNamespace {
  public class CovDocsCommand : ICommand {
    public string Value { get; set; } = "";
  }
}
""";

    const string docsMapJson = """
{
  "CovDocsCommand": {
    "File": "src/CovDocsCommand.cs",
    "Symbol": "CovDocsCommand",
    "Docs": "https://example.test/docs/cov-docs-command"
  }
}
""";

    // Two test entries so the comma-separator branch in _buildTestEntries is exercised.
    const string testsMapJson = """
{
  "CodeToTests": {
    "CovDocsCommand": [
      {
        "TestFile": "tests/CovDocs/CovDocsCommandFirstTests.cs",
        "TestMethod": "CovDocsCommand_FirstScenario_Async",
        "TestLine": 42,
        "TestClass": "CovDocsCommandFirstTests"
      },
      {
        "TestFile": "tests/CovDocs/CovDocsCommandSecondTests.cs",
        "TestMethod": "CovDocsCommand_SecondScenario_Async",
        "TestLine": 77,
        "TestClass": "CovDocsCommandSecondTests"
      }
    ]
  }
}
""";

    var docsRepoPath = _createDocsRepo(docsMapJson, testsMapJson);
    try {
      // Act
      var result = _runGeneratorWithDocsPath(source, docsRepoPath);

      // Assert - docsUrl and test entries flow through into the registry JSON.
      var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
      await Assert.That(generatedSource).IsNotNull();
      await Assert.That(generatedSource).Contains("CovDocsCommand");
      await Assert.That(generatedSource).Contains("https://example.test/docs/cov-docs-command");
      await Assert.That(generatedSource).Contains("CovDocsCommand_FirstScenario_Async");
      await Assert.That(generatedSource).Contains("CovDocsCommand_SecondScenario_Async");
      await Assert.That(generatedSource).Contains("\"\"testLine\"\": 42");
      await Assert.That(generatedSource).Contains("\"\"testLine\"\": 77");
      await Assert.That(generatedSource).Contains("\"\"testClass\"\": \"\"CovDocsCommandFirstTests\"\"");
      await Assert.That(generatedSource).Contains("\"\"testFile\"\": \"\"tests/CovDocs/CovDocsCommandSecondTests.cs\"\"");

      // Map loading succeeded, so no load-failure diagnostics may be reported.
      var diagnosticIds = result.Diagnostics.Select(d => d.Id).ToList();
      await Assert.That(diagnosticIds).DoesNotContain("WHIZ053");
      await Assert.That(diagnosticIds).DoesNotContain("WHIZ054");
    } finally {
      Directory.Delete(docsRepoPath, recursive: true);
    }
  }

  [Test]
  [NotInParallel(DOCS_PATH_PARALLEL_KEY)]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_MalformedMapFiles_ReportsInfoDiagnosticsAndStillGeneratesAsync() {
    // Arrange - both map files exist but contain invalid JSON. The generator must
    // report WHIZ053/WHIZ054 info diagnostics and continue generating the registry
    // without enrichment instead of failing the build.
    const string source = """

using Whizbang.Core;

namespace TestNamespace {
  public class CovBrokenMapsCommand : ICommand {
    public string Value { get; set; } = "";
  }
}
""";

    const string malformedJson = "{ this is not valid json !!!";

    var docsRepoPath = _createDocsRepo(malformedJson, malformedJson);
    try {
      // Act
      var result = _runGeneratorWithDocsPath(source, docsRepoPath);

      // Assert - both load failures surface as diagnostics.
      var diagnosticIds = result.Diagnostics.Select(d => d.Id).ToList();
      await Assert.That(diagnosticIds).Contains("WHIZ053");
      await Assert.That(diagnosticIds).Contains("WHIZ054");

      // Generation still succeeds without enrichment.
      var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
      await Assert.That(generatedSource).IsNotNull();
      await Assert.That(generatedSource).Contains("CovBrokenMapsCommand");
      await Assert.That(generatedSource).Contains("\"\"docsUrl\"\": \"\"\"\"");
    } finally {
      Directory.Delete(docsRepoPath, recursive: true);
    }
  }

  [Test]
  [NotInParallel(DOCS_PATH_PARALLEL_KEY)]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_DocsRepoWithoutMapFiles_GeneratesWithoutEnrichmentAsync() {
    // Arrange - the docs repository path resolves, but neither map file exists.
    // The generator must take the File.Exists early-return branches and emit an
    // unenriched registry with no load-failure diagnostics.
    const string source = """

using Whizbang.Core;

namespace TestNamespace {
  public class CovNoMapsCommand : ICommand {
    public string Value { get; set; } = "";
  }
}
""";

    var docsRepoPath = _createDocsRepo(docsMapJson: null, testsMapJson: null);
    try {
      // Act
      var result = _runGeneratorWithDocsPath(source, docsRepoPath);

      // Assert
      var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
      await Assert.That(generatedSource).IsNotNull();
      await Assert.That(generatedSource).Contains("CovNoMapsCommand");
      await Assert.That(generatedSource).Contains("\"\"docsUrl\"\": \"\"\"\"");

      var diagnosticIds = result.Diagnostics.Select(d => d.Id).ToList();
      await Assert.That(diagnosticIds).DoesNotContain("WHIZ053");
      await Assert.That(diagnosticIds).DoesNotContain("WHIZ054");
    } finally {
      Directory.Delete(docsRepoPath, recursive: true);
    }
  }

  [Test]
  [NotInParallel(DOCS_PATH_PARALLEL_KEY)]
  [RequiresAssemblyFiles()]
  public async Task MessageRegistryGenerator_NullAndEmptyMapContents_GeneratesWithoutEnrichmentAsync() {
    // Arrange - the docs map deserializes to null (JSON literal "null") and the
    // tests map deserializes to an object with a null CodeToTests dictionary plus
    // a null entry array. Exercises the null-coalescing fallbacks in both loaders
    // without triggering the catch blocks.
    const string source = """

using Whizbang.Core;

namespace TestNamespace {
  public class CovEmptyMapsCommand : ICommand {
    public string Value { get; set; } = "";
  }
}
""";

    const string docsMapJson = "null";
    const string testsMapJson = """
{
  "CodeToTests": {
    "CovEmptyMapsCommand": null
  }
}
""";

    var docsRepoPath = _createDocsRepo(docsMapJson, testsMapJson);
    try {
      // Act
      var result = _runGeneratorWithDocsPath(source, docsRepoPath);

      // Assert - no enrichment, no failure diagnostics.
      var generatedSource = GeneratorTestHelper.GetGeneratedSource(result, "MessageRegistry.g.cs");
      await Assert.That(generatedSource).IsNotNull();
      await Assert.That(generatedSource).Contains("CovEmptyMapsCommand");
      await Assert.That(generatedSource).Contains("\"\"docsUrl\"\": \"\"\"\"");

      var diagnosticIds = result.Diagnostics.Select(d => d.Id).ToList();
      await Assert.That(diagnosticIds).DoesNotContain("WHIZ053");
      await Assert.That(diagnosticIds).DoesNotContain("WHIZ054");
    } finally {
      Directory.Delete(docsRepoPath, recursive: true);
    }
  }

  // ========================================
  // Helpers
  // ========================================

  /// <summary>
  /// Creates a temporary fake documentation repository (root + src/assets) and writes the
  /// provided map file contents. Pass null to omit a map file entirely.
  /// </summary>
  private static string _createDocsRepo(string? docsMapJson, string? testsMapJson) {
    var root = Path.Combine(Path.GetTempPath(), $"whizbang-docs-cov-{Guid.NewGuid():N}");
    var assetsPath = Path.Combine(root, "src", "assets");
    Directory.CreateDirectory(assetsPath);

    if (docsMapJson is not null) {
      File.WriteAllText(Path.Combine(assetsPath, "code-docs-map.json"), docsMapJson);
    }
    if (testsMapJson is not null) {
      File.WriteAllText(Path.Combine(assetsPath, "code-tests-map.json"), testsMapJson);
    }

    return root;
  }

  /// <summary>
  /// Runs the generator with WHIZBANG_DOCS_PATH pointing at the given docs repository,
  /// restoring the previous environment variable value afterwards.
  /// </summary>
  [RequiresAssemblyFiles()]
  private static GeneratorDriverRunResult _runGeneratorWithDocsPath(string source, string docsRepoPath) {
    var previousValue = Environment.GetEnvironmentVariable(DOCS_PATH_ENV_VAR);
    Environment.SetEnvironmentVariable(DOCS_PATH_ENV_VAR, docsRepoPath);
    try {
      return GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(source);
    } finally {
      Environment.SetEnvironmentVariable(DOCS_PATH_ENV_VAR, previousValue);
    }
  }
}
