// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data;
using Npgsql;
using Whizbang.Core.Capabilities;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Creates an extension a schema cannot exist without, or fails start saying why: the one place the generated
/// code creates pgvector, before the data source is built and before the schema pass.
/// </summary>
/// <remarks>
/// <para>
/// It reads the extension's state with the capability probe's own query first. Installed is left alone, so a role
/// that could not create it still starts against a server where an operator did. Available, or Unknown (the probe
/// could not read what it needed), is created. Any other state is known to refuse, so start fails at once.
/// </para>
/// <para>
/// A failure is an <see cref="InvalidOperationException"/> whose message is the capability verdict line: the
/// state, the fix, and every feature and column that requires the extension. A refusal only the
/// <c>CREATE EXTENSION</c> reveals carries the server's answer, with the exception as the inner one.
/// </para>
/// <para>
/// Because the data source creates the extension before it is built, and the startup probe opens its connections
/// through that data source, the verdict always sees an extension the library created as Ready.
/// </para>
/// </remarks>
/// <docs>fundamentals/perspectives/vector-fields#pgvector-extension</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Capabilities/RequiredVectorExtensionTests.cs</tests>
public static class PostgresRequiredExtension {
  /// <summary>Creates <paramref name="extension"/> unless it is installed, or throws naming the fix.</summary>
  /// <param name="connection">The connection; opened if closed, and closed again after.</param>
  /// <param name="extension">The extension.</param>
  /// <param name="scope">The name the database is reported under, for the verdict line.</param>
  /// <param name="requiredBy">The requirements in force; those naming this extension are listed in the line.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>A task that completes when the extension is installed.</returns>
  /// <exception cref="InvalidOperationException">The server will not, or did not, create it.</exception>
  public static async Task EnsureAsync(
      NpgsqlConnection connection,
      string extension,
      string scope,
      IReadOnlyList<ActiveCapabilityRequirement> requiredBy,
      CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(connection);
    ArgumentNullException.ThrowIfNull(extension);
    ArgumentNullException.ThrowIfNull(scope);
    ArgumentNullException.ThrowIfNull(requiredBy);

    var info = Describe(extension);
    var opened = false;
    if (connection.State != ConnectionState.Open) {
      await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
      opened = true;
    }
    try {
      var state = await ProbeAsync(connection, info, cancellationToken).ConfigureAwait(false);
      if (state == PostgresExtensionState.Installed) {
        return;
      }
      if (state is not (PostgresExtensionState.Available or PostgresExtensionState.Unknown)) {
        throw _refused(info, scope, state, state.ToString(), reason: null, requiredBy, inner: null);
      }
      try {
        await using var create = new NpgsqlCommand($"CREATE EXTENSION IF NOT EXISTS {_quote(extension)}", connection);
        await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
      } catch (PostgresException ex) when (OptionalExtensionBlocks.IsUnavailable(ex.SqlState)) {
        throw _refused(info, scope, StateOfRefusal(ex.SqlState), ex.SqlState, ex.MessageText, requiredBy, ex);
      }
    } finally {
      if (opened) {
        await connection.CloseAsync().ConfigureAwait(false);
      }
    }
  }

  /// <summary>The catalog entry for an extension, or a plain one (allowlisted, no preload) for one it does not list.</summary>
  internal static PostgresExtensionInfo Describe(string extension) =>
    PostgresExtensionCatalog.BuiltIn.FirstOrDefault(e => string.Equals(e.Name, extension, StringComparison.Ordinal))
      ?? new PostgresExtensionInfo(extension, extension, [], LibraryMayCreate: true, PostgresExtensionPrerequisites.Allowlist);

  /// <summary>One extension's state, read with the capability probe's query on an open connection.</summary>
  internal static async Task<PostgresExtensionState> ProbeAsync(
      NpgsqlConnection connection, PostgresExtensionInfo extension, CancellationToken cancellationToken) {
    var rows = await PostgresCapabilityProbe.ReadAsync(connection, [extension], cancellationToken).ConfigureAwait(false);
    return PostgresCapabilityProbe.DeriveState(extension, rows[extension.Name]);
  }

  /// <summary>The Postgres state a <c>CREATE EXTENSION</c> refusal reports.</summary>
  /// <param name="sqlState">One of the refusal states <see cref="OptionalExtensionBlocks.IsUnavailable"/> recognizes.</param>
  /// <returns><c>0A000</c> NotAllowlisted, <c>42501</c> NoPrivilege, anything else Missing.</returns>
  internal static PostgresExtensionState StateOfRefusal(string sqlState) => sqlState switch {
    "0A000" => PostgresExtensionState.NotAllowlisted,
    "42501" => PostgresExtensionState.NoPrivilege,
    _ => PostgresExtensionState.Missing,
  };

  private static InvalidOperationException _refused(
      PostgresExtensionInfo extension, string scope, PostgresExtensionState state, string detail, string? reason,
      IReadOnlyList<ActiveCapabilityRequirement> requiredBy, Exception? inner) {
    var status = new CapabilityStatus(PostgresExtensionCatalog.Describe(extension), scope,
      PostgresCapabilityProbe.ToCapabilityState(state), Version: null, detail, reason);
    var required = requiredBy
      .Where(r => string.Equals(r.Requirement.Capability.Provider, PostgresExtensionCatalog.PROVIDER, StringComparison.OrdinalIgnoreCase)
        && string.Equals(r.Requirement.Capability.Name, extension.Name, StringComparison.OrdinalIgnoreCase))
      .ToList();
    var line = new CapabilityVerdictEntry(status, required, CapabilityVerdictOutcome.Fail).Line;
    return new InvalidOperationException(line, inner);
  }

  private static string _quote(string identifier) =>
    "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
