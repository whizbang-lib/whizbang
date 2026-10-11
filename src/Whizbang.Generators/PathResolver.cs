// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.IO;

namespace Whizbang.Generators;

/// <summary>
/// Resolves the documentation repository a generator reads its docs and tests maps from, from the generator's own
/// inputs only (#1180): the <c>WhizbangDocsPath</c> MSBuild property, else a sibling of the git root above the project.
/// </summary>
/// <remarks>
/// It used to read the <c>WHIZBANG_DOCS_PATH</c> environment variable and walk up from the process's current directory
/// while generating, so its output varied by machine and a test that set the variable changed what generator tests
/// running beside it produced. The package's build props still default <c>WhizbangDocsPath</c> from that variable, so
/// setting it keeps working, but as an MSBuild input rather than process state.
/// </remarks>
/// <tests>tests/Whizbang.Generators.Component.Tests/PathResolverTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Component.Tests/MessageRegistryDocsPathTests.cs</tests>
public static class PathResolver {
  /// <summary>The MSBuild property naming the documentation checkout, as the generator receives it.</summary>
  public const string DOCS_PATH_PROPERTY = "build_property.WhizbangDocsPath";

  /// <summary>The MSBuild property naming the project's directory, where sibling discovery starts.</summary>
  public const string PROJECT_DIRECTORY_PROPERTY = "build_property.ProjectDir";

  /// <summary>
  /// Finds the documentation repository path: <paramref name="configuredPath"/> when it exists, else a
  /// <c>whizbang-lib.github.io</c> sibling of the git root above <paramref name="projectDirectory"/>.
  /// </summary>
  /// <param name="configuredPath">The <c>WhizbangDocsPath</c> property, if set.</param>
  /// <param name="projectDirectory">The project's directory, where sibling discovery starts; none means no discovery.</param>
  /// <returns>Path to documentation repository, or null if not found</returns>
  public static string? FindDocsRepositoryPath(string? configuredPath, string? projectDirectory) {
    if (!string.IsNullOrEmpty(configuredPath) && Directory.Exists(configuredPath)) {
      return configuredPath;
    }

    var libraryRoot = string.IsNullOrEmpty(projectDirectory) ? null : _findGitRoot(projectDirectory!);
    if (libraryRoot == null) {
      return null;
    }

    // The sibling is resolved through "..", which the filesystem answers even at its root (the root is
    // its own parent), so a library root of "/" looks for "/whizbang-lib.github.io" rather than needing a
    // case of its own; the existence test below decides either way.
    var docsPath = Path.GetFullPath(Path.Combine(libraryRoot, "..", "whizbang-lib.github.io"));
    return Directory.Exists(docsPath) ? docsPath : null;
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
