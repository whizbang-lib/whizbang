// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Generators.Tests;

/// <summary>
/// The real-shape options helper delivers what a build does, so a test built on it is not quietly feeding the
/// generators nothing: each declared property is present under the key a generator reads, empty unless the project
/// sets it.
/// </summary>
[Category("SourceGenerators")]
public class MSBuildGlobalOptionsTests {
  [Test]
  public async Task Declared_ListsEveryPropertyThePackagesMakeVisibleAsync() {
    await Assert.That(MSBuildGlobalOptions.Declared).IsEquivalentTo([
      "WhizbangCollectiveIndexWarning",
      "WhizbangMaxIdentifierLength",
      "WhizbangStripTableNameSuffixes",
      "WhizbangTableNameSuffixesToStrip",
      "WhizbangGuidInterceptionEnabled",
      "WhizbangQueryComposingAttributes",
      "WhizbangDocsPath",
      "ProjectDir",
    ]);
  }

  [Test]
  public async Task For_DeliversAnUnsetDeclaredPropertyAsAnEmptyValueAsync() {
    var options = MSBuildGlobalOptions.For();

    await Assert.That(options.TryGetValue("build_property.WhizbangStripTableNameSuffixes", out var value)).IsTrue()
      .Because("a declared property is written to the global config whether or not the project sets it");
    await Assert.That(value).IsEqualTo(string.Empty);
  }

  [Test]
  public async Task For_DeliversASetPropertyTrimmedAsync() {
    var options = MSBuildGlobalOptions.For(new Dictionary<string, string> { ["WhizbangMaxIdentifierLength"] = "  40  " });

    await Assert.That(options["build_property.WhizbangMaxIdentifierLength"]).IsEqualTo("40")
      .Because("the global config parser trims a value, so a generator never sees a whitespace-only one from a build");
  }
}
