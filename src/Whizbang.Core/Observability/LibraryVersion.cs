using System;

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
