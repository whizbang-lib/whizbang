// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// <see cref="PathResolver"/> finds the documentation checkout from its parameters alone, the MSBuild inputs the
/// generator passes (#1180): a configured path, else a <c>whizbang-lib.github.io</c> sibling of the git root above the
/// project. Nothing here touches the process environment or current directory, so the tests need no serialization.
/// </summary>
/// <docs>extending/source-generators/message-registry#documentation-and-test-maps</docs>
public class PathResolverTests {
  /// <summary>A configured path that exists is the answer, whatever the project directory holds.</summary>
  [Test]
  public async Task FindDocsRepositoryPath_WithAnExistingConfiguredPath_UsesItAsync() {
    var configured = Path.Combine(Path.GetTempPath(), $"whizbang-docs-{Guid.NewGuid():N}");
    Directory.CreateDirectory(configured);

    try {
      var result = PathResolver.FindDocsRepositoryPath(configured, projectDirectory: null);

      await Assert.That(result).IsEqualTo(configured);
    } finally {
      Directory.Delete(configured);
    }
  }

  /// <summary>A configured path that does not exist falls back to sibling discovery from the project directory.</summary>
  [Test]
  public async Task FindDocsRepositoryPath_WithAMissingConfiguredPath_FallsBackToSiblingDiscoveryAsync() {
    var (parent, startDirectory, docsSibling) = _workingTreeWithDocsSibling();

    try {
      var result = PathResolver.FindDocsRepositoryPath(
        Path.Combine(Path.GetTempPath(), $"whizbang-missing-{Guid.NewGuid():N}"), startDirectory);

      await Assert.That(Path.GetFullPath(result!)).IsEqualTo(Path.GetFullPath(docsSibling));
    } finally {
      Directory.Delete(parent, recursive: true);
    }
  }

  /// <summary>Without a configured path or a project directory there is nothing to look from, so there is no answer.</summary>
  [Test]
  public async Task FindDocsRepositoryPath_WithNeitherInput_ReturnsNullAsync() =>
    await Assert.That(PathResolver.FindDocsRepositoryPath(configuredPath: null, projectDirectory: null)).IsNull();

  /// <summary>
  /// Sibling discovery starts by walking up for a <c>.git</c> directory. When the walk reaches the filesystem root
  /// without finding one, the shape a generator sees when it runs from a NuGet package inside a build directory that is
  /// not in a working tree, there is no library root to hang a sibling off, and the answer is "no docs repository".
  /// </summary>
  [Test]
  public async Task FindDocsRepositoryPath_StartedOutsideAnyGitWorkingTree_ReturnsNullAsync() {
    var outsideAnyRepository = Path.Combine(Path.GetTempPath(), $"whizbang-no-git-{Guid.NewGuid():N}", "nested");
    Directory.CreateDirectory(outsideAnyRepository);

    try {
      var result = PathResolver.FindDocsRepositoryPath(configuredPath: null, outsideAnyRepository);

      await Assert.That(result).IsNull()
        .Because("with no .git above the project there is no library root, so there is no sibling to resolve");
    } finally {
      Directory.Delete(Path.GetDirectoryName(outsideAnyRepository)!, recursive: true);
    }
  }

  /// <summary>
  /// The mirror of the test above: started inside a working tree whose parent holds a <c>whizbang-lib.github.io</c>
  /// sibling, the resolver finds that sibling, beside the library root rather than beside the start directory.
  /// </summary>
  [Test]
  public async Task FindDocsRepositoryPath_StartedInsideAWorkingTreeWithADocsSibling_ReturnsTheSiblingAsync() {
    var (parent, startDirectory, docsSibling) = _workingTreeWithDocsSibling();

    try {
      var result = PathResolver.FindDocsRepositoryPath(configuredPath: null, startDirectory);

      await Assert.That(result).IsNotNull();
      await Assert.That(Path.GetFullPath(result!)).IsEqualTo(Path.GetFullPath(docsSibling))
        .Because("the documentation repository is the whizbang-lib.github.io directory beside the library root");
    } finally {
      Directory.Delete(parent, recursive: true);
    }
  }

  /// <summary>
  /// Inside a working tree whose parent holds no <c>whizbang-lib.github.io</c> directory there is no documentation
  /// repository, and the resolver says so rather than inventing a path.
  /// </summary>
  [Test]
  public async Task FindDocsRepositoryPath_StartedInsideAWorkingTreeWithoutADocsSibling_ReturnsNullAsync() {
    var parent = Path.Combine(Path.GetTempPath(), $"whizbang-git-{Guid.NewGuid():N}");
    var repository = Path.Combine(parent, "whizbang");
    Directory.CreateDirectory(Path.Combine(repository, ".git"));

    try {
      await Assert.That(PathResolver.FindDocsRepositoryPath(configuredPath: null, repository)).IsNull();
    } finally {
      Directory.Delete(parent, recursive: true);
    }
  }

  // parent/{whizbang/.git/, whizbang-lib.github.io/}, with the start directory deep inside the repository.
  private static (string Parent, string StartDirectory, string DocsSibling) _workingTreeWithDocsSibling() {
    var parent = Path.Combine(Path.GetTempPath(), $"whizbang-git-{Guid.NewGuid():N}");
    var repository = Path.Combine(parent, "whizbang");
    var startDirectory = Path.Combine(repository, "src", "Whizbang.Generators");
    var docsSibling = Path.Combine(parent, "whizbang-lib.github.io");
    Directory.CreateDirectory(Path.Combine(repository, ".git"));
    Directory.CreateDirectory(startDirectory);
    Directory.CreateDirectory(docsSibling);
    return (parent, startDirectory, docsSibling);
  }
}
