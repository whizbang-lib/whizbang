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
/// A live instance is one that has heartbeated within the window, and its release is what its
/// heartbeat's metadata says. This instance's own row is ignored, and so is any instance on the
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
  /// <param name="liveWindow">How recent a heartbeat has to be for the instance to count as alive.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The distinct other releases, empty when the fleet is uniform or the registry is absent.</returns>
  public static async Task<IReadOnlyList<string>> OtherLiveVersionsAsync(
      NpgsqlConnection connection,
      string schema,
      Guid self,
      string ownVersion,
      TimeSpan liveWindow,
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
      "SELECT DISTINCT coalesce(metadata ->> 'Version', $3) AS release "
      + $"FROM {quoted}.wh_service_instances "
      + "WHERE instance_id <> $1 AND last_heartbeat_at > now() - $2 "
      + "ORDER BY release", connection);
    command.Parameters.AddWithValue(self);
    command.Parameters.AddWithValue(liveWindow);
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
}
