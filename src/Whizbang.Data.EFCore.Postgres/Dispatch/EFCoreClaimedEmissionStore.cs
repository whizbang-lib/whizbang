using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Dispatch;

/// <summary>
/// PostgreSQL-backed <see cref="IClaimedEmissionStore"/>. Atomicity is
/// enforced by <c>INSERT … ON CONFLICT DO NOTHING</c> against
/// <c>wh_unique_emission_claims</c>: the first INSERT for a given
/// <c>claim_key</c> affects one row; subsequent attempts affect zero.
/// </summary>
/// <remarks>
/// <para>
/// The INSERT runs on the ambient <see cref="DbContext"/>'s connection,
/// so when the caller is inside an EF Core transaction the claim
/// participates — a rollback of the outer scope releases the claim and
/// the invariant <em>claim taken iff emission committed</em> holds.
/// </para>
/// <para>
/// AOT-clean: no reflection, no expression compilation. Parameterized
/// raw SQL via <see cref="NpgsqlCommand"/>.
/// </para>
/// </remarks>
/// <docs>fundamentals/dispatcher/publish-once</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Dispatch/ClaimedEmissionStoreTests.cs:TryClaim_TwoConcurrentSameKey_ExactlyOneWinsAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Dispatch/ClaimedEmissionStoreTests.cs:TryClaim_SecondAttemptSameKey_ReturnsFalseAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Dispatch/ClaimedEmissionStoreTests.cs:TryClaim_DistinctKeys_BothWinAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Dispatch/ClaimedEmissionStoreTests.cs:FindClaimed_ReturnsExactlyTheHeldKeysAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Dispatch/ClaimedEmissionStoreTests.cs:FindClaimedAndRelease_SchemaScopedDbContext_UseTheModelSchemaAsync</tests>
[SuppressMessage("csharpsquid", "S2077:Formatting SQL queries is security-sensitive",
  Justification = "The only interpolated value is a schema-qualified SQL identifier " +
    "(\"schema\".wh_unique_emission_claims) resolved from the EF Core model's configured schema " +
    "(HasDefaultSchema), not user input. SQL identifiers cannot be parameterized; there is no " +
    "injection vector. All row values are @parameters.")]
public sealed class EFCoreClaimedEmissionStore(DbContext dbContext) : IClaimedEmissionStore {

  private readonly DbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

  // wh_unique_emission_claims lives in the service schema (migration 060 creates it at
  // __SCHEMA__.wh_unique_emission_claims); qualify it like EFCoreDeadLetterStore does — a
  // bare INSERT resolves through search_path, which is not guaranteed to include the
  // service schema. Two failure shapes: search_path lacks the table (42P01, every claim
  // throws), or search_path resolves to ANOTHER schema that also has it (the claim
  // silently lands in the wrong table — exactly-once against the wrong schema). The
  // schema comes from the EF model (HasDefaultSchema via OutboxRecord's mapping), never
  // from user input.
  private string _table() {
    var schema = _dbContext.Model.FindEntityType(typeof(OutboxRecord))?.GetSchema();
    return string.IsNullOrWhiteSpace(schema) || schema == "public"
      ? "wh_unique_emission_claims"
      : $"{Whizbang.Data.Postgres.PgIdentifier.Quote(schema)}.wh_unique_emission_claims";
  }

  public async Task<bool> TryClaimAsync(string claimKey, Guid claimedByEventId, CancellationToken cancellationToken) {
    ArgumentException.ThrowIfNullOrWhiteSpace(claimKey);

    await using var cmd = await _commandAsync(cancellationToken).ConfigureAwait(false);
    cmd.CommandText = $"""
      INSERT INTO {_table()} (claim_key, claimed_by_event_id)
      VALUES (@key, @eventId)
      ON CONFLICT (claim_key) DO NOTHING
      """;

    var keyParam = cmd.CreateParameter();
    keyParam.ParameterName = "@key";
    keyParam.Value = claimKey;
    cmd.Parameters.Add(keyParam);

    var idParam = cmd.CreateParameter();
    idParam.ParameterName = "@eventId";
    idParam.Value = claimedByEventId;
    cmd.Parameters.Add(idParam);

    var affected = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    return affected == 1;
  }

  /// <inheritdoc />
  /// <remarks>
  /// Reads the rows by primary key and ignores <c>expires_at</c>, because <see cref="TryClaimAsync"/>
  /// conflicts on the key alone: a key reads as held for exactly as long as a claim on it would lose.
  /// </remarks>
  public async Task<IReadOnlySet<string>?> FindClaimedAsync(IReadOnlyCollection<string> claimKeys, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(claimKeys);
    if (claimKeys.Count == 0) {
      return new HashSet<string>(StringComparer.Ordinal);
    }

    await using var cmd = await _commandAsync(cancellationToken).ConfigureAwait(false);
    cmd.CommandText = $"SELECT claim_key FROM {_table()} WHERE claim_key = ANY(@keys)";
    cmd.Parameters.AddWithValue("keys", claimKeys.ToArray());

    var held = new HashSet<string>(StringComparer.Ordinal);
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
      held.Add(reader.GetString(0));
    }
    return held;
  }

  /// <inheritdoc />
  public async Task<bool> ReleaseAsync(string claimKey, CancellationToken cancellationToken) {
    ArgumentException.ThrowIfNullOrWhiteSpace(claimKey);

    await using var cmd = await _commandAsync(cancellationToken).ConfigureAwait(false);
    cmd.CommandText = $"DELETE FROM {_table()} WHERE claim_key = @key";
    cmd.Parameters.AddWithValue("key", claimKey);
    return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
  }

  /// <inheritdoc />
  /// <remarks>
  /// <c>starts_with</c> rather than <c>LIKE</c>, so a <c>_</c> or <c>%</c> in the prefix is literal and
  /// cannot stretch the delete over another owner's keys.
  /// </remarks>
  public async Task<int> PruneAsync(string keyPrefix, DateTimeOffset claimedBefore, CancellationToken cancellationToken) {
    ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);

    await using var cmd = await _commandAsync(cancellationToken).ConfigureAwait(false);
    cmd.CommandText = $"DELETE FROM {_table()} WHERE starts_with(claim_key, @prefix) AND claimed_at < @before";
    cmd.Parameters.AddWithValue("prefix", keyPrefix);
    cmd.Parameters.AddWithValue("before", claimedBefore);
    return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <inheritdoc />
  /// <remarks>
  /// Oldest expiry first, through the <c>expires_at</c> index, and <c>starts_with</c> for the retained
  /// prefixes so a <c>_</c> or <c>%</c> in one is literal.
  /// </remarks>
  public async Task<int> PruneExpiredAsync(
      DateTimeOffset expiredBefore, IReadOnlyCollection<string> retainedKeyPrefixes, int maxClaims, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(retainedKeyPrefixes);
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxClaims);

    var table = _table();
    await using var cmd = await _commandAsync(cancellationToken).ConfigureAwait(false);
    cmd.CommandText = $"""
      DELETE FROM {table} WHERE claim_key IN (
        SELECT c.claim_key FROM {table} c
        WHERE c.expires_at < @before
          AND NOT EXISTS (SELECT 1 FROM unnest(@retained) AS r(prefix) WHERE starts_with(c.claim_key, r.prefix))
        ORDER BY c.expires_at
        LIMIT @max)
      """;
    cmd.Parameters.AddWithValue("before", expiredBefore);
    cmd.Parameters.AddWithValue("retained", retainedKeyPrefixes.ToArray());
    cmd.Parameters.AddWithValue("max", maxClaims);
    return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <summary>A command on the ambient context's connection, opened if it is not yet.</summary>
  private async Task<NpgsqlCommand> _commandAsync(CancellationToken cancellationToken) {
    var conn = (NpgsqlConnection)_dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
    }
    return conn.CreateCommand();
  }
}
