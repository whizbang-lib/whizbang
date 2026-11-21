// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Archival;

/// <summary>
/// Where an archive store keeps objects outside the database, as logical information only (#2137): the store, the
/// storage endpoint, the logical connection name the service is configured with, and the container. Never a key, a
/// signature or a connection string.
/// </summary>
/// <remarks>
/// A store outside the database records one location per row of <c>wh_archive_locations</c>, in normalized form
/// (<see cref="Normalize"/>, #2141), and every archive record names its location by id plus a path that starts at its
/// stream. The database keeps that pin; infrastructure decides where the objects actually are, and configuration points
/// the store there. Before it reads, restores, deletes or writes, the store compares the record's location with the one
/// its configuration names, and a difference is handled as the store's <see cref="ArchiveLocationMismatchHandling"/>
/// says: by default it throws <see cref="ArchiveLocationMismatchException"/>. A record never redirects an operation.
/// </remarks>
/// <param name="Provider">The store that writes the location (for example <c>azure-blob</c>).</param>
/// <param name="Endpoint">The storage account's endpoint (for example <c>https://account.blob.core.windows.net</c>), or
/// <see langword="null"/> for a location recorded before endpoints were.</param>
/// <param name="ConnectionName">The logical connection name, or <see langword="null"/> for a location recorded before
/// names were.</param>
/// <param name="Container">The container (or prefix) the objects live in.</param>
/// <docs>fundamentals/events/archive-store#archive-locations</docs>
/// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Archival/ArchiveLocationMismatchExceptionTests.cs:ALocationRecordedBeforeEndpointsWere_ReadsAsNotRecordedAsync</tests>
public sealed record ArchiveLocation(string Provider, string? Endpoint, string? ConnectionName, string Container) {
  private const string NOT_RECORDED = "(not recorded)";

  // What surrounds a value without being part of it; the same set the SQL helpers of migration 553 trim.
  private static readonly char[] _whitespace = [' ', '\t', '\r', '\n'];

  /// <summary>
  /// This location in normalized form (#2141), the form the database stores and compares: two locations are the same
  /// place exactly when their normalized forms are equal.
  /// </summary>
  /// <remarks>
  /// Every part loses surrounding whitespace and is compared in lower case (host names, storage account names and
  /// container names are case-insensitive or lower case only, and a logical connection name is compared without case).
  /// The endpoint also loses trailing slashes and the scheme's default port when it is written out (<c>:443</c> for
  /// https, <c>:80</c> for http). Every other difference is another location: the scheme, the host (another account, the
  /// account's secondary endpoint, a custom domain), any other port, the path, the connection name, the container. The
  /// SQL helpers of migration 553 apply the same rules to what the database stores.
  /// </remarks>
  /// <returns>The normalized location; a location already normalized is returned equal to itself.</returns>
  /// <docs>fundamentals/events/archive-store#archive-locations</docs>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Archival/ArchiveLocationNormalizationTests.cs</tests>
  public ArchiveLocation Normalize() =>
    new(NormalizeName(Provider)!, NormalizeEndpoint(Endpoint), NormalizeName(ConnectionName), NormalizeName(Container)!);

  /// <summary>
  /// A storage endpoint in normalized form: surrounding whitespace removed, lower case, no trailing slash, and no
  /// <c>:443</c> (https) or <c>:80</c> (http). <see langword="null"/> stays <see langword="null"/>.
  /// </summary>
  /// <param name="endpoint">The endpoint as written.</param>
  /// <returns>The normalized endpoint.</returns>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Archival/ArchiveLocationNormalizationTests.cs:NormalizeEndpoint_DropsWhatOnlyChangesHowTheEndpointIsWrittenAsync</tests>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Archival/ArchiveLocationNormalizationTests.cs:NormalizeEndpoint_KeepsEveryDifferenceThatIsAnotherPlaceAsync</tests>
  public static string? NormalizeEndpoint(string? endpoint) {
    if (endpoint is null) {
      return null;
    }
    var value = endpoint.Trim(_whitespace).ToLowerInvariant().TrimEnd('/');
    if (value.StartsWith(_https, StringComparison.Ordinal)) {
      return _withoutPort(value, _https.Length, ":443");
    }
    return value.StartsWith(_http, StringComparison.Ordinal) ? _withoutPort(value, _http.Length, ":80") : value;
  }

  /// <summary>
  /// A provider, logical connection name or container in normalized form: surrounding whitespace removed, lower case.
  /// <see langword="null"/> stays <see langword="null"/>.
  /// </summary>
  /// <param name="name">The name as written.</param>
  /// <returns>The normalized name.</returns>
  /// <tests>tests/Whizbang.Core.Unit.Perspectives.Tests/Archival/ArchiveLocationNormalizationTests.cs:NormalizeName_IgnoresSurroundingWhitespaceAndCaseAsync</tests>
  public static string? NormalizeName(string? name) => name?.Trim(_whitespace).ToLowerInvariant();

  private static readonly string _https = Uri.UriSchemeHttps + Uri.SchemeDelimiter;
  private static readonly string _http = Uri.UriSchemeHttp + Uri.SchemeDelimiter;

  // The endpoint without the scheme's default port, when the authority (scheme to the first slash) ends with it.
  private static string _withoutPort(string endpoint, int authorityStart, string defaultPort) {
    var slash = endpoint.IndexOf('/', authorityStart);
    var authorityEnd = slash < 0 ? endpoint.Length : slash;
    return endpoint.AsSpan(authorityStart, authorityEnd - authorityStart).EndsWith(defaultPort, StringComparison.Ordinal)
      ? endpoint.Remove(authorityEnd - defaultPort.Length, defaultPort.Length)
      : endpoint;
  }

  /// <summary>The location as an operator reads it.</summary>
  /// <returns><c>{provider}, endpoint {endpoint}, connection '{name}', container '{container}'</c>.</returns>
  public override string ToString() =>
    $"{Provider}, endpoint {Endpoint ?? NOT_RECORDED}, connection {(ConnectionName is null ? NOT_RECORDED : $"'{ConnectionName}'")}, container '{Container}'";
}
