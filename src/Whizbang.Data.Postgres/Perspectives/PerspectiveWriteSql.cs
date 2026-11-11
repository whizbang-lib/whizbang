// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core.Perspectives;

namespace Whizbang.Data.Postgres.Perspectives;

/// <summary>
/// The statements a set-based perspective write is made of: one per group of writes that share a table and a
/// statement shape, shared by both PostgreSQL drivers.
/// </summary>
/// <remarks>
/// <para>
/// Each shape is the per-stream statement written for many rows. An upsert is
/// <c>INSERT … VALUES (…), (…) ON CONFLICT (id) DO UPDATE … WHERE &lt;ordering guard&gt;</c>, an insert of rows the
/// apply read as absent ends <c>ON CONFLICT (id) DO NOTHING</c>, and an update on a read version is
/// <c>UPDATE … FROM (VALUES …) WHERE id = r.id AND xmin = r.expected AND &lt;ordering guard&gt;</c>. Each returns
/// the ids it wrote, so a row it did not write is told apart from one it did without a second statement on the
/// common path. A purge is <c>DELETE … WHERE id = ANY(@ids)</c>.
/// </para>
/// <para>
/// Multi-row <c>VALUES</c> rather than <c>jsonb_to_recordset</c> or arrays: every value is bound as its own
/// parameter, exactly as the per-stream statement binds it, so a physical column's value (a vector, a converted
/// jsonb document, an enum's integer) needs no second encoding and the stored bytes match the per-stream write.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/write-modes</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Perspectives/SetBasedPerspectiveWriteSinkTests.cs</tests>
internal static class PerspectiveWriteSql {
  /// <summary>The parameters a statement binds once for all its rows: the system clock, the activity and version decisions.</summary>
  internal const int GROUP_PARAMETERS = 3;

  /// <summary>The parameters an upsert, insert or update binds per row, before its expiry and physical columns.</summary>
  internal const int ROW_PARAMETERS = 6;

  /// <summary>The parameters one row of the shape binds.</summary>
  internal static int ParametersPerRow(PerspectiveWriteShape shape) =>
    ROW_PARAMETERS + (shape.HasExpiry ? 1 : 0) + shape.PhysicalColumns.Count;

  /// <summary>The statement for <paramref name="rowCount"/> rows of an upsert, insert or update shape.</summary>
  internal static string RowStatement(PerspectiveWriteShape shape, int rowCount) =>
    shape.Operation == PerspectiveWriteOperation.Update ? _update(shape, rowCount) : _insert(shape, rowCount);

  /// <summary>The purge of a table's rows, by id.</summary>
  internal static string PurgeStatement(string table) => "DELETE FROM " + table + " WHERE id = ANY(@ids)";

  /// <summary>The read that explains rows a checked statement did not write: their current version, if any.</summary>
  internal static string VersionsStatement(string table) => "SELECT id, xmin FROM " + table + " WHERE id = ANY(@ids)";

  /// <summary>
  /// The parameter for one physical column's value, the same one the per-stream write binds: a converted document as
  /// <c>jsonb</c>, anything else as it is, and a null as a SQL null.
  /// </summary>
  /// <param name="name">The parameter name.</param>
  /// <param name="value">The value.</param>
  /// <returns>The parameter.</returns>
  internal static NpgsqlParameter PhysicalParameter(string name, PerspectivePhysicalValue value) =>
    value.IsJsonDocument && value.Value is not null
      ? new NpgsqlParameter(name, NpgsqlDbType.Jsonb) { Value = value.Value }
      : new NpgsqlParameter(name, value.Value ?? DBNull.Value);

  /// <summary>A column name, double-quoted, as the per-stream statement writes it.</summary>
  internal static string QuoteColumn(string column) => "\"" + column.Replace("\"", "\"\"") + "\"";

  /// <summary>
  /// Whether a store type can be written into a cast: letters, digits, spaces, and the punctuation a type name
  /// with a modifier or a schema uses. The type comes from the model's metadata, never from input; this keeps it so.
  /// </summary>
  internal static bool IsPlainStoreType(string storeType) =>
    !string.IsNullOrWhiteSpace(storeType)
    && storeType.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '_' or '(' or ')' or ',' or '[' or ']' or '.');

  private static string _insert(PerspectiveWriteShape shape, int rowCount) {
    var t = shape.Table;
    var sql = new StringBuilder("INSERT INTO ").Append(t)
      .Append(" (id, data, metadata, scope, created_at, updated_at, sys_created_at, sys_updated_at, version");
    if (shape.HasExpiry) {
      sql.Append(", expires_at");
    }
    foreach (var column in shape.PhysicalColumns) {
      sql.Append(", ").Append(QuoteColumn(column.Column));
    }
    sql.Append(") VALUES ");
    for (var row = 0; row < rowCount; row++) {
      sql.Append(row == 0 ? "(" : ", (")
        .Append("@i").Append(row).Append(", @d").Append(row).Append("::jsonb, @m").Append(row).Append("::jsonb, @s").Append(row)
        .Append("::jsonb, @c").Append(row).Append(", @u").Append(row).Append(", @wb_sys, @wb_sys, 1");
      if (shape.HasExpiry) {
        sql.Append(", @e").Append(row);
      }
      for (var p = 0; p < shape.PhysicalColumns.Count; p++) {
        sql.Append(", @p").Append(row).Append('_').Append(p);
      }
      sql.Append(')');
    }

    if (shape.Operation == PerspectiveWriteOperation.Insert) {
      return sql.Append(" ON CONFLICT (id) DO NOTHING RETURNING id").ToString();
    }

    sql.Append(" ON CONFLICT (id) DO UPDATE SET data = EXCLUDED.data, metadata = EXCLUDED.metadata, ")
      .Append("updated_at = CASE WHEN @wb_suppressactivity THEN ").Append(t).Append(".updated_at ELSE EXCLUDED.updated_at END, ")
      .Append("sys_updated_at = EXCLUDED.sys_updated_at, ");
    if (shape.HasExpiry) {
      sql.Append("expires_at = EXCLUDED.expires_at, ");
    }
    sql.Append("version = ").Append(t).Append(".version + @wb_versionbump");
    if (shape.ForceUpdateScope) {
      sql.Append(", scope = EXCLUDED.scope");
    }
    foreach (var column in shape.PhysicalColumns) {
      var quoted = QuoteColumn(column.Column);
      sql.Append(", ").Append(quoted).Append(" = EXCLUDED.").Append(quoted);
    }
    return sql.Append(" WHERE ")
      .Append(PerspectiveRowVersionCommands.OrderingGuard(t, "EXCLUDED.metadata", shape.OrdersOnEventId))
      .Append(" RETURNING id").ToString();
  }

  private static string _update(PerspectiveWriteShape shape, int rowCount) {
    var t = shape.Table;
    var sql = new StringBuilder("UPDATE ").Append(t)
      .Append(" SET data = r.data, metadata = r.metadata, updated_at = CASE WHEN @wb_suppressactivity THEN ")
      .Append(t).Append(".updated_at ELSE r.updated_at END, sys_updated_at = @wb_sys, ");
    if (shape.HasExpiry) {
      sql.Append("expires_at = r.expires_at, ");
    }
    sql.Append("version = ").Append(t).Append(".version + @wb_versionbump");
    if (shape.ForceUpdateScope) {
      sql.Append(", scope = r.scope");
    }
    for (var p = 0; p < shape.PhysicalColumns.Count; p++) {
      sql.Append(", ").Append(QuoteColumn(shape.PhysicalColumns[p].Column)).Append(" = r.p_").Append(p);
    }
    sql.Append(" FROM (VALUES ");
    for (var row = 0; row < rowCount; row++) {
      sql.Append(row == 0 ? "(" : ", (")
        .Append("@i").Append(row).Append("::uuid, @d").Append(row).Append("::jsonb, @m").Append(row).Append("::jsonb, @s").Append(row)
        .Append("::jsonb, @u").Append(row).Append(", @x").Append(row).Append("::xid");
      if (shape.HasExpiry) {
        sql.Append(", @e").Append(row).Append("::timestamptz");
      }
      for (var p = 0; p < shape.PhysicalColumns.Count; p++) {
        sql.Append(", @p").Append(row).Append('_').Append(p);
        // A VALUES list has no target column to take a type from, so a null alone would be read as text.
        if (shape.PhysicalColumns[p].StoreType is { } storeType) {
          sql.Append("::").Append(storeType);
        }
      }
      sql.Append(')');
    }
    sql.Append(") AS r(id, data, metadata, scope, updated_at, expected");
    if (shape.HasExpiry) {
      sql.Append(", expires_at");
    }
    for (var p = 0; p < shape.PhysicalColumns.Count; p++) {
      sql.Append(", p_").Append(p);
    }
    return sql.Append(") WHERE ").Append(t).Append(".id = r.id AND ").Append(t).Append(".xmin = r.expected AND (")
      .Append(PerspectiveRowVersionCommands.OrderingGuard(t, "r.metadata", shape.OrdersOnEventId))
      .Append(") RETURNING ").Append(t).Append(".id").ToString();
  }
}

/// <summary>A physical column of a statement shape: its name and the store type a set-based update casts to.</summary>
internal readonly record struct PerspectiveWriteColumn(string Column, string? StoreType);

/// <summary>
/// Everything about a group of writes that its statement's text depends on. Writes that share a shape share one
/// statement; anything that differs per row is a parameter of that row.
/// </summary>
/// <param name="Table">The schema-qualified, quoted table.</param>
/// <param name="Operation">The statement form.</param>
/// <param name="OrdersOnEventId">Which ordering guard the statement carries.</param>
/// <param name="ForceUpdateScope">Whether an update replaces the stored scope.</param>
/// <param name="SuppressActivity">Whether an update keeps the stored business time.</param>
/// <param name="BumpVersion">Whether an update moves the version column.</param>
/// <param name="HasExpiry">Whether the rows carry an expiry.</param>
/// <param name="PhysicalColumns">The physical columns, in order.</param>
/// <param name="Signature">The physical columns as one comparable string.</param>
internal sealed record PerspectiveWriteShape(
    string Table,
    PerspectiveWriteOperation Operation,
    bool OrdersOnEventId,
    bool ForceUpdateScope,
    bool SuppressActivity,
    bool BumpVersion,
    bool HasExpiry,
    IReadOnlyList<PerspectiveWriteColumn> PhysicalColumns,
    string Signature) {
  /// <summary>The shape of one write.</summary>
  internal static PerspectiveWriteShape Of(PendingPerspectiveWrite write) {
    if (write.Row is not { } row) {
      return new(write.Table, write.Operation, false, false, false, false, false, [], string.Empty);
    }
    var columns = new PerspectiveWriteColumn[row.PhysicalValues.Count];
    var signature = new StringBuilder();
    for (var i = 0; i < columns.Length; i++) {
      var value = row.PhysicalValues[i];
      columns[i] = new PerspectiveWriteColumn(value.Column, value.StoreType);
      signature.Append(value.Column).Append('\u001e').Append(value.StoreType).Append('\u001f');
    }
    return new(write.Table, write.Operation, row.OrdersOnEventId, row.ForceUpdateScope, row.SuppressActivity,
      row.BumpVersion, row.ExpiresAt.HasValue, columns, signature.ToString());
  }

  /// <summary>The fields two writes must share to share a statement.</summary>
  internal (string, PerspectiveWriteOperation, bool, bool, bool, bool, bool, string) Key =>
    (Table, Operation, OrdersOnEventId, ForceUpdateScope, SuppressActivity, BumpVersion, HasExpiry, Signature);
}
