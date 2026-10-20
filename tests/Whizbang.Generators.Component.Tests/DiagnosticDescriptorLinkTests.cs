// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Every WHIZ diagnostic links to its own page and to a test that asserts it (#1430). The page is
/// <c>operations/diagnostics/whizNNN</c>, the one the docs site and the IDE's help link open; a descriptor that
/// linked a concept page instead, or nothing, sent a reader nowhere useful. Whether a <c>&lt;tests&gt;</c> link
/// resolves is the link guard's job (<c>.github/scripts/Test-TestsTagLink.ps1</c>); this checks that there is one.
/// </summary>
/// <docs>operations/diagnostics/error-reference</docs>
[Category("SourceGenerators")]
public partial class DiagnosticDescriptorLinkTests {
  private static readonly string _repoRoot = _findRepoRoot();

  /// <summary>Each descriptor carries the docs link to its own page and at least one tests link.</summary>
  [Test]
  public async Task EveryDescriptor_LinksItsPageAndATestAsync() {
    var descriptors = _descriptors();

    var problems = descriptors
      .Where(d => d.Docs.Count != 1 || d.Docs[0] != $"operations/diagnostics/{d.Id.ToLowerInvariant()}" || d.Tests == 0)
      .Select(d => $"{d.Id} ({d.Where}): docs [{string.Join(", ", d.Docs)}], {d.Tests} tests link(s)")
      .ToList();

    await Assert.That(problems).IsEmpty()
      .Because("Each WHIZ descriptor needs <docs>operations/diagnostics/whizNNN</docs> and a <tests> link to a test that asserts it");
  }

  /// <summary>The scan finds the descriptors, in every project that declares them, so an empty result above is not a blind scan.</summary>
  [Test]
  public async Task TheScan_FindsDescriptorsInEveryDeclaringProjectAsync() {
    var descriptors = _descriptors();

    await Assert.That(descriptors.Count).IsGreaterThan(100);
    await Assert.That(descriptors.Select(d => d.Id)).Contains("WHIZ001");
    await Assert.That(descriptors.Select(d => d.Id)).Contains("WHIZ703")
      .Because("The EF Core generator's notices are declared too");
    await Assert.That(descriptors.Select(d => d.Id)).Contains("WHIZ306")
      .Because("The shared generator library declares descriptors too");
  }

  private sealed record Declared(string Id, string Where, List<string> Docs, int Tests);

  // A descriptor is a `static readonly DiagnosticDescriptor X = new(` whose id follows within a few lines; its links are
  // the /// comment (and attribute) lines directly above it.
  private static List<Declared> _descriptors() {
    var found = new List<Declared>();
    foreach (var file in Directory.EnumerateFiles(Path.Combine(_repoRoot, "src"), "*.cs", SearchOption.AllDirectories)) {
      if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
          || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) {
        continue;
      }
      var lines = File.ReadAllLines(file);
      for (var i = 0; i < lines.Length; i++) {
        if (!_declaration().IsMatch(lines[i])) {
          continue;
        }
        var id = lines.Skip(i).Take(4).Select(l => _id().Match(l)).FirstOrDefault(m => m.Success)?.Groups["id"].Value;
        if (id is null) {
          continue;
        }
        var above = new List<string>();
        for (var k = i - 1; k >= 0 && (lines[k].TrimStart().StartsWith("///", StringComparison.Ordinal) || lines[k].TrimStart().StartsWith('[')); k--) {
          above.Add(lines[k]);
        }
        var docs = above.SelectMany(l => _docs().Matches(l).Select(m => m.Groups["path"].Value)).ToList();
        var tests = above.Count(l => l.Contains("<tests>", StringComparison.Ordinal));
        found.Add(new Declared(id, $"{Path.GetRelativePath(_repoRoot, file)}:{i + 1}", docs, tests));
      }
    }
    return found;
  }

  private static string _findRepoRoot() {
    var dir = AppContext.BaseDirectory;
    while (dir is not null) {
      if (Directory.Exists(Path.Combine(dir, "src", "Whizbang.Core"))) {
        return dir;
      }
      dir = Path.GetDirectoryName(dir);
    }
    throw new InvalidOperationException("Could not locate the repository root from AppContext.BaseDirectory");
  }

  [GeneratedRegex(@"static readonly DiagnosticDescriptor \w+ = new\(")]
  private static partial Regex _declaration();

  [GeneratedRegex("id: \"(?<id>WHIZ[0-9]+)\"")]
  private static partial Regex _id();

  [GeneratedRegex("<docs>(?<path>[^<]*)</docs>")]
  private static partial Regex _docs();
}
