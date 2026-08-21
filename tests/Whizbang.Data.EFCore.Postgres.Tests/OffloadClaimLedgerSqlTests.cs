using Npgsql;
using TUnit.Core;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Locks the offload-claim ledger: the database's record of every transport-offloaded blob, kept so
/// cleanup is a query instead of a container listing.
/// </summary>
/// <remarks>
/// <para>
/// The claim (storage key + provider) otherwise exists only inside the claim envelope in
/// <c>wh_outbox</c>/<c>wh_inbox</c>, and those rows are deleted on completion — after which the
/// database knows nothing about the blob. In passive-cleanup mode (the default, and the only safe
/// mode for fan-out) the blob would then live forever unless a provider-side lifecycle rule
/// happens to exist. The ledger closes that: one row per upload, expiry evaluated against
/// <c>uploaded_at</c> (DB clock) at sweep time — so changing the window is retroactive over
/// existing blobs by construction, with nothing stamped per blob.
/// </para>
/// <para>
/// The invariant the sweep upholds: the ledger row outlives the blob, never the reverse. A failed
/// blob delete leaves its row for automatic retry next sweep; a row whose blob is already gone
/// resolves as success because <c>DeleteAsync</c> is idempotent on missing.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/body-offload</docs>
[Category("Integration")]
[NotInParallel("OffloadClaimLedger")]
[Category("Shard2")]
public class OffloadClaimLedgerSqlTests : EFCoreTestBase {

  private static IWorkCoordinator _coordinator(WorkCoordinationDbContext ctx) =>
    new EFCoreWorkCoordinator<WorkCoordinationDbContext>(
      ctx, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions());

  private async Task _cleanupAsync(NpgsqlConnection conn) {
    await using var cmd = new NpgsqlCommand(
      "DELETE FROM wh_offload_claims WHERE storage_key LIKE 'test-ledger/%'; " +
      "DELETE FROM wh_settings WHERE setting_key = 'offload_claim_sweep_last_run';", conn);
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _backdateAsync(NpgsqlConnection conn, string key, int days) {
    await using var cmd = new NpgsqlCommand(
      "UPDATE wh_offload_claims SET uploaded_at = NOW() - make_interval(days => @d) " +
      "WHERE storage_key = @k", conn);
    cmd.Parameters.AddWithValue("d", days);
    cmd.Parameters.AddWithValue("k", key);
    await cmd.ExecuteNonQueryAsync();
  }

  [Test]
  public async Task Record_ThenExpiredQuery_ReturnsOnlyClaimsPastTheWindowAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _cleanupAsync(conn);
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);

    await coordinator.RecordOffloadClaimAsync("test-ledger/old", "blob-a");
    await coordinator.RecordOffloadClaimAsync("test-ledger/fresh", "blob-a");
    await _backdateAsync(conn, "test-ledger/old", days: 40);

    var expired = await coordinator.GetExpiredOffloadClaimsAsync(TimeSpan.FromDays(30), batchSize: 100);

    await Assert.That(expired.Select(c => c.StorageKey)).Contains("test-ledger/old")
      .Because("a claim uploaded past the window is exactly what the sweep exists to find");
    await Assert.That(expired.Select(c => c.StorageKey)).DoesNotContain("test-ledger/fresh")
      .Because("expiry is evaluated against uploaded_at at query time — a fresh blob must never be "
        + "offered for deletion, whatever the window is later changed to");
    await Assert.That(expired.First(c => c.StorageKey == "test-ledger/old").ProviderName)
      .IsEqualTo("blob-a")
      .Because("the sweep resolves the keyed store per claim, so the provider must travel with the key");
    await _cleanupAsync(conn);
  }

  [Test]
  public async Task Record_IsIdempotentAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _cleanupAsync(conn);
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);

    await coordinator.RecordOffloadClaimAsync("test-ledger/dup", "blob-a");
    await coordinator.RecordOffloadClaimAsync("test-ledger/dup", "blob-a");

    await using var count = new NpgsqlCommand(
      "SELECT COUNT(*) FROM wh_offload_claims WHERE storage_key = 'test-ledger/dup'", conn);
    var rows = Convert.ToInt64(
      await count.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    await Assert.That(rows).IsEqualTo(1)
      .Because("at-least-once dispatch can retry the same upload path; the ledger must absorb the "
        + "replay rather than throw into the dispatch pipeline");
    await _cleanupAsync(conn);
  }

  [Test]
  public async Task Remove_DeletesOnlyTheGivenKeysAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _cleanupAsync(conn);
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);

    await coordinator.RecordOffloadClaimAsync("test-ledger/deleted", "blob-a");
    await coordinator.RecordOffloadClaimAsync("test-ledger/failed", "blob-a");

    // The sweep removes rows ONLY for blobs whose delete succeeded — the failed one keeps its row
    // and is retried next sweep. This is the row-outlives-blob invariant at the SQL layer.
    await coordinator.RemoveOffloadClaimsAsync(["test-ledger/deleted"]);

    await using var remaining = new NpgsqlCommand(
      "SELECT storage_key FROM wh_offload_claims WHERE storage_key LIKE 'test-ledger/%'", conn);
    var keys = new List<string>();
    await using (var reader = await remaining.ExecuteReaderAsync()) {
      while (await reader.ReadAsync()) {
        keys.Add(reader.GetString(0));
      }
    }
    await Assert.That(keys).Contains("test-ledger/failed")
      .Because("a failed blob delete must leave its ledger row in place so the sweep retries it — "
        + "removing it would orphan the blob with no record, the exact failure the ledger kills");
    await Assert.That(keys).DoesNotContain("test-ledger/deleted");
    await _cleanupAsync(conn);
  }

  [Test]
  public async Task TryClaimSweep_SecondClaimInsideTheWindow_LosesAsync() {
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await _cleanupAsync(conn);
    await using var ctx = CreateDbContext();
    var coordinator = _coordinator(ctx);

    var first = await coordinator.TryClaimOffloadSweepAsync(TimeSpan.FromHours(1));
    var second = await coordinator.TryClaimOffloadSweepAsync(TimeSpan.FromHours(1));

    await Assert.That(first).IsTrue();
    await Assert.That(second).IsFalse()
      .Because("the sweep is once-per-service-per-window: replicas race on the wh_settings CAS "
        + "watermark and exactly one wins — N replicas must not issue N delete storms against the "
        + "same container");
    await _cleanupAsync(conn);
  }
}
