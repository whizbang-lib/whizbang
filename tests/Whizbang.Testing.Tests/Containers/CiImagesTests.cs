// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Testing.Containers;

namespace Whizbang.Testing.Tests.Containers;

/// <summary>
/// Locks <see cref="CiImages"/>'s prefix rule to Testcontainers' own (DockerImage.ApplyHubImageNamePrefix),
/// so a fixture started with <c>docker run</c> pulls from the same place a Testcontainers fixture does:
/// the CI mirror when <c>TESTCONTAINERS_HUB_IMAGE_NAME_PREFIX</c> is set, Docker Hub otherwise.
/// </summary>
public class CiImagesTests {
  private const string MIRROR = "ghcr.io/whizbang-lib/ci-mirror/";

  [Test]
  [Arguments(null)]
  [Arguments("")]
  [Arguments("   ")]
  public async Task Resolve_WithoutPrefix_PullsFromDockerHubUnchangedAsync(string? prefix) {
    await Assert.That(CiImages.Resolve(CiImages.PGVECTOR, prefix)).IsEqualTo("pgvector/pgvector:pg17");
  }

  [Test]
  [Arguments("ghcr.io/whizbang-lib/ci-mirror/")]
  [Arguments("ghcr.io/whizbang-lib/ci-mirror")]
  [Arguments(" ghcr.io/whizbang-lib/ci-mirror/ ")]
  public async Task Resolve_WithPrefix_NamesTheMirrorCopyAsync(string prefix) {
    await Assert.That(CiImages.Resolve(CiImages.RABBITMQ, prefix))
      .IsEqualTo("ghcr.io/whizbang-lib/ci-mirror/rabbitmq:3.13-management-alpine");
    await Assert.That(CiImages.Resolve(CiImages.PGVECTOR, prefix))
      .IsEqualTo("ghcr.io/whizbang-lib/ci-mirror/pgvector/pgvector:pg17");
  }

  [Test]
  [Arguments("mcr.microsoft.com/azure-storage/azurite:latest")]
  [Arguments("localhost:5000/postgres:17")]
  [Arguments("localhost/postgres:17")]
  public async Task Resolve_ImageFromAnotherRegistry_IsNeverPrefixedAsync(string image) {
    await Assert.That(CiImages.Resolve(image, MIRROR)).IsEqualTo(image);
  }

  [Test]
  public async Task Resolve_WithoutExplicitPrefix_ReadsTheTestcontainersVariableAsync() {
    // Reads the variable rather than setting it: tests run in parallel, and the process environment is shared.
    var prefix = Environment.GetEnvironmentVariable(CiImages.PREFIX_VARIABLE);

    await Assert.That(CiImages.Resolve(CiImages.PGVECTOR)).IsEqualTo(CiImages.Resolve(CiImages.PGVECTOR, prefix));
  }

  [Test]
  [Arguments("")]
  [Arguments(" ")]
  public async Task Resolve_BlankImage_ThrowsAsync(string image) {
    await Assert.That(() => CiImages.Resolve(image, MIRROR)).Throws<ArgumentException>();
  }

  [Test]
  public async Task HasRegistry_NullImage_ThrowsAsync() {
    await Assert.That(() => CiImages.HasRegistry(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  [Arguments("rabbitmq:3", false)]
  [Arguments("pgvector/pgvector:pg17", false)]
  [Arguments("mcr.microsoft.com/mssql/server:2022-latest", true)]
  [Arguments("localhost:5000/x:1", true)]
  [Arguments("localhost/x:1", true)]
  public async Task HasRegistry_FollowsDockersRuleAsync(string image, bool expected) {
    await Assert.That(CiImages.HasRegistry(image)).IsEqualTo(expected);
  }
}
