// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// One <see cref="MetadataReference"/> per assembly file for the whole test run, shared by every compilation the tests
/// build (#2159).
/// </summary>
/// <remarks>
/// <para>
/// Roslyn decodes an assembly's metadata, and builds its symbols, once per reference instance and reuses them for
/// every compilation that holds the same instance. The helpers here used to call
/// <c>MetadataReference.CreateFromFile</c> for each
/// compilation, so each of the module's thousands of tests decoded the framework assemblies and Whizbang.Core again,
/// and every compilation in flight held its own copy. At TUnit's default parallelism the module's process tree grew
/// past 20 GB, and on a 16 GB GitHub-hosted runner the runner was shut down part-way through the module.
/// </para>
/// <para>
/// References are immutable, so sharing them changes nothing a test observes. A reference with aliases or other
/// properties is not shared: it is a different reference, and is still created where it is used.
/// </para>
/// <para>
/// <c>BannedSymbols.Tests.txt</c> bans <c>MetadataReference.CreateFromFile</c>
/// everywhere else in this project (RS0030, an error), so a new helper cannot quietly go back to a reference per test.
/// </para>
/// </remarks>
/// <tests>tests/Whizbang.Generators.Tests/GeneratorsTestParallelismTests.cs:FromFile_ReturnsOneReferencePerPath_ForTheWholeRunAsync</tests>
internal static class SharedMetadataReferences {
  private static readonly ConcurrentDictionary<string, PortableExecutableReference> _byPath =
    new(StringComparer.Ordinal);

  /// <summary>The shared reference to the assembly at <paramref name="path"/>.</summary>
  /// <param name="path">The assembly file.</param>
  /// <returns>The same instance for the same path, for the life of the test process.</returns>
  public static PortableExecutableReference FromFile(string path) =>
#pragma warning disable RS0030 // The one place a reference is created from a file: this cache.
    _byPath.GetOrAdd(path, static p => MetadataReference.CreateFromFile(p));
#pragma warning restore RS0030
}
