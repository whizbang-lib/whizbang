// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Archival;

/// <summary>
/// Where an archive store keeps objects outside the database, as logical information only (#2137): the store, the
/// storage endpoint, the logical connection name the service is configured with, and the container. Never a key, a
/// signature or a connection string.
/// </summary>
/// <remarks>
/// A store outside the database records one location per row of <c>wh_archive_locations</c>, and every archive record
/// names its location by id plus a path that starts at its stream. Before it reads, restores or deletes, the store
/// compares the record's location with the one its configuration names and throws
/// <see cref="ArchiveLocationMismatchException"/> when they differ, so a record never redirects an operation and nothing
/// is ever deleted outside the configured location.
/// </remarks>
/// <param name="Provider">The store that writes the location (for example <c>azure-blob</c>).</param>
/// <param name="Endpoint">The storage account's endpoint (for example <c>https://account.blob.core.windows.net</c>), or
/// <see langword="null"/> for a location recorded before endpoints were.</param>
/// <param name="ConnectionName">The logical connection name, or <see langword="null"/> for a location recorded before
/// names were.</param>
/// <param name="Container">The container (or prefix) the objects live in.</param>
/// <docs>fundamentals/events/archive-store#archive-locations</docs>
/// <tests>tests/Whizbang.Core.Tests/Archival/ArchiveLocationMismatchExceptionTests.cs:ALocationRecordedBeforeEndpointsWere_ReadsAsNotRecordedAsync</tests>
public sealed record ArchiveLocation(string Provider, string? Endpoint, string? ConnectionName, string Container) {
  private const string NOT_RECORDED = "(not recorded)";

  /// <summary>The location as an operator reads it.</summary>
  /// <returns><c>{provider}, endpoint {endpoint}, connection '{name}', container '{container}'</c>.</returns>
  public override string ToString() =>
    $"{Provider}, endpoint {Endpoint ?? NOT_RECORDED}, connection {(ConnectionName is null ? NOT_RECORDED : $"'{ConnectionName}'")}, container '{Container}'";
}
