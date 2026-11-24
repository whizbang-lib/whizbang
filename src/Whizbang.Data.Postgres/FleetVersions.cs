// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Which other releases are alive in the fleet, as the instance registry reports them.
/// </summary>
/// <remarks>
/// <para>
/// A rewrite that changes a stored unit is not safe under a mixed fleet: an older instance keeps
/// writing the old unit into a table the ledger already says is converted, and nothing can tell
/// those rows apart afterward. The migrator cannot refuse to run, because under a rolling update
/// the older instances stay until the newer ones are ready and the newer ones are not ready until
/// the migration finishes. What it can do is say so, loudly, naming the releases it saw; this is
/// the query behind that warning.
/// </para>
/// <para>
/// A live instance is one the database's one liveness rule (#2027, <c>wh_live_instance_ids</c>) calls live, and its
/// release is what its heartbeat's metadata says. The rule is Preflight's, so it is there before this runs. This instance's own row is ignored, and so is any instance on the
/// same release. A registry that does not exist yet answers nothing rather than failing the
/// migrator over a warning.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/FleetVersionsTests.cs</tests>
public static class FleetVersions {
  /// <summary>The value reported for an instance whose heartbeat carries no release.</summary>
  public const string UNKNOWN = "unknown";

  /// <summary>
  /// The releases of the other live instances that differ from this one's, each once, in order.
  /// </summary>
  /// <param name="connection">An open connection.</param>
  /// <param name="schema">The schema the registry lives in.</param>
  /// <param name="self">This instance, which is not "other".</param>
  /// <param name="ownVersion">This instance's release, which is not "other" either.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The distinct other releases, empty when the fleet is uniform or the registry is absent.</returns>
  public static async Task<IReadOnlyList<string>> OtherLiveVersionsAsync(
      NpgsqlConnection connection,
      string schema,
      Guid self,
      string ownVersion,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentNullException.ThrowIfNull(ownVersion);

    var target = string.IsNullOrEmpty(schema) ? "public" : schema.Replace("\"", string.Empty);
    var quoted = "\"" + target.Replace("\"", "\"\"") + "\"";

    await using (var exists = new NpgsqlCommand("SELECT to_regclass($1) IS NOT NULL", connection)) {
      exists.Parameters.AddWithValue($"{quoted}.wh_service_instances");
      if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true) {
        return [];
      }
    }

    await using var command = new NpgsqlCommand(
      "SELECT DISTINCT coalesce(metadata ->> 'Version', $2) AS release "
      + $"FROM {quoted}.wh_service_instances "
      + $"WHERE instance_id <> $1 AND instance_id IN (SELECT l.instance_id FROM {quoted}.wh_live_instance_ids() l) "
      + "ORDER BY release", connection);
    command.Parameters.AddWithValue(self);
    command.Parameters.AddWithValue(UNKNOWN);

    var others = new List<string>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      var release = reader.GetString(0);
      if (!string.Equals(release, ownVersion, StringComparison.Ordinal)) {
        others.Add(release);
      }
    }
    return others;
  }

  /// <summary>
  /// The library versions of live instances older than <paramref name="ownLibraryVersion"/>, each once, in order: an
  /// instance that recorded none, or one whose version cannot be read, counts as older (#2518).
  /// </summary>
  /// <param name="connection">An open connection.</param>
  /// <param name="schema">The schema the registry lives in.</param>
  /// <param name="ownLibraryVersion">This instance's library version.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>
  /// The older versions (<see cref="UNKNOWN"/> for none recorded); empty when there are none, when the registry is
  /// absent, or when this instance's own version cannot be read, since nothing can then be judged older than it.
  /// </returns>
  /// <remarks>
  /// Versions compare by their numeric part, ignoring a prerelease or build suffix, so two builds of one release are the
  /// same release. Asked by a conversion an earlier release cannot read, which waits until no such instance is live.
  /// </remarks>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/StrongIdScalarRewriteIntegrationTests.cs:TheRewrite_WaitsWhileAnOlderReleaseIsLive_AndRunsOnceNoneIsAsync</tests>
  public static async Task<IReadOnlyList<string>> OlderLiveLibraryVersionsAsync(
      NpgsqlConnection connection,
      string schema,
      string ownLibraryVersion,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentNullException.ThrowIfNull(ownLibraryVersion);

    if (NumericPart(ownLibraryVersion) is not { } own) {
      return [];
    }

    var target = string.IsNullOrEmpty(schema) ? "public" : schema.Replace("\"", string.Empty);
    var quoted = "\"" + target.Replace("\"", "\"\"") + "\"";

    await using (var exists = new NpgsqlCommand(
        "SELECT to_regclass($1) IS NOT NULL AND to_regprocedure($2) IS NOT NULL", connection)) {
      exists.Parameters.AddWithValue($"{quoted}.wh_service_instances");
      exists.Parameters.AddWithValue($"{quoted}.wh_live_instance_ids()");
      if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true) {
        return [];
      }
    }

    await using var command = new NpgsqlCommand(
      "SELECT DISTINCT coalesce(library_version, $1) AS release "
      + $"FROM {quoted}.wh_service_instances "
      + $"WHERE instance_id IN (SELECT l.instance_id FROM {quoted}.wh_live_instance_ids() l) "
      + "ORDER BY release", connection);
    command.Parameters.AddWithValue(UNKNOWN);

    var older = new List<string>();
    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      var release = reader.GetString(0);
      if (NumericPart(release) is not { } version || version < own) {
        older.Add(release);
      }
    }
    return older;
  }

  /// <summary>The numeric part of a version (<c>1.2.3</c> of <c>1.2.3-local.4+abc</c>), or null when it has none.</summary>
  /// <param name="version">The version text.</param>
  /// <returns>The version, or null.</returns>
  internal static Version? NumericPart(string version) {
    ArgumentNullException.ThrowIfNull(version);
    var end = version.IndexOfAny(['-', '+']);
    var numeric = end < 0 ? version : version[..end];
    return Version.TryParse(numeric, out var parsed) ? parsed : null;
  }
}
