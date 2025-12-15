using System;
using System.IO;

namespace Whizbang.Generators;

/// <summary>
/// Resolves paths to documentation repository with sibling discovery and environment variable overrides.
/// </summary>
/// <tests>tests/Whizbang.Generators.Tests/PathResolverTests.cs</tests>
internal static class PathResolver {
  /// <summary>
  /// Finds the documentation repository path.
  /// Priority: 1) WHIZBANG_DOCS_PATH env var, 2) Sibling directory discovery
  /// </summary>
  /// <returns>Path to documentation repository, or null if not found</returns>
  public static string? FindDocsRepositoryPath() {
    // Priority 1: Environment variable override
    var envPath = Environment.GetEnvironmentVariable("WHIZBANG_DOCS_PATH");
    if (!string.IsNullOrEmpty(envPath) && Directory.Exists(envPath)) {
      return envPath;
    }

    // Priority 2: Sibling directory discovery
    var libraryRoot = FindGitRoot(Directory.GetCurrentDirectory());
    if (libraryRoot == null) {
      return null;
    }

    var parentDir = Path.GetDirectoryName(libraryRoot);
    if (parentDir == null) {
      return null;
    }

    var docsPath = Path.Combine(parentDir, "whizbang-lib.github.io");
    return Directory.Exists(docsPath) ? docsPath : null;
  }

  /// <summary>
  /// Finds the git root directory by walking up from startPath.
  /// </summary>
  /// <param name="startPath">Starting directory path</param>
  /// <returns>Git root directory path, or null if not found</returns>
  private static string? FindGitRoot(string startPath) {
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
