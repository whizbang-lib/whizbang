// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Testing.Containers;

/// <summary>
/// The Docker Hub images the test fixtures start, and the one rule that decides where they are pulled
/// from: Docker Hub locally, the GHCR mirror in CI.
/// </summary>
/// <remarks>
/// <para>
/// CI sets <c>TESTCONTAINERS_HUB_IMAGE_NAME_PREFIX</c> to <c>ghcr.io/whizbang-lib/ci-mirror/</c> once the
/// mirror holds every image in <c>.github/ci-images.txt</c>. Testcontainers reads that variable itself
/// and prefixes every Docker Hub image it pulls, its reaper included. A fixture that starts a container
/// with a plain <c>docker run</c> bypasses Testcontainers, so it names its image through
/// <see cref="Resolve(string)"/>, which applies the same rule from the same variable. Unset (local runs),
/// every image is pulled from Docker Hub unchanged.
/// </para>
/// <para>
/// Every image here must also be listed in <c>.github/ci-images.txt</c>: the CI pull check fails a job
/// that pulls an unlisted Docker Hub image. See <c>ai-docs/ci-image-mirror.md</c>.
/// </para>
/// </remarks>
public static class CiImages {
#pragma warning disable CA1707 // project convention: public const strings use UPPER_CASE with underscores
  /// <summary>The environment variable holding the mirror prefix, as Testcontainers names it.</summary>
  public const string PREFIX_VARIABLE = "TESTCONTAINERS_HUB_IMAGE_NAME_PREFIX";
#pragma warning restore CA1707

  /// <summary>PostgreSQL 17 with pgvector: the shared PostgreSQL fixture and the restart chaos tests.</summary>
  public const string PGVECTOR = "pgvector/pgvector:pg17";

  /// <summary>RabbitMQ 3.13 with the management plugin: the shared RabbitMQ fixture.</summary>
  public const string RABBITMQ = "rabbitmq:3.13-management-alpine";

  /// <summary>
  /// The name to pull <paramref name="image"/> by: its mirror copy when
  /// <see cref="PREFIX_VARIABLE"/> is set, the image itself otherwise.
  /// </summary>
  /// <param name="image">The image as Docker Hub names it, e.g. <see cref="PGVECTOR"/>.</param>
  /// <returns>The image name to pass to <c>docker run</c>.</returns>
  public static string Resolve(string image) =>
    Resolve(image, Environment.GetEnvironmentVariable(PREFIX_VARIABLE));

  /// <summary>
  /// Applies <paramref name="prefix"/> to a Docker Hub image the way Testcontainers does: an image whose
  /// first path segment is a registry host (contains a dot or a colon, or is <c>localhost</c>) is left
  /// alone; any other image becomes <c>&lt;prefix&gt;/&lt;image&gt;</c>.
  /// </summary>
  /// <param name="image">The image as Docker Hub names it.</param>
  /// <param name="prefix">The mirror prefix; null or blank means Docker Hub.</param>
  /// <returns>The image name to pull.</returns>
  public static string Resolve(string image, string? prefix) {
    ArgumentException.ThrowIfNullOrWhiteSpace(image);
    if (string.IsNullOrWhiteSpace(prefix) || HasRegistry(image)) {
      return image;
    }
    return $"{prefix.Trim().Trim('/')}/{image}";
  }

  /// <summary>Whether <paramref name="image"/> starts with a registry host, by Docker's rule.</summary>
  /// <param name="image">An image name.</param>
  /// <returns>True when the first path segment is a registry host.</returns>
  public static bool HasRegistry(string image) {
    ArgumentNullException.ThrowIfNull(image);
    var slash = image.IndexOf('/', StringComparison.Ordinal);
    if (slash < 0) {
      return false;
    }
    var first = image[..slash];
    return first.Contains('.', StringComparison.Ordinal) || first.Contains(':', StringComparison.Ordinal) || first == "localhost";
  }
}
