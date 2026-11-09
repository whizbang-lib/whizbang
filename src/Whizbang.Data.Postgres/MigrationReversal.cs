// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Reflection;
using System.Text.RegularExpressions;
using Npgsql;
using Whizbang.Core.Versioning;

namespace Whizbang.Data.Postgres;

/// <summary>
/// How one applied schema change is reversed (#2122, owner decision 2): its kind and, for a reversible or lossy change,
/// the script that reverses it. The version that applies a change stores this in the migration ledger beside it, so an
/// older version, which cannot know a newer migration any other way, rolls back from the record.
/// </summary>
/// <remarks>
/// <para>
/// A library migration declares its kind in its header, <c>-- Reversal: reversible</c>, <c>lossy</c> or
/// <c>irreversible</c> (optionally followed by why), and a reversible or lossy one ships its reverse beside it as
/// <c>NNN_Name.down.sql</c>, embedded under <c>MigrationReversals</c> so it is never applied as a forward migration. Every
/// migration numbered through <see cref="UNDECLARED_THROUGH"/> predates the declaration: it records nothing and counts as
/// irreversible. The build guard fails a later migration that declares nothing, a reversible one with no reverse, and an
/// irreversible one with one.
/// </para>
/// <para>
/// Generated application schema has its reverse generated: a perspective table a pass created is dropped
/// (<see cref="DropTable"/>, lossy, since the rows the newer version projected go with it); the associations are
/// registered again by the older version's own pass (<see cref="RegisteredAgain"/>); a perspective table a pass changed
/// cannot be reversed, since the newer version does not know the older shape.
/// </para>
/// </remarks>
/// <param name="Kind">How the change is reversed.</param>
/// <param name="DownSql">The script that reverses it; null for an irreversible change, empty when nothing needs to run.</param>
/// <docs>operations/infrastructure/migrations#how-a-migration-is-reversed</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/MigrationReversalTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/MigrationReversalDeclarationGuardTests.cs</tests>
public sealed partial record MigrationReversal(SchemaChangeReversal Kind, string? DownSql) {
  /// <summary>The last migration number that predates the declaration: it and every one before it count as irreversible.</summary>
  public const int UNDECLARED_THROUGH = 375;

  /// <summary>The resource folder a library assembly embeds its reverse scripts under.</summary>
  public const string RESOURCE_FOLDER = "MigrationReversals";

  private const string REVERSIBLE = "reversible";
  private const string LOSSY = "lossy";
  private const string IRREVERSIBLE = "irreversible";

  /// <summary>The associations' reverse: nothing runs, and the older version's own pass registers its own again.</summary>
  public static readonly MigrationReversal RegisteredAgain = new(SchemaChangeReversal.Reversible, string.Empty);

  /// <summary>A change that cannot be reversed.</summary>
  public static readonly MigrationReversal Irreversible = new(SchemaChangeReversal.Irreversible, null);

  [GeneratedRegex(@"^--\s*Reversal:\s*(\S+)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
  private static partial Regex _header();

  /// <summary>The kind a migration's header declares; null when it declares none.</summary>
  /// <param name="forwardSql">The migration's text.</param>
  /// <returns>The kind, or null.</returns>
  /// <exception cref="InvalidOperationException">The header names a kind that is not one of the three.</exception>
  public static SchemaChangeReversal? DeclaredBy(string forwardSql) {
    ArgumentNullException.ThrowIfNull(forwardSql);
    var match = _header().Match(forwardSql);
    if (!match.Success) {
      return null;
    }
    var word = match.Groups[1].Value;
    return word switch {
      REVERSIBLE => SchemaChangeReversal.Reversible,
      LOSSY => SchemaChangeReversal.Lossy,
      IRREVERSIBLE => SchemaChangeReversal.Irreversible,
      _ => throw new InvalidOperationException(
        $"A migration declares '-- Reversal: {word}', which is not reversible, lossy or irreversible."),
    };
  }

  /// <summary>
  /// How a library migration is reversed: null for one that declares nothing (it predates the declaration), otherwise
  /// its declared kind with its reverse script read from <paramref name="source"/> and transformed as the forward text is.
  /// </summary>
  /// <param name="fileName">The migration's file name, with or without <c>.sql</c>.</param>
  /// <param name="forwardSql">The migration's text, as embedded.</param>
  /// <param name="transform">What the runner does to a migration's text before running it (the schema substituted).</param>
  /// <param name="source">The assembly that embeds the reverse scripts under <see cref="RESOURCE_FOLDER"/>.</param>
  /// <returns>The reversal, or null.</returns>
  /// <exception cref="InvalidOperationException">A reversible or lossy migration ships no reverse script.</exception>
  public static MigrationReversal? ForLibraryMigration(
      string fileName, string forwardSql, Func<string, string> transform, Assembly source) {
    ArgumentNullException.ThrowIfNull(fileName);
    ArgumentNullException.ThrowIfNull(transform);
    ArgumentNullException.ThrowIfNull(source);
    var kind = DeclaredBy(forwardSql);
    if (kind is not { } declared) {
      return null;
    }
    if (declared == SchemaChangeReversal.Irreversible) {
      return Irreversible;
    }
    var stem = fileName.EndsWith(".sql", StringComparison.Ordinal) ? fileName[..^".sql".Length] : fileName;
    var resource = $"{source.GetName().Name}.{RESOURCE_FOLDER}.{stem}.down.sql";
    using var stream = source.GetManifestResourceStream(resource)
      ?? throw new InvalidOperationException(
        $"Migration {fileName} declares '-- Reversal: {LedgerText(declared)}' but ships no {stem}.down.sql to reverse it.");
    using var reader = new StreamReader(stream);
    return new MigrationReversal(declared, transform(reader.ReadToEnd()));
  }

  /// <summary>The reverse of a perspective table a pass created: dropping it, and the rows in it with it.</summary>
  /// <param name="qualifiedTable">The table, schema-qualified and quoted.</param>
  /// <returns>The reversal.</returns>
  public static MigrationReversal DropTable(string qualifiedTable) {
    ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedTable);
    return new MigrationReversal(SchemaChangeReversal.Lossy, $"DROP TABLE IF EXISTS {qualifiedTable};");
  }

  /// <summary>The reverse of a perspective table a pass created in <paramref name="quotedSchema"/>: dropping it.</summary>
  /// <param name="quotedSchema">The schema, quoted.</param>
  /// <param name="table">The table, unquoted.</param>
  /// <returns>The reversal.</returns>
  public static MigrationReversal DropTable(string quotedSchema, string table) {
    ArgumentException.ThrowIfNullOrWhiteSpace(quotedSchema);
    return DropTable($"{quotedSchema}.{PgIdentifier.Quote(table)}");
  }

  /// <summary>How the ledger writes a kind.</summary>
  /// <param name="kind">The kind.</param>
  /// <returns><c>reversible</c>, <c>lossy</c> or <c>irreversible</c>.</returns>
  public static string LedgerText(SchemaChangeReversal kind) => kind switch {
    SchemaChangeReversal.Reversible => REVERSIBLE,
    SchemaChangeReversal.Lossy => LOSSY,
    _ => IRREVERSIBLE,
  };

  /// <summary>How the ledger's text reads; anything but reversible or lossy, nothing included, is irreversible.</summary>
  /// <param name="text">The ledger's text, or null.</param>
  /// <returns>The kind.</returns>
  public static SchemaChangeReversal FromLedgerText(string? text) => text switch {
    REVERSIBLE => SchemaChangeReversal.Reversible,
    LOSSY => SchemaChangeReversal.Lossy,
    _ => SchemaChangeReversal.Irreversible,
  };

  /// <summary>
  /// Records how the change the ledger names <paramref name="fileName"/> is reversed, in the transaction that applied it.
  /// Nothing for a null reversal (a migration that predates the declaration), and nothing on a ledger older than the
  /// record (377).
  /// </summary>
  /// <param name="connection">The connection applying the change, in its transaction.</param>
  /// <param name="ledger">The ledger, as the caller names it (qualified or not).</param>
  /// <param name="fileName">The ledger's name for the change.</param>
  /// <param name="reversal">How it is reversed.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>A task that completes once recorded.</returns>
  public static async Task RecordAsync(
      NpgsqlConnection connection, string ledger, string fileName, MigrationReversal? reversal, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(connection);
    if (reversal is null) {
      return;
    }
    await using (var probe = connection.CreateCommand()) {
      probe.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = to_regclass($1) AND attname = 'reversal' AND NOT attisdropped)";
      probe.Parameters.AddWithValue(ledger);
      if (await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true) {
        return;
      }
    }
    await using var command = connection.CreateCommand();
#pragma warning disable S2077 // the ledger name is the caller's own constant; every value is bound
    command.CommandText = $"UPDATE {ledger} SET reversal = $1, down_sql = $2 WHERE file_name = $3";
#pragma warning restore S2077
    command.Parameters.AddWithValue(LedgerText(reversal.Kind));
    command.Parameters.AddWithValue((object?)reversal.DownSql ?? DBNull.Value);
    command.Parameters.AddWithValue(fileName);
    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }
}
