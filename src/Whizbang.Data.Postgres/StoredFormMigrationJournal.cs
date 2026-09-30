using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>Where a stored-form migration stands.</summary>
/// <docs>fundamentals/perspectives/stored-form-migrations#journal</docs>
public enum StoredFormMigrationState {
  /// <summary>Declared, and never applied: it runs on the next start that does the schema work.</summary>
  Pending,

  /// <summary>Applied, and not settled: a generated migration that converted rows runs once more.</summary>
  Applied,

  /// <summary>Settled: skipped at startup, with no scan.</summary>
  Settled,
}

/// <summary>One stored-form migration's status: what the journal says about it, or that it has no row yet.</summary>
/// <param name="Name">The migration's name.</param>
/// <param name="Table">The perspective table.</param>
/// <param name="Kind"><c>generated</c> or <c>custom</c>.</param>
/// <param name="State">Pending, Applied or Settled.</param>
/// <param name="DeclaredAt">When a pass first declared it, or <see langword="null"/> when none has.</param>
/// <param name="FirstAppliedAt">The first pass that ran it.</param>
/// <param name="LastAppliedAt">The latest pass that ran it.</param>
/// <param name="RowsConverted">The row updates across its passes.</param>
/// <param name="SettledAt">When it stopped needing to run.</param>
/// <docs>fundamentals/perspectives/stored-form-migrations#status</docs>
public sealed record StoredFormMigrationStatus(
    string Name,
    string Table,
    string Kind,
    StoredFormMigrationState State,
    DateTimeOffset? DeclaredAt,
    DateTimeOffset? FirstAppliedAt,
    DateTimeOffset? LastAppliedAt,
    long RowsConverted,
    DateTimeOffset? SettledAt);

/// <summary>
/// Reads the stored-form migration journal (<c>wh_stored_form_migrations</c>), for the generated
/// <c>GetStoredFormMigrationStatusAsync</c> and for <c>whizbang stored-forms status</c>.
/// </summary>
/// <docs>fundamentals/perspectives/stored-form-migrations#status</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/StoredFormMigrationTests.cs</tests>
public static class StoredFormMigrationJournal {
  /// <summary>
  /// The status of every stored-form migration in a schema.
  /// </summary>
  /// <param name="connection">An open connection.</param>
  /// <param name="schema">The schema, unquoted.</param>
  /// <param name="declared">
  /// The migrations a build declares, listed first and in their order, each merged with its journal row (Pending
  /// when it has none), followed by any journal rows no declaration names. <see langword="null"/> reads the journal
  /// alone, ordered by table and name, as a tool with no build to ask does.
  /// </param>
  /// <param name="cancellationToken">Cancellation token.</param>
  public static async Task<IReadOnlyList<StoredFormMigrationStatus>> ReadAsync(
      NpgsqlConnection connection,
      string schema,
      IReadOnlyList<StoredFormMigration>? declared = null,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentException.ThrowIfNullOrWhiteSpace(schema);

    var journal = $"{SqlText.Identifier(schema)}.{StoredFormMigrationSql.JOURNAL}";
    var recorded = new List<StoredFormMigrationStatus>();
    await using (var exists = new NpgsqlCommand("SELECT to_regclass($1) IS NOT NULL", connection)) {
      exists.Parameters.AddWithValue(journal);
      if (await exists.ExecuteScalarAsync(cancellationToken) is true) {
        await using var command = new NpgsqlCommand(
          $"SELECT name, table_name, kind, declared_at, first_applied_at, last_applied_at, rows_converted, settled_at FROM {journal} ORDER BY table_name, name",
          connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
          DateTimeOffset? at(int ordinal) =>
            reader.IsDBNull(ordinal) ? null : new DateTimeOffset(reader.GetDateTime(ordinal), TimeSpan.Zero);
          var settledAt = at(7);
          var firstAppliedAt = at(4);
          recorded.Add(new StoredFormMigrationStatus(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            _state(firstAppliedAt, settledAt),
            at(3), firstAppliedAt, at(5), reader.GetInt64(6), settledAt));
        }
      }
    }

    if (declared is null) {
      return recorded;
    }

    var byName = recorded.ToDictionary(r => r.Name, StringComparer.Ordinal);
    var merged = declared
      .Select(d => byName.TryGetValue(d.Name, out var row)
        ? row
        : new StoredFormMigrationStatus(
          d.Name, d.Table, StoredFormMigrationSql.KindName(d.Kind),
          StoredFormMigrationState.Pending, null, null, null, 0, null))
      .ToList();
    var declaredNames = declared.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
    merged.AddRange(recorded.Where(r => !declaredNames.Contains(r.Name)));
    return merged;
  }

  private static StoredFormMigrationState _state(DateTimeOffset? firstAppliedAt, DateTimeOffset? settledAt) {
    if (settledAt is not null) {
      return StoredFormMigrationState.Settled;
    }
    return firstAppliedAt is not null ? StoredFormMigrationState.Applied : StoredFormMigrationState.Pending;
  }

  /// <summary>A fixed-width report of the statuses, one migration per line under a header.</summary>
  /// <param name="statuses">The statuses, in the order to list them.</param>
  public static string Format(IReadOnlyList<StoredFormMigrationStatus> statuses) {
    ArgumentNullException.ThrowIfNull(statuses);
    if (statuses.Count == 0) {
      return "No stored-form migrations are declared or recorded.";
    }

    var nameWidth = Math.Max("Migration".Length, statuses.Max(s => s.Name.Length)) + 2;
    var tableWidth = Math.Max("Table".Length, statuses.Max(s => s.Table.Length)) + 2;
    static string time(DateTimeOffset? at) =>
      at?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "";

    var sb = new StringBuilder();
    sb.Append("State".PadRight(10)).Append("Migration".PadRight(nameWidth)).Append("Table".PadRight(tableWidth))
      .Append("Rows".PadLeft(8)).Append("  ").Append("Applied".PadRight(21)).Append("Settled").AppendLine();
    foreach (var s in statuses) {
      var line = s.State.ToString().PadRight(10) + s.Name.PadRight(nameWidth) + s.Table.PadRight(tableWidth)
        + s.RowsConverted.ToString(CultureInfo.InvariantCulture).PadLeft(8) + "  "
        + time(s.LastAppliedAt).PadRight(21) + time(s.SettledAt);
      sb.AppendLine(line.TrimEnd());
    }
    return sb.ToString().TrimEnd();
  }
}
