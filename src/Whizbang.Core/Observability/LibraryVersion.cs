// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Reflection;

namespace Whizbang.Core.Observability;

/// <summary>
/// The Whizbang library version this binary runs, as a value rather than a reflection lookup.
/// The storage driver's source generator registers it from the version constant it already embeds
/// — zero reflection, AOT-safe, and identical to the version the migration ledger records.
/// </summary>
/// <docs>operations/startup/capabilities-and-duties</docs>
public interface ILibraryVersionProvider {
  /// <summary>The library version (SemVer text, build metadata stripped).</summary>
  string LibraryVersion { get; }
}

/// <summary>Default <see cref="ILibraryVersionProvider"/> over a fixed value.</summary>
/// <docs>operations/startup/capabilities-and-duties</docs>
public sealed class LibraryVersionProvider : ILibraryVersionProvider {
  /// <summary>Creates the provider over <paramref name="libraryVersion"/>.</summary>
  public LibraryVersionProvider(string libraryVersion) {
    ArgumentException.ThrowIfNullOrEmpty(libraryVersion);
    LibraryVersion = libraryVersion;
  }

  /// <inheritdoc />
  public string LibraryVersion { get; }
}

/// <summary>
/// The version text an assembly declares: its informational version, else its assembly version,
/// else <c>unknown</c>.
/// </summary>
/// <remarks>
/// The informational version carries the full SemVer a package was built with (prerelease label
/// included); the assembly version is all an unstamped build has; and a version has to be some text
/// wherever it is reported, so the last resort is a word rather than an empty string.
/// </remarks>
/// <tests>tests/Whizbang.Core.Tests/Observability/AssemblyVersionTextTests.cs</tests>
internal static class AssemblyVersionText {
  /// <summary>The version text <paramref name="assembly"/> declares.</summary>
  /// <param name="assembly">The assembly to read.</param>
  /// <returns>Its informational version, else its assembly version, else <c>unknown</c>.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Observability/AssemblyVersionTextTests.cs:Informational_WinsAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Observability/AssemblyVersionTextTests.cs:NoInformational_AssemblyVersionAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Observability/AssemblyVersionTextTests.cs:NoVersion_UnknownAsync</tests>
  internal static string Of(Assembly assembly) =>
    assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? assembly.GetName().Version?.ToString()
    ?? "unknown";
}
