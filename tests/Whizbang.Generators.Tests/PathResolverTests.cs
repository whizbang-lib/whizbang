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
      await Assert.That(result).IsEqualTo(tempPath);
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

  // TODO: Add tests for sibling directory discovery (requires temporary git repository setup)
  // TODO: Add tests for edge cases (no git root, no parent directory, etc.)
}
