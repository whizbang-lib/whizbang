// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;

namespace Whizbang.Data.EFCore.Postgres.Tests.Migrations;

/// <summary>
/// Issue #1728 (WS-2 S11): event bodies compress with lz4, and <c>toast_tuple_target</c> on <c>wh_event_body</c> stays
/// at the server's value, because lowering it was measured to compress none of the small rows and to move larger
/// values out of line instead.
/// </summary>
/// <docs>data/postgres#event-body-storage</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Migrations/290_EventBodyLz4Compression.sql</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class EventBodyCompressionTests : EFCoreTestBase {
  private const string MIGRATION_FILE = "290_EventBodyLz4Compression.sql";

  private async Task<object?> _scalarAsync(string sql) {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(sql, connection);
    return await command.ExecuteScalarAsync();
  }

  private async Task<string> _compressionOfAsync(string column) =>
    (string)(await _scalarAsync(
      $"SELECT attcompression::text FROM pg_attribute WHERE attrelid = to_regclass('wh_event_body') AND attname = '{column}'"))!;

  private async Task<string> _reloptionsAsync() =>
    (string)(await _scalarAsync(
      "SELECT COALESCE(array_to_string(reloptions, ','), '') FROM pg_class WHERE oid = to_regclass('wh_event_body')"))!;

  /// <summary>Stores one body whose JSON text is about <paramref name="bodyBytes"/> bytes, with metadata of about <paramref name="metadataBytes"/>.</summary>
  private async Task<Guid> _storeBodyAsync(int bodyBytes, int metadataBytes) {
    var eventId = Guid.NewGuid();
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    // Repeated line items, as an order or a document event carries: the shape that compresses in practice.
    await using var command = new NpgsqlCommand("""
      INSERT INTO wh_event_body (event_id, event_data, metadata)
      SELECT @id,
             jsonb_build_object('Items', (SELECT jsonb_agg(jsonb_build_object('Sku', 'SKU-' || lpad(g::text, 6, '0'), 'Quantity', g % 9, 'Description', 'Widget ' || g))
                                          FROM generate_series(1, @items) AS g)),
             jsonb_build_object('Hops', (SELECT jsonb_agg(jsonb_build_object('ServiceName', 'orders-api', 'Timestamp', '2026-10-01T00:00:00+00:00'))
                                         FROM generate_series(1, @hops) AS g))
      """, connection);
    command.Parameters.AddWithValue("id", eventId);
    // About 62 bytes of JSON text per item and 68 per hop.
    command.Parameters.AddWithValue("items", Math.Max(1, bodyBytes / 62));
    command.Parameters.AddWithValue("hops", Math.Max(1, metadataBytes / 68));
    await command.ExecuteNonQueryAsync();
    return eventId;
  }

  [Test]
  [Arguments("event_data")]
  [Arguments("metadata")]
  public async Task BothBodyColumns_CompressWithLz4Async(string column) {
    await Assert.That(await _compressionOfAsync(column)).IsEqualTo("l")
      .Because("lz4 measured 1.1% smaller, 26% faster to write and 11% faster to read in full than the server's default pglz");
  }

  [Test]
  public async Task TheBodyTable_KeepsTheServersToastTupleTargetAsync() {
    await Assert.That(await _reloptionsAsync()).DoesNotContain("toast_tuple_target")
      .Because("a lower target compresses no row under the 2 KB toast threshold and moves larger values out of line, adding TOAST reads");
  }

  [Test]
  public async Task ASecondBoot_LeavesTheCompressionAndTheTargetAsTheyAreAsync() {
    // The ledger row gone is what a re-run looks like to the migration pass: the file applies again.
    await _scalarAsync($"DELETE FROM wh_schema_migrations WHERE file_name = '{MIGRATION_FILE}'");
    await using (var context = CreateDbContext()) {
      await context.EnsureWhizbangDatabaseInitializedAsync();
    }

    await Assert.That(await _scalarAsync($"SELECT count(*) FROM wh_schema_migrations WHERE file_name = '{MIGRATION_FILE}'"))
      .IsEqualTo(1L).Because("the second boot must have applied the file again for this test to say anything");
    await Assert.That(await _compressionOfAsync("event_data")).IsEqualTo("l");
    await Assert.That(await _compressionOfAsync("metadata")).IsEqualTo("l");
    await Assert.That(await _reloptionsAsync()).DoesNotContain("toast_tuple_target");
  }

  [Test]
  public async Task ABodyInARowOverTheToastThreshold_IsStoredLz4CompressedAsync() {
    var eventId = await _storeBodyAsync(bodyBytes: 3000, metadataBytes: 600);

    await Assert.That(await _scalarAsync($"SELECT pg_column_compression(event_data) FROM wh_event_body WHERE event_id = '{eventId}'"))
      .IsEqualTo("lz4");
    await Assert.That(await _scalarAsync(
        $"SELECT pg_column_size(event_data) < octet_length(event_data::text) FROM wh_event_body WHERE event_id = '{eventId}'"))
      .IsEqualTo(true).Because("a compressed body takes fewer bytes than its text");
  }

  [Test]
  public async Task ARowUnderTheToastThreshold_IsStoredUncompressedAsync() {
    // The measured limit, held so the page cannot drift from it: a 1 KB body with slim metadata never reaches the
    // toaster, so no column setting compresses it. Compressing these is the dictionary work, not a table setting.
    var eventId = await _storeBodyAsync(bodyBytes: 1000, metadataBytes: 200);

    await Assert.That(await _scalarAsync($"SELECT pg_column_compression(event_data) IS NULL FROM wh_event_body WHERE event_id = '{eventId}'"))
      .IsEqualTo(true).Because("pg_column_compression is null for a value stored as it came");
  }
}
