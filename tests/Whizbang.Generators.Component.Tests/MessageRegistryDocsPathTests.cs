// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The message registry finds the documentation checkout from its MSBuild inputs, never from the process (#1180). It
/// used to read an environment variable while generating, so its output varied by machine and a test that set the
/// variable changed what generator tests running beside it produced.
/// </summary>
/// <docs>extending/source-generators/message-registry#documentation-and-test-maps</docs>
[Category("SourceGenerators")]
public class MessageRegistryDocsPathTests {
  private const string DOCS_PATH_VARIABLE = "WHIZBANG_DOCS_PATH";
  private const string DOCS_URL = "fundamentals/events/order-placed#placing";

  private const string SOURCE = """
    using Whizbang.Core;

    namespace Shop;

    public record OrderPlaced : IEvent;
    """;

  /// <summary>The <c>WhizbangDocsPath</c> property names the checkout whose maps enrich the registry.</summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task Registry_WithTheDocsPathProperty_ReadsThatCheckoutsMapAsync() {
    var docs = _docsCheckout();
    try {
      var registry = _registry(new() { ["build_property.WhizbangDocsPath"] = docs });

      await Assert.That(registry).Contains(DOCS_URL);
    } finally {
      Directory.Delete(docs, recursive: true);
    }
  }

  /// <summary>The environment variable is not an input to the generator: setting it changes nothing it produces.</summary>
  [Test]
  [RequiresAssemblyFiles]
  [NotInParallel(DOCS_PATH_VARIABLE)]
  public async Task Registry_WithOnlyTheEnvironmentVariable_IgnoresItAsync() {
    var docs = _docsCheckout();
    var previous = Environment.GetEnvironmentVariable(DOCS_PATH_VARIABLE);
    Environment.SetEnvironmentVariable(DOCS_PATH_VARIABLE, docs);
    try {
      var registry = _registry([]);

      await Assert.That(registry).DoesNotContain(DOCS_URL);
    } finally {
      Environment.SetEnvironmentVariable(DOCS_PATH_VARIABLE, previous);
      Directory.Delete(docs, recursive: true);
    }
  }

  [RequiresAssemblyFiles]
  private static string _registry(Dictionary<string, string> globalOptions) =>
    GeneratorTestHelper.GetGeneratedSource(
      GeneratorTestHelper.RunGenerator<MessageRegistryGenerator>(SOURCE, globalOptions), "MessageRegistry.g.cs")!;

  private static string _docsCheckout() {
    var root = Path.Combine(Path.GetTempPath(), "whizbang-docs-" + Guid.NewGuid().ToString("N"));
    var assets = Path.Combine(root, "src", "assets");
    Directory.CreateDirectory(assets);
    File.WriteAllText(Path.Combine(assets, "code-docs-map.json"),
      $$"""{ "OrderPlaced": { "File": "Shop/OrderPlaced.cs", "Symbol": "OrderPlaced", "Docs": "{{DOCS_URL}}" } }""");
    return root;
  }
}
