using Microsoft.EntityFrameworkCore;
using Npgsql;
using Whizbang.Core.Dispatch;

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
public sealed class EFCoreClaimedEmissionStore(DbContext dbContext) : IClaimedEmissionStore {

  private readonly DbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

  public async Task<bool> TryClaimAsync(string claimKey, Guid claimedByEventId, CancellationToken cancellationToken) {
    ArgumentException.ThrowIfNullOrWhiteSpace(claimKey);

    var conn = (NpgsqlConnection)_dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_unique_emission_claims (claim_key, claimed_by_event_id)
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
}
