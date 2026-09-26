using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for <see cref="PathResolver"/>.
/// Validates documentation repository path resolution via environment variables and sibling discovery.
/// </summary>
/// <remarks>
/// NOTE: These tests currently have compilation issues due to ModuleInitializerAttribute conflict
/// between PolySharp and System.Runtime. This is a known issue that needs resolution.
/// Tests are stubs pending fix.
/// </remarks>
public class PathResolverTests {

  [Test]
  public async Task FindDocsRepositoryPath_ReturnsPathOrNullAsync() {
    // Arrange & Act
    var result = PathResolver.FindDocsRepositoryPath();

    // Assert
    // Result can be null (no sibling repo) or a valid path (sibling repo exists)
    if (result is not null) {
      await Assert.That(Directory.Exists(result)).IsTrue();
    } else {
      // Null is acceptable if no documentation repository is found
      await Assert.That(result).IsNull();
    }
  }

  [Test]
  public async Task FindDocsRepositoryPath_WithEnvironmentVariable_UsesEnvironmentPathAsync() {
    // Arrange
    var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    Directory.CreateDirectory(tempPath);

    try {
      Environment.SetEnvironmentVariable("WHIZBANG_DOCS_PATH", tempPath);

      // Act
      var result = PathResolver.FindDocsRepositoryPath();

      // Assert
      // Normalize paths to handle macOS symlink resolution (/var/folders vs /private/var/folders)
      await Assert.That(Path.GetFullPath(result!)).IsEqualTo(Path.GetFullPath(tempPath));
    } finally {
      // Cleanup
      Environment.SetEnvironmentVariable("WHIZBANG_DOCS_PATH", null);
      Directory.Delete(tempPath);
    }
  }

  [Test]
  public async Task FindDocsRepositoryPath_WithInvalidEnvironmentVariable_FallsBackToSiblingDiscoveryAsync() {
    // Arrange
    var invalidPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    // Don't create directory - it should not exist

    try {
      Environment.SetEnvironmentVariable("WHIZBANG_DOCS_PATH", invalidPath);

      // Act
      var result = PathResolver.FindDocsRepositoryPath();

      // Assert
      // Should fall back to sibling discovery since env var path doesn't exist
      await Assert.That(result).IsNotEqualTo(invalidPath);
    } finally {
      // Cleanup
      Environment.SetEnvironmentVariable("WHIZBANG_DOCS_PATH", null);
    }
  }

  /// <summary>
  /// Sibling discovery starts by walking up for a <c>.git</c> directory. When the walk reaches the
  /// filesystem root without finding one — the shape a generator sees when it runs from a NuGet
  /// package inside a build directory that is not in a working tree — there is no library root to
  /// hang a sibling off, and the resolver must answer "no docs repository" rather than guessing a
  /// path or throwing.
  /// </summary>
  /// <remarks>
  /// Asserting on a temp directory rather than on the process's current directory: the current
  /// directory is shared by every test in the run, and a test that reassigns it can only be correct
  /// while nothing else is executing.
  /// </remarks>
  [Test]
  public async Task FindDocsRepositoryPath_StartedOutsideAnyGitWorkingTree_ReturnsNullAsync() {
    // Arrange - a directory with no .git anywhere above it (the temp root is not a working tree)
    var outsideAnyRepository = Path.Combine(Path.GetTempPath(), $"whizbang-no-git-{Guid.NewGuid():N}", "nested");
    Directory.CreateDirectory(outsideAnyRepository);
    var previousEnvironmentOverride = Environment.GetEnvironmentVariable("WHIZBANG_DOCS_PATH");
    Environment.SetEnvironmentVariable("WHIZBANG_DOCS_PATH", null);

    try {
      // Act
      var result = PathResolver.FindDocsRepositoryPath(outsideAnyRepository);

      // Assert
      await Assert.That(result).IsNull()
        .Because("with no .git above the start directory there is no library root, so there is no sibling documentation repository to resolve and the resolver must say so instead of returning a path that does not exist");
    } finally {
      Environment.SetEnvironmentVariable("WHIZBANG_DOCS_PATH", previousEnvironmentOverride);
      Directory.Delete(Path.GetDirectoryName(outsideAnyRepository)!, recursive: true);
    }
  }

  /// <summary>
  /// The mirror of the test above, so the null there is the "no working tree" answer and not simply
  /// "this resolver always returns null": started inside a real working tree whose parent holds a
  /// <c>whizbang-lib.github.io</c> sibling, the resolver must find that sibling.
  /// </summary>
  [Test]
  public async Task FindDocsRepositoryPath_StartedInsideAWorkingTreeWithADocsSibling_ReturnsTheSiblingAsync() {
    // Arrange - parent/{repo/.git/, whizbang-lib.github.io/}, starting deep inside repo
    var parent = Path.Combine(Path.GetTempPath(), $"whizbang-git-{Guid.NewGuid():N}");
    var repository = Path.Combine(parent, "whizbang");
    var startDirectory = Path.Combine(repository, "src", "Whizbang.Generators");
    var docsSibling = Path.Combine(parent, "whizbang-lib.github.io");
    Directory.CreateDirectory(Path.Combine(repository, ".git"));
    Directory.CreateDirectory(startDirectory);
    Directory.CreateDirectory(docsSibling);
    var previousEnvironmentOverride = Environment.GetEnvironmentVariable("WHIZBANG_DOCS_PATH");
    Environment.SetEnvironmentVariable("WHIZBANG_DOCS_PATH", null);

    try {
      // Act
      var result = PathResolver.FindDocsRepositoryPath(startDirectory);

      // Assert
      await Assert.That(result).IsNotNull()
        .Because("the walk up from src/Whizbang.Generators reaches the .git directory, making the repository the library root");
      await Assert.That(Path.GetFullPath(result!)).IsEqualTo(Path.GetFullPath(docsSibling))
        .Because("the documentation repository is the whizbang-lib.github.io directory beside the library root, not beside the start directory");
    } finally {
      Environment.SetEnvironmentVariable("WHIZBANG_DOCS_PATH", previousEnvironmentOverride);
      Directory.Delete(parent, recursive: true);
    }
  }

  // FUTURE: Add tests for edge cases (no parent directory above the git root)
}
