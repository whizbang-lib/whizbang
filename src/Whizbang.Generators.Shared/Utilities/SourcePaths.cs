// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Shared.Utilities;

/// <summary>One prefix rewrite of the build's path map (<c>-pathmap:From=To</c>).</summary>
/// <param name="From">The path prefix as the build sees it, such as the repository root.</param>
/// <param name="To">What the compiler writes in its place, such as <c>/_/</c>.</param>
public sealed record PathMapEntry(string From, string To);

/// <summary>
/// Source paths as the compiler itself would write them into the build's output.
/// </summary>
/// <remarks>
/// A generator that writes a source file's path into generated code must map it the way the compiler maps the paths
/// it writes (PDB documents, <c>CallerFilePath</c>): through the build's path map, which ContinuousIntegrationBuild
/// sets so the repository root reads <c>/_/</c>. A path written unmapped is the checkout's directory, so two builds of
/// one commit from different directories produce different assemblies (#2484). A build with no path map, such as a
/// local one, keeps the full path, so the editor can still open the file.
/// </remarks>
/// <tests>tests/Whizbang.Generators.Tests/MessageRegistryGeneratorTests.cs:MessageRegistryGenerator_SameCommitInTwoDirectories_EmitsIdenticalRegistryUnderThePathMapAsync</tests>
public static class SourcePaths {
  /// <summary>
  /// The compilation's path map, in the order the compiler applies it; empty when the build sets none.
  /// </summary>
  /// <remarks>Equatable, so a generator can combine it into its pipeline without regenerating on every edit.</remarks>
  public static EquatableArray<PathMapEntry> PathMapOf(Compilation compilation) =>
    compilation.Options.SourceReferenceResolver is SourceFileResolver resolver
      ? [.. resolver.PathMap.Select(static p => new PathMapEntry(p.Key, p.Value))]
      : [];

  /// <summary>
  /// <paramref name="path"/> rewritten by the first entry whose prefix it starts with, with the separators of that
  /// entry's replacement (both kept when it holds both), exactly as the compiler rewrites a path; unchanged when no entry matches.
  /// </summary>
  public static string Map(string path, EquatableArray<PathMapEntry> pathMap) {
    foreach (var entry in pathMap) {
      if (!path.StartsWith(entry.From, StringComparison.Ordinal)) {
        continue;
      }
      // The compiler ends every replacement with a separator, so it holds a slash, a backslash or both.
      var mapped = entry.To + path.Substring(entry.From.Length);
      if (entry.To.IndexOf('\\') < 0) {
        return mapped.Replace('\\', '/');
      }
      return entry.To.IndexOf('/') < 0 ? mapped.Replace('/', '\\') : mapped;
    }
    return path;
  }
}
