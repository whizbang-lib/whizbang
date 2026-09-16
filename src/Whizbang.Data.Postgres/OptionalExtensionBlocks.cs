using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>
/// One piece of a schema script: the statements of an optional-extension block, named by the
/// extension they need, or the plain statements between blocks.
/// </summary>
/// <param name="Extension">The extension the statements need, or null for plain statements.</param>
/// <param name="Sql">The statements, without the <c>CREATE EXTENSION</c> line of a block.</param>
/// <docs>fundamentals/perspectives/physical-fields</docs>
public readonly record struct SchemaScriptPiece(string? Extension, string Sql);

/// <summary>
/// Applies a schema script whose index families may need an extension the server refuses.
/// </summary>
/// <remarks>
/// <para>
/// A generated schema marks the statements that need an extension with a block, opened by a line
/// naming the extension and closed by a line of its own. The reader creates the extension once per
/// block and applies the block when that succeeds. When the server refuses the extension, it skips
/// the block, warns once naming the extension and the indexes it is running without, and applies
/// everything else, so the pass completes. A substring query against a table without its trigram
/// index scans, which is what it does wherever the index is absent.
/// </para>
/// <para>
/// A refusal is one of three answers: <c>0A000</c> from a managed server that does not allow-list
/// the extension, <c>42501</c> from a role without the privilege, <c>58P01</c> from a build without
/// the extension's files. Anything else is a defect and propagates. Applied as one batch, any of
/// the three failed the whole perspective pass, and a service with one substring index paid a
/// failed startup attempt on every start without ever getting the index.
/// </para>
/// <para>
/// Inside a transaction the extension statement runs under a savepoint, because a failed statement
/// otherwise poisons the transaction the rest of the pass runs in.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/physical-fields</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/OptionalExtensionBlocksTests.cs</tests>
public static class OptionalExtensionBlocks {
#pragma warning disable CA1707 // Repo style: public const fields are ALL_CAPS_SNAKE per editorconfig.
  /// <summary>Opens a block; the extension name follows on the same line.</summary>
  public const string BEGIN_MARKER = "-- @whizbang:optional-extension ";

  /// <summary>Closes a block.</summary>
  public const string END_MARKER = "-- @whizbang:optional-extension-end";
#pragma warning restore CA1707

  private const string SAVEPOINT = "wh_optional_extension";
  private const string CREATE_INDEX = "CREATE INDEX ";
  private const string IF_NOT_EXISTS = "IF NOT EXISTS ";

  private static readonly ImmutableHashSet<string> _unavailable =
    ImmutableHashSet.Create(StringComparer.Ordinal, "0A000", "42501", "58P01");

  /// <summary>Whether <paramref name="sql"/> carries at least one block.</summary>
  /// <param name="sql">The script, or null.</param>
  /// <returns><see langword="true"/> when a block is present.</returns>
  /// <remarks>
  /// Asked of a script before anything has validated it, on the path that decides how to apply one,
  /// so no script is no block rather than a throw.
  /// </remarks>
  public static bool HasBlocks(string? sql) =>
    sql?.Contains(BEGIN_MARKER, StringComparison.Ordinal) == true;

  /// <summary>Whether a SQL state is one of the ways a server refuses an extension.</summary>
  /// <param name="sqlState">The state.</param>
  /// <returns><see langword="true"/> for a refusal, <see langword="false"/> for anything else.</returns>
  public static bool IsUnavailable(string? sqlState) =>
    sqlState is not null && _unavailable.Contains(sqlState);

  /// <summary>
  /// Splits a script into its plain pieces and its blocks, in order.
  /// </summary>
  /// <param name="sql">The script.</param>
  /// <returns>The pieces. A block's piece carries its extension and omits the extension statement.</returns>
  public static ImmutableArray<SchemaScriptPiece> Split(string sql) {
    ArgumentNullException.ThrowIfNull(sql);

    var pieces = ImmutableArray.CreateBuilder<SchemaScriptPiece>();
    var current = new List<string>();
    string? extension = null;

    foreach (var raw in sql.Split('\n')) {
      var line = raw.TrimEnd('\r');
      var trimmed = line.Trim();
      if (trimmed.StartsWith(BEGIN_MARKER, StringComparison.Ordinal)) {
        _flush(pieces, current, null);
        extension = trimmed[BEGIN_MARKER.Length..].Trim();
        continue;
      }
      if (trimmed.Equals(END_MARKER, StringComparison.Ordinal)) {
        _flush(pieces, current, extension);
        extension = null;
        continue;
      }
      if (extension is not null && trimmed.StartsWith("CREATE EXTENSION", StringComparison.OrdinalIgnoreCase)) {
        // The reader issues this itself, guarded; the block keeps only what depends on it.
        continue;
      }
      current.Add(line);
    }
    _flush(pieces, current, extension);

    return pieces.ToImmutable();
  }

  /// <summary>The names of the indexes a piece would create, for reporting.</summary>
  /// <param name="sql">The piece's statements.</param>
  /// <returns>The names, in order.</returns>
  public static ImmutableArray<string> IndexNames(string sql) {
    ArgumentNullException.ThrowIfNull(sql);

    var names = ImmutableArray.CreateBuilder<string>();
    foreach (var raw in sql.Split('\n')) {
      var line = raw.Trim();
      if (!line.StartsWith(CREATE_INDEX, StringComparison.OrdinalIgnoreCase)) {
        continue;
      }
      var rest = line[CREATE_INDEX.Length..].TrimStart();
      if (rest.StartsWith(IF_NOT_EXISTS, StringComparison.OrdinalIgnoreCase)) {
        rest = rest[IF_NOT_EXISTS.Length..].TrimStart();
      }
      var end = rest.IndexOf(' ', StringComparison.Ordinal);
      names.Add(end < 0 ? rest : rest[..end]);
    }
    return names.ToImmutable();
  }

  /// <summary>
  /// Applies a script, creating each block's extension once and skipping a block whose extension
  /// the server refuses.
  /// </summary>
  /// <param name="connection">The connection; opened for the apply if closed, and closed after.</param>
  /// <param name="transaction">The open transaction, if any; a block's extension runs under a savepoint in it.</param>
  /// <param name="sql">The script.</param>
  /// <param name="commandTimeoutSeconds">The timeout for one piece.</param>
  /// <param name="logger">Optional logger.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The extensions that were refused, one per skipped block.</returns>
  public static async Task<ImmutableArray<string>> ApplyAsync(
      NpgsqlConnection connection,
      NpgsqlTransaction? transaction,
      string sql,
      int commandTimeoutSeconds,
      ILogger? logger = null,
      CancellationToken cancellationToken = default) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentNullException.ThrowIfNull(sql);

    var log = logger ?? NullLogger.Instance;
    var skipped = ImmutableArray.CreateBuilder<string>();
    var opened = false;
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
      opened = true;
    }

    try {
      foreach (var piece in Split(sql)) {
        if (string.IsNullOrWhiteSpace(piece.Sql)) {
          continue;
        }
        if (piece.Extension is null) {
          await _executeAsync(connection, transaction, piece.Sql, commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
          continue;
        }
        if (await _tryCreateExtensionAsync(connection, transaction, piece.Extension, commandTimeoutSeconds, log, piece.Sql, cancellationToken).ConfigureAwait(false)) {
          await _executeAsync(connection, transaction, piece.Sql, commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
        } else {
          skipped.Add(piece.Extension);
        }
      }
    } finally {
      if (opened) {
        await connection.CloseAsync().ConfigureAwait(false);
      }
    }

    return skipped.ToImmutable();
  }

  private static async Task<bool> _tryCreateExtensionAsync(
      NpgsqlConnection connection, NpgsqlTransaction? transaction, string extension, int commandTimeoutSeconds,
      ILogger log, string dependentSql, CancellationToken cancellationToken) {
    if (transaction is not null) {
      await _executeAsync(connection, transaction, $"SAVEPOINT {SAVEPOINT}", commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
    }

    try {
      await _executeAsync(connection, transaction, $"CREATE EXTENSION IF NOT EXISTS {_quote(extension)}", commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
    } catch (PostgresException ex) when (IsUnavailable(ex.SqlState)) {
      if (transaction is not null) {
        await _executeAsync(connection, transaction, $"ROLLBACK TO SAVEPOINT {SAVEPOINT}", commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
      }
      OptionalExtensionLog.ExtensionUnavailable(log, extension, ex.SqlState, ex.MessageText, string.Join(", ", IndexNames(dependentSql)));
      return false;
    }

    if (transaction is not null) {
      await _executeAsync(connection, transaction, $"RELEASE SAVEPOINT {SAVEPOINT}", commandTimeoutSeconds, cancellationToken).ConfigureAwait(false);
    }
    return true;
  }

  private static async Task _executeAsync(
      NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, int commandTimeoutSeconds, CancellationToken cancellationToken) {
    await using var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = commandTimeoutSeconds };
    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  private static string _quote(string identifier) =>
    "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

  private static void _flush(ImmutableArray<SchemaScriptPiece>.Builder pieces, List<string> current, string? extension) {
    if (current.Count > 0) {
      pieces.Add(new SchemaScriptPiece(extension, string.Join("\n", current)));
      current.Clear();
    }
  }
}

/// <summary>Source-generated logging for the optional-extension reader.</summary>
internal static partial class OptionalExtensionLog {
  [LoggerMessage(
      Level = LogLevel.Warning,
      Message = "Extension {Extension} is unavailable on this server ({SqlState}: {Detail}); the indexes that need it "
              + "were skipped and queries they would answer will scan instead: {Indexes}")]
  public static partial void ExtensionUnavailable(ILogger logger, string extension, string sqlState, string detail, string indexes);
}
