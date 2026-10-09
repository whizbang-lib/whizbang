// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using Npgsql;

namespace Whizbang.Data.Postgres.Schema;

/// <summary>
/// Reads and pins a schema's managed-object ledger from outside the service: what <c>whizbang schema status</c>,
/// <c>plan</c>, <c>pin</c> and <c>unpin</c> run.
/// </summary>
/// <docs>fundamentals/perspectives/managed-schema-objects#dba-runbook</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/ManagedSchemaLedgerTests.cs</tests>
public static class ManagedSchemaLedger {
  /// <summary>Every row of the schema's ledger, by table and name; empty when the schema has no ledger.</summary>
  public static async Task<IReadOnlyList<LedgerRow>> StatusAsync(
      NpgsqlConnection connection, string schema, CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(connection);
    var ledgerTable = _ledgerTable(schema);
    await using (var exists = new NpgsqlCommand("SELECT to_regclass(@t) IS NOT NULL", connection)) {
      exists.Parameters.AddWithValue("t", ledgerTable);
      if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true) {
        return [];
      }
    }
    var rows = await ManagedSchemaCatalog.ReadLedgerAsync(connection, ledgerTable, cancellationToken).ConfigureAwait(false);
    return [.. rows.OrderBy(r => r.Table, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.Ordinal)];
  }

  /// <summary>
  /// The objects the next start drops, as far as the ledger can say: Whizbang's, pending retirement, and pinned by
  /// neither pin. The fleet gate and the settings of the service that starts can still keep one.
  /// </summary>
  public static async Task<IReadOnlyList<LedgerRow>> PendingRetirementAsync(
      NpgsqlConnection connection, string schema, CancellationToken cancellationToken = default) =>
    [.. (await StatusAsync(connection, schema, cancellationToken).ConfigureAwait(false))
      .Where(r => r.Owner == ManagedOwners.WHIZBANG && r.Status == ManagedStatuses.PENDING_RETIREMENT
        && !r.CodePinned && !r.DbPinned)];

  /// <summary>Sets the database pin, recorded as set from the CLI. Returns <c>pinned</c>.</summary>
  public static Task<string> PinAsync(
      NpgsqlConnection connection, string schema, string table, string name, string? reason,
      CancellationToken cancellationToken = default) =>
    _callAsync(connection, $"SELECT {PgIdentifier.Quote(schema)}.wh_pin_object(@t, @n, @r, 'cli')",
      cancellationToken, ("t", table), ("n", name), ("r", (object?)reason ?? DBNull.Value));

  /// <summary>
  /// Releases the database pin. Returns <c>unpinned</c>, <c>absent</c>, or <c>unpinned; still pinned by …</c> when C#
  /// also pins the object.
  /// </summary>
  public static Task<string> UnpinAsync(
      NpgsqlConnection connection, string schema, string table, string name, CancellationToken cancellationToken = default) =>
    _callAsync(connection, $"SELECT {PgIdentifier.Quote(schema)}.wh_unpin_object(@t, @n)",
      cancellationToken, ("t", table), ("n", name));

  /// <summary>The rows as a table for a terminal.</summary>
  public static string Format(IReadOnlyList<LedgerRow> rows) {
    ArgumentNullException.ThrowIfNull(rows);
    if (rows.Count == 0) {
      return "No managed objects.";
    }
    var sb = new StringBuilder();
    foreach (var row in rows) {
      var pins = new List<string>();
      if (row.CodePinned) {
        pins.Add(_pin(row.CodePinSource, row.CodePinReason));
      }
      if (row.DbPinned) {
        pins.Add(_pin(row.DbPinSource, row.DbPinReason));
      }
      sb.Append(CultureInfo.InvariantCulture,
        $"{row.Table,-32} {row.Name,-48} {ManagedSchemaCatalog.FormatKind(row.Kind),-11} {row.Owner ?? "-",-9} {row.Status,-19}");
      sb.AppendLine(pins.Count == 0 ? string.Empty : "pinned by " + string.Join("; ", pins));
    }
    return sb.ToString().TrimEnd();
  }

  private static string _pin(string? source, string? reason) => reason is null ? source ?? "?" : $"{source}: {reason}";

  private static string _ledgerTable(string schema) => PgIdentifier.Quote(schema) + ".wh_managed_objects";

  private static async Task<string> _callAsync(
      NpgsqlConnection connection, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters) {
    ArgumentNullException.ThrowIfNull(connection);
    await using var command = new NpgsqlCommand(sql, connection);
    foreach (var (name, value) in parameters) {
      command.Parameters.AddWithValue(name, value);
    }
    return (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
  }
}
