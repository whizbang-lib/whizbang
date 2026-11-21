// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

extern alias shared;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using PathMapEntry = shared::Whizbang.Generators.Shared.Utilities.PathMapEntry;
using SourcePaths = shared::Whizbang.Generators.Shared.Utilities.SourcePaths;

namespace Whizbang.Generators.Tests;

/// <summary>
/// A source path a generator writes is mapped exactly as the compiler maps the paths it writes, so two builds of one
/// commit from different directories emit the same bytes (#2484), and a build with no path map keeps the full path.
/// </summary>
public class SourcePathsTests {
  [Test]
  public async Task ACompilationWithAPathMap_ReturnsItsEntriesInOrderAsync() {
    var pathMap = ImmutableArray.Create(
      new KeyValuePair<string, string>("/home/ci/repo/", "/_/"),
      new KeyValuePair<string, string>("/home/ci/other/", "/_1/"));
    var compilation = CSharpCompilation.Create(
      "PathMapped",
      options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        .WithSourceReferenceResolver(new SourceFileResolver([], null, pathMap)));

    var entries = SourcePaths.PathMapOf(compilation);

    await Assert.That(entries.ToArray()).IsEquivalentTo([
      new PathMapEntry("/home/ci/repo/", "/_/"),
      new PathMapEntry("/home/ci/other/", "/_1/"),
    ]);
  }

  [Test]
  public async Task ACompilationWithNoResolver_HasNoPathMapAsync() {
    var compilation = CSharpCompilation.Create(
      "Unmapped", options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    await Assert.That(SourcePaths.PathMapOf(compilation).ToArray()).IsEmpty();
  }

  [Test]
  public async Task APathNoEntryMatches_IsUnchangedAsync() {
    var map = _map(new PathMapEntry("/home/ci/repo/", "/_/"));

    await Assert.That(SourcePaths.Map("/Users/dev/repo/src/A.cs", map)).IsEqualTo("/Users/dev/repo/src/A.cs");
  }

  [Test]
  public async Task TheFirstMatchingEntry_RewritesThePrefixAsync() {
    var map = _map(new PathMapEntry("/other/", "/_9/"), new PathMapEntry("/home/ci/repo/", "/_/"),
      new PathMapEntry("/home/", "/_2/"));

    await Assert.That(SourcePaths.Map("/home/ci/repo/src/A.cs", map)).IsEqualTo("/_/src/A.cs");
  }

  [Test]
  public async Task AReplacementWithOnlySlashes_TurnsEveryBackslashIntoASlashAsync() {
    var map = _map(new PathMapEntry(@"C:\repo\", "/_/"));

    await Assert.That(SourcePaths.Map(@"C:\repo\src\Messages\A.cs", map)).IsEqualTo("/_/src/Messages/A.cs");
  }

  [Test]
  public async Task AReplacementWithOnlyBackslashes_TurnsEverySlashIntoABackslashAsync() {
    var map = _map(new PathMapEntry("/home/ci/repo/", @"X:\_\"));

    await Assert.That(SourcePaths.Map("/home/ci/repo/src/A.cs", map)).IsEqualTo(@"X:\_\src\A.cs");
  }

  [Test]
  public async Task AReplacementWithBothSeparators_KeepsThePathAsWrittenAsync() {
    var map = _map(new PathMapEntry("/home/ci/repo/", @"X:\_/"));

    await Assert.That(SourcePaths.Map(@"/home/ci/repo/src\A.cs", map)).IsEqualTo(@"X:\_/src\A.cs");
  }

  private static shared::Whizbang.Generators.Shared.Utilities.EquatableArray<PathMapEntry> _map(
      params PathMapEntry[] entries) => [.. entries];
}
