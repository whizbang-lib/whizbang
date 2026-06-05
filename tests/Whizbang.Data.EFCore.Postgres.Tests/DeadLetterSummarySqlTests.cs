using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

#pragma warning disable CA1707, IDE1006, IDE0042

/// <summary>
/// Slice 6 of release/v0.645.0-alpha.1 (outbox-DLQ + dual-hash analysis) — locks
/// the <c>wh_dead_letter_summary</c> table + <c>aggregate_dead_letters()</c>
/// function: an operator/AI-friendly view that collapses the raw 38k+ row DLQ
/// queue into ~dozens of distinct fingerprint clusters with occurrence counts
/// and a representative error_text per cluster.
///
/// <para>Locked invariants:</para>
/// <list type="bullet">
/// <item><description>Two wh_dead_letters rows with the same error_text collapse
/// to a single summary row with <c>occurrence_count = 2</c>.</description></item>
/// <item><description>Distinct exception types produce distinct summary rows
/// (no cluster collision).</description></item>
/// <item><description>Version-aware backfill: rows tagged with a stale algorithm
/// version are re-hashed; rows tagged with the current version are NOT touched
/// (this is the "no unnecessary work" invariant — without it, every aggregation
/// tick would re-hash every row).</description></item>
/// <item><description>Sample preservation: the summary row's
/// <c>sample_error_text</c> is the most-recently-failed row's error_text so
/// operators see a concrete in-context example without re-querying raw
/// wh_dead_letters.</description></item>
/// </list>
/// </summary>
/// <docs>operations/dead-letter-queue/summary-aggregation</docs>
public class DeadLetterSummarySqlTests : EFCoreTestBase {

  private const string _stackInvalidOp = """
    System.InvalidOperationException: Could not open connection to 'jdx_bff'
       at Whizbang.Data.EFCore.Postgres.Functions.OutboxClaim.LeaseAsync(Guid instanceId)
    """;

  private const string _stackNullRef = """
    System.NullReferenceException: Object reference not set to an instance of an object
       at Whizbang.Data.EFCore.Postgres.Functions.InboxDispatch.ClaimAsync(Guid instanceId)
    """;

  // --- helpers ---

  private async Task<NpgsqlConnection> _openAsync() {
    var conn = (NpgsqlConnection)CreateDbContext().Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  /// <summary>
  /// Insert a wh_dead_letters row directly (bypassing move_to_dead_letters) so we
  /// can pre-stage rows with chosen error_fingerprint_version values — required by
  /// the version-bump test which simulates rows landed by an older algorithm.
  /// </summary>
  private static async Task _insertDlqRowAsync(
      NpgsqlConnection conn,
      string errorText,
      DateTimeOffset deadLetteredAt,
      short? fingerprintVersion = null,
      string sourceTable = "wh_outbox",
      string messageType = "TestMessage") {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      INSERT INTO wh_dead_letters (
        dead_letter_id, source_table, source_id, message_type,
        envelope, failure_reason, error_text, attempts_when_dlq,
        dead_lettered_at, generation,
        error_fingerprint, error_fingerprint_version
      ) VALUES (
        @dlq, @src, @src_id, @msg_type,
        '{}'::jsonb, 99, @err, 11,
        @at, 'test-gen',
        CASE WHEN @explicit_version::SMALLINT IS NULL THEN compute_dead_letter_fingerprint(@err) ELSE 'stale-tag-pad' END,
        @explicit_version
      )
      """;
    cmd.Parameters.AddWithValue("dlq", (Guid)TrackedGuid.NewMedo());
    cmd.Parameters.AddWithValue("src", sourceTable);
    cmd.Parameters.AddWithValue("src_id", (Guid)TrackedGuid.NewMedo());
    cmd.Parameters.AddWithValue("msg_type", messageType);
    cmd.Parameters.AddWithValue("err", errorText);
    cmd.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = deadLetteredAt });
    cmd.Parameters.Add(new NpgsqlParameter("explicit_version", NpgsqlDbType.Smallint) {
      Value = (object?)fingerprintVersion ?? DBNull.Value
    });
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task _aggregateAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT aggregate_dead_letters()";
    await cmd.ExecuteNonQueryAsync();
  }

  private static async Task<int> _summaryRowCountAsync(NpgsqlConnection conn) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM wh_dead_letter_summary";
    return (int)(long)(await cmd.ExecuteScalarAsync())!;
  }

  private static async Task<(long Count, string? Sample, DateTimeOffset FirstSeen, DateTimeOffset LastSeen)> _summaryForFingerprintAsync(
      NpgsqlConnection conn, string errorText) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
      SELECT s.occurrence_count, s.sample_error_text, s.first_seen_at, s.last_seen_at
      FROM wh_dead_letter_summary s
      WHERE s.error_fingerprint = compute_dead_letter_fingerprint(@err)
      """;
    cmd.Parameters.AddWithValue("err", errorText);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      throw new InvalidOperationException("no summary row for given error text");
    }
    return (
      Count: reader.GetInt64(0),
      Sample: reader.IsDBNull(1) ? null : reader.GetString(1),
      FirstSeen: reader.GetFieldValue<DateTimeOffset>(2),
      LastSeen: reader.GetFieldValue<DateTimeOffset>(3));
  }

  private static async Task<short?> _fingerprintVersionForRowAsync(NpgsqlConnection conn, string errorText) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT error_fingerprint_version FROM wh_dead_letters WHERE error_text = @err LIMIT 1";
    cmd.Parameters.AddWithValue("err", errorText);
    var raw = await cmd.ExecuteScalarAsync();
    return raw is DBNull or null ? null : (short)raw;
  }

  // --- tests ---

  [Test]
  public async Task AggregateDeadLetters_TwoRowsSameFingerprint_CollapsedToSingleSummaryRowAsync() {
    await using var conn = await _openAsync();
    var t1 = DateTimeOffset.UtcNow.AddMinutes(-10);
    var t2 = DateTimeOffset.UtcNow.AddMinutes(-5);
    await _insertDlqRowAsync(conn, _stackInvalidOp, deadLetteredAt: t1);
    await _insertDlqRowAsync(conn, _stackInvalidOp, deadLetteredAt: t2);

    await _aggregateAsync(conn);

    await Assert.That(await _summaryRowCountAsync(conn)).IsEqualTo(1)
      .Because("Same error_text → same fingerprint → must collapse to one summary row. Anything more is the bug operators wanted to avoid — the 38k+ row pre-Slice-6 mess.");
    var summary = await _summaryForFingerprintAsync(conn, _stackInvalidOp);
    await Assert.That(summary.Count).IsEqualTo(2L)
      .Because("occurrence_count = number of raw rows backing this cluster. Operators sort by count to find top failure modes.");
  }

  [Test]
  public async Task AggregateDeadLetters_DifferentExceptionTypes_ProducesDistinctSummaryRowsAsync() {
    await using var conn = await _openAsync();
    await _insertDlqRowAsync(conn, _stackInvalidOp, DateTimeOffset.UtcNow);
    await _insertDlqRowAsync(conn, _stackNullRef, DateTimeOffset.UtcNow);

    await _aggregateAsync(conn);

    await Assert.That(await _summaryRowCountAsync(conn)).IsEqualTo(2)
      .Because("InvalidOperationException + NullReferenceException are two distinct failure modes — they must NOT collapse into a single cluster.");
  }

  [Test]
  public async Task AggregateDeadLetters_VersionBump_RecomputesStaleRowsOnlyAsync() {
    await using var conn = await _openAsync();
    // Pre-stage rows tagged with version 0 (pretending they were landed by an older
    // algorithm). The current version is 1; the aggregator must re-hash these.
    await _insertDlqRowAsync(conn, _stackInvalidOp, DateTimeOffset.UtcNow.AddMinutes(-30), fingerprintVersion: 0);
    await _insertDlqRowAsync(conn, _stackNullRef, DateTimeOffset.UtcNow.AddMinutes(-20), fingerprintVersion: 0);
    var versionBefore = await _fingerprintVersionForRowAsync(conn, _stackInvalidOp);
    await Assert.That(versionBefore).IsEqualTo((short)0)
      .Because("Setup check: rows were pre-staged with version 0 so the aggregator has stale work to do.");

    await _aggregateAsync(conn);

    var versionAfter1 = await _fingerprintVersionForRowAsync(conn, _stackInvalidOp);
    var versionAfter2 = await _fingerprintVersionForRowAsync(conn, _stackNullRef);
    await Assert.That(versionAfter1).IsEqualTo((short)1)
      .Because("Stale rows MUST be re-hashed to the current version — without this, summary clusters would be split between old/new algorithm fingerprints for the same root cause.");
    await Assert.That(versionAfter2).IsEqualTo((short)1)
      .Because("Same as above — every stale row gets re-hashed.");

    // Now insert a row tagged with the current version (1). Run aggregate again.
    // Assert: the current-version row's fingerprint is NOT recomputed (no spurious
    // UPDATE — would burn IO on no-op work each maintenance tick).
    const string msgRow3Text = _stackInvalidOp;
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = """
        UPDATE wh_dead_letters
        SET error_fingerprint = 'sentinel0123abcd'
        WHERE error_text = @err
        """;
      cmd.Parameters.AddWithValue("err", msgRow3Text);
      await cmd.ExecuteNonQueryAsync();
    }

    await _aggregateAsync(conn);

    // After aggregator, since version is already at current, the row's fingerprint
    // should STILL be the sentinel (NOT recomputed).
    await using (var cmd = conn.CreateCommand()) {
      cmd.CommandText = "SELECT error_fingerprint FROM wh_dead_letters WHERE error_text = @err LIMIT 1";
      cmd.Parameters.AddWithValue("err", msgRow3Text);
      var stored = (string?)await cmd.ExecuteScalarAsync();
      await Assert.That(stored).IsEqualTo("sentinel0123abcd")
        .Because("Version-skip invariant: rows tagged with the current algorithm version MUST be left alone — otherwise every aggregation tick burns IO re-hashing 38k+ unchanged rows.");
    }
  }

  [Test]
  public async Task AggregateDeadLetters_SampleErrorText_IsMostRecentRowAsync() {
    await using var conn = await _openAsync();
    var older = DateTimeOffset.UtcNow.AddMinutes(-30);
    var newer = DateTimeOffset.UtcNow.AddMinutes(-5);
    // Insert two rows with the same fingerprint but slightly different error_text
    // (extra whitespace produces a different text but the algorithm normalizes —
    // actually our algorithm doesn't normalize whitespace, so make these textually
    // identical at the fingerprint-relevant slots but tag-distinguishable). Simpler:
    // make both rows the same error_text and rely on different dead_lettered_at to
    // verify "most recent" is selected as the sample.
    await _insertDlqRowAsync(conn, _stackInvalidOp + "\n   (older instance)", older);
    await _insertDlqRowAsync(conn, _stackInvalidOp + "\n   (newer instance)", newer);

    await _aggregateAsync(conn);

    var summary = await _summaryForFingerprintAsync(conn, _stackInvalidOp);
    await Assert.That(summary.Sample).IsNotNull();
    await Assert.That(summary.Sample).Contains("(newer instance)")
      .Because("sample_error_text MUST be the most-recently-failed row's text so operators looking at the summary table see the freshest example — older samples are useful for first-occurrence forensics but the dashboard view should track current state.");
  }
}
