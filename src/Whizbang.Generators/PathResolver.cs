using System;
using System.IO;

namespace Whizbang.Generators;

/// <summary>
/// Resolves paths to documentation repository with sibling discovery and environment variable overrides.
/// </summary>
/// <tests>tests/Whizbang.Generators.Tests/PathResolverTests.cs</tests>
public static class PathResolver {
  /// <summary>
  /// Finds the documentation repository path.
  /// Priority: 1) WHIZBANG_DOCS_PATH env var, 2) Sibling directory discovery
  /// </summary>
  /// <param name="searchStartDirectory">
  /// Where sibling discovery starts walking up from. Null (the default, and what every production
  /// call site passes) means the process's current directory.
  /// </param>
  /// <returns>Path to documentation repository, or null if not found</returns>
  /// <remarks>
  /// The start directory is a parameter rather than always the process's current directory so the
  /// "not inside a git working tree" outcome — a generator running from a NuGet package in a build
  /// directory with no repository above it — can be asserted without mutating process-wide state
  /// that every other test in the run shares.
  /// </remarks>
  public static string? FindDocsRepositoryPath(string? searchStartDirectory = null) {
    // Priority 1: Environment variable override
    var envPath = Environment.GetEnvironmentVariable("WHIZBANG_DOCS_PATH");
    if (!string.IsNullOrEmpty(envPath) && Directory.Exists(envPath)) {
      return envPath;
    }

    // Priority 2: Sibling directory discovery
    var libraryRoot = _findGitRoot(searchStartDirectory ?? Directory.GetCurrentDirectory());
    if (libraryRoot == null) {
      return null;
    }

    // Path.GetDirectoryName is null only when the library root IS the filesystem root, which would
    // take a .git directory at "/". The guard stays, folded into the existence test so it is
    // evaluated on every call: no parent means no sibling path, which means no documentation repo.
    var parentDir = Path.GetDirectoryName(libraryRoot);
    var docsPath = parentDir == null ? null : Path.Combine(parentDir, "whizbang-lib.github.io");
    return docsPath != null && Directory.Exists(docsPath) ? docsPath : null;
  }

  /// <summary>
  /// Finds the git root directory by walking up from startPath.
  /// </summary>
  /// <param name="startPath">Starting directory path</param>
  /// <returns>Git root directory path, or null if not found</returns>
  private static string? _findGitRoot(string startPath) {
    var current = new DirectoryInfo(startPath);
    while (current != null) {
      if (Directory.Exists(Path.Combine(current.FullName, ".git"))) {
        return current.FullName;
      }
      current = current.Parent;
    }
    return null;
  }
}
