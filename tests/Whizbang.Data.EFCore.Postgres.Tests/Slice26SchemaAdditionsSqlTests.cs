using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Slice 26 step 1 — RED-first locks for the foundational schema additions that the
/// commit-sequence stamping subsystem (slice 26 step 2+) depends on.
///
/// <para><strong>Locked invariants:</strong></para>
/// <list type="bullet">
/// <item><description><c>wh_service_config</c> table exists with a single bootstrap row whose
/// <c>service_id</c> is auto-populated. Stable across deployments; this is the source-of-truth
/// for the local service's identity that rides on outgoing envelopes as <c>SourceServiceId</c>.</description></item>
/// <item><description><c>wh_event_store</c> gains <c>commit_sequence BIGINT NULL</c> (populated
/// post-commit by the stamper), <c>origin_service_id UUID NULL</c> and
/// <c>origin_commit_sequence BIGINT NULL</c> (set when an event is forwarded 1:1 from
/// another service).</description></item>
/// <item><description><c>wh_inbox</c> gains <c>source_service_id UUID NOT NULL</c> and
/// <c>source_commit_sequence BIGINT NOT NULL</c>. Loose-deserialization gate at receive
/// boundary drops messages missing these fields; rows that reach the table must have them.</description></item>
/// <item><description><c>wh_commit_seq</c> sequence exists; supplies monotonic BIGINT values
/// to the stamper.</description></item>
/// <item><description>Indexes for the stamper's hot-path query (find unstamped rows by xmin)
/// and for downstream-consumer joins on <c>(source_service_id, source_commit_sequence)</c>.</description></item>
/// </list>
/// </summary>
/// <docs>fundamentals/work-coordinator/commit-sequence</docs>
public class Slice26SchemaAdditionsSqlTests : EFCoreTestBase {

  // ----- wh_service_config -----

  [Test]
  public async Task WhServiceConfig_TableExistsAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var exists = await _columnExistsAsync(conn, "wh_service_config", "service_id");
    await Assert.That(exists).IsTrue()
      .Because("wh_service_config table with service_id column is required for envelope SourceServiceId");
  }

  [Test]
  public async Task WhServiceConfig_HasExactlyOneBootstrapRowAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var count = await _scalarAsync<long>(conn, "SELECT COUNT(*) FROM wh_service_config");
    await Assert.That(count).IsEqualTo(1L)
      .Because("singleton bootstrap row pattern (CHECK constraint forces single_row = TRUE)");
  }

  [Test]
  public async Task WhServiceConfig_ServiceIdIsNonNullAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var serviceId = await _scalarAsync<Guid>(conn, "SELECT service_id FROM wh_service_config");
    await Assert.That(serviceId).IsNotEqualTo(Guid.Empty)
      .Because("the bootstrap row's service_id must be auto-populated, not NULL or all-zeros");
  }

  // ----- wh_event_store new columns -----

  [Test]
  public async Task WhEventStore_HasCommitSequenceColumnAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var (exists, dataType, isNullable) = await _columnInfoAsync(conn, "wh_event_store", "commit_sequence");
    await Assert.That(exists).IsTrue();
    await Assert.That(dataType).IsEqualTo("bigint");
    await Assert.That(isNullable).IsEqualTo("YES")
      .Because("commit_sequence is stamped post-commit by the stamper; NULL means 'not yet stable'");
  }

  [Test]
  public async Task WhEventStore_HasOriginServiceIdColumnAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var (exists, dataType, isNullable) = await _columnInfoAsync(conn, "wh_event_store", "origin_service_id");
    await Assert.That(exists).IsTrue();
    await Assert.That(dataType).IsEqualTo("uuid");
    await Assert.That(isNullable).IsEqualTo("YES")
      .Because("origin_service_id is NULL for locally-originated events; populated only on 1:1 forwarding");
  }

  [Test]
  public async Task WhEventStore_HasOriginCommitSequenceColumnAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var (exists, dataType, isNullable) = await _columnInfoAsync(conn, "wh_event_store", "origin_commit_sequence");
    await Assert.That(exists).IsTrue();
    await Assert.That(dataType).IsEqualTo("bigint");
    await Assert.That(isNullable).IsEqualTo("YES");
  }

  // ----- wh_inbox new columns -----

  [Test]
  public async Task WhInbox_HasSourceServiceIdColumnNotNullAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var (exists, dataType, isNullable) = await _columnInfoAsync(conn, "wh_inbox", "source_service_id");
    await Assert.That(exists).IsTrue();
    await Assert.That(dataType).IsEqualTo("uuid");
    await Assert.That(isNullable).IsEqualTo("NO")
      .Because("loose-deserialization gate at receive boundary ensures messages without source_service_id are dropped; rows that land must have it");
  }

  [Test]
  public async Task WhInbox_HasSourceCommitSequenceColumnNotNullAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var (exists, dataType, isNullable) = await _columnInfoAsync(conn, "wh_inbox", "source_commit_sequence");
    await Assert.That(exists).IsTrue();
    await Assert.That(dataType).IsEqualTo("bigint");
    await Assert.That(isNullable).IsEqualTo("NO");
  }

  // ----- wh_commit_seq sequence -----

  [Test]
  public async Task WhCommitSeq_SequenceExistsAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var dataType = await _scalarAsync<string?>(conn,
      "SELECT data_type FROM information_schema.sequences WHERE sequence_name = 'wh_commit_seq'");
    await Assert.That(dataType).IsEqualTo("bigint")
      .Because("the stamper allocates from wh_commit_seq; sequence must exist as BIGINT");
  }

  [Test]
  public async Task WhCommitSeq_FirstValueIsOneAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var nextVal = await _scalarAsync<long>(conn, "SELECT nextval('wh_commit_seq')");
    await Assert.That(nextVal).IsEqualTo(1L)
      .Because("sequence starts at 1; downstream cursors compare with commit_sequence so 0/NULL is a sentinel for 'not stamped'");
  }

  // ----- indexes -----

  [Test]
  public async Task WhEventStore_HasCommitSequenceIndexAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var indexExists = await _indexExistsForColumnAsync(conn, "wh_event_store", "commit_sequence");
    await Assert.That(indexExists).IsTrue()
      .Because("downstream consumers ORDER BY commit_sequence; needs an index for efficient drain");
  }

  [Test]
  public async Task WhEventStore_HasUnstampedIndexForStamperAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    // Look for any index that targets unstamped rows. The stamper SQL is `WHERE commit_sequence IS NULL`,
    // a partial index of that exact shape gives the stamper hot-path performance even when the table grows.
    var found = await _scalarAsync<long>(conn, @"
      SELECT count(*) FROM pg_indexes
      WHERE tablename = 'wh_event_store'
        AND indexdef ILIKE '%commit_sequence IS NULL%'");
    await Assert.That(found).IsGreaterThan(0L)
      .Because("the stamper's hot-path filter is `commit_sequence IS NULL`; needs a partial index for cheap discovery");
  }

  [Test]
  public async Task WhInbox_HasSourceCommitSequenceIndexAsync() {
    await using var dbContext = CreateDbContext();
    var conn = await _openAsync(dbContext);
    var found = await _scalarAsync<long>(conn, @"
      SELECT count(*) FROM pg_indexes
      WHERE tablename = 'wh_inbox'
        AND indexdef ILIKE '%source_service_id%'
        AND indexdef ILIKE '%source_commit_sequence%'");
    await Assert.That(found).IsGreaterThan(0L)
      .Because("downstream consumers index cursor by (source_service_id, source_commit_sequence)");
  }

  // ============================================================================
  // helpers
  // ============================================================================

  private static async Task<NpgsqlConnection> _openAsync(WorkCoordinationDbContext dbContext) {
    var conn = (NpgsqlConnection)dbContext.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) {
      await conn.OpenAsync();
    }
    return conn;
  }

  private static async Task<bool> _columnExistsAsync(NpgsqlConnection conn, string table, string column) {
    var (exists, _, _) = await _columnInfoAsync(conn, table, column);
    return exists;
  }

  private static async Task<(bool Exists, string DataType, string IsNullable)> _columnInfoAsync(
      NpgsqlConnection conn, string table, string column) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT data_type, is_nullable
      FROM information_schema.columns
      WHERE table_name = @t AND column_name = @c";
    cmd.Parameters.AddWithValue("t", table);
    cmd.Parameters.AddWithValue("c", column);
    await using var reader = await cmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) {
      return (false, string.Empty, string.Empty);
    }
    return (true, reader.GetString(0), reader.GetString(1));
  }

  private static async Task<bool> _indexExistsForColumnAsync(NpgsqlConnection conn, string table, string column) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
      SELECT count(*) FROM pg_indexes
      WHERE tablename = @t AND indexdef ILIKE '%' || @c || '%'";
    cmd.Parameters.AddWithValue("t", table);
    cmd.Parameters.AddWithValue("c", column);
    var result = await cmd.ExecuteScalarAsync();
    return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture) > 0;
  }

  private static async Task<T> _scalarAsync<T>(NpgsqlConnection conn, string sql) {
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    var result = await cmd.ExecuteScalarAsync();
    if (result is null || result is DBNull) {
      return default!;
    }
    return (T)Convert.ChangeType(result, typeof(T).GetGenericArguments().FirstOrDefault() ?? typeof(T),
      System.Globalization.CultureInfo.InvariantCulture);
  }
}
