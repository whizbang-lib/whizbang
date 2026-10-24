// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Npgsql;
using Whizbang.Core.Capabilities;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The Postgres provider's provisioning actions: <c>CREATE EXTENSION</c> for an extension the probe found available,
/// and <c>ALTER EXTENSION ... UPDATE</c> for one installed at an older version than the server's default.
/// </summary>
/// <remarks>
/// <para>
/// Each action is one transaction on a connection of its own (from the same database registration the probe uses):
/// it takes the schema lock (<see cref="SchemaInitializationLockKey"/> for the schema the extension is created in,
/// <c>current_schema()</c>) with <c>pg_advisory_xact_lock</c>, so a migration pass or another instance's
/// provisioning of that schema runs before or after it and never alongside, and the lock cannot outlive the
/// transaction behind a pooler. Under the lock it reads the extension again: one another session created or upgraded
/// meanwhile is recorded as already present, and one this role turns out unable to create comes back in the state
/// the probe reads, without trying.
/// </para>
/// <para>
/// The create is <see cref="PostgresRequiredExtension"/>'s own. A refusal it classifies comes back as the state it
/// reveals, with the server's SQLSTATE as the detail and its message as the reason. A refused upgrade leaves the
/// extension Ready at its installed version, already present, with the refusal as the reason. After a commit the
/// extension is read back, so the status carries the version the server reports.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/capabilities#provisioning</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PostgresCapabilityProvisioningIntegrationTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/PostgresExtensions/PostgresCapabilityProviderTests.cs</tests>
internal sealed partial class PostgresCapabilityProvisioner : ICapabilityProvisioner {
#pragma warning disable CA1707
  /// <summary>The event id of the Information line written when another session holds the schema lock.</summary>
  internal const int WAITING_FOR_SCHEMA_LOCK_EVENT = 1;
#pragma warning restore CA1707

  private readonly Dictionary<string, PostgresCapabilityDatabase> _databases;
  private readonly ILogger<PostgresCapabilityProvisioner> _logger;

  /// <summary>Creates the provisioner over the registered databases; a name registered twice is the first one.</summary>
  public PostgresCapabilityProvisioner(IEnumerable<PostgresCapabilityDatabase> databases, ILogger<PostgresCapabilityProvisioner> logger) {
    ArgumentNullException.ThrowIfNull(databases);
    ArgumentNullException.ThrowIfNull(logger);
    _databases = databases.DistinctBy(d => d.Name, StringComparer.Ordinal).ToDictionary(d => d.Name, StringComparer.Ordinal);
    _logger = logger;
  }

  /// <inheritdoc />
  public string Provider => PostgresExtensionCatalog.PROVIDER;

  /// <inheritdoc />
  public async Task<CapabilityStatus> ProvisionAsync(
      CapabilityStatus status, CapabilityProvisionAction action, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(status);
    var extension = PostgresRequiredExtension.Describe(status.Descriptor.Id.Name);
    var connection = await _databases[status.Scope].OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    await using (connection.ConfigureAwait(false)) {
      CapabilityProvisioningOutcome outcome;
      var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
      await using (transaction.ConfigureAwait(false)) {
        await _lockSchemaAsync(connection, transaction, status, cancellationToken).ConfigureAwait(false);
        var row = await _readAsync(connection, extension, cancellationToken).ConfigureAwait(false);
        if (action == CapabilityProvisionAction.Upgrade) {
          if (CapabilityVersion.IsNewer(row.DefaultVersion, row.InstalledVersion)) {
            var refusal = await _updateAsync(connection, extension.Name, cancellationToken).ConfigureAwait(false);
            if (refusal is not null) {
              return status with {
                Provisioning = CapabilityProvisioningOutcome.Present,
                Reason = $"upgrade to {row.DefaultVersion} refused ({refusal.SqlState}: {refusal.MessageText})",
              };
            }
            outcome = CapabilityProvisioningOutcome.Upgraded;
          } else {
            outcome = CapabilityProvisioningOutcome.Present;
          }
        } else {
          var state = PostgresCapabilityProbe.DeriveState(extension, row);
          if (state == PostgresExtensionState.Installed) {
            outcome = CapabilityProvisioningOutcome.Present;
          } else if (state is PostgresExtensionState.Available or PostgresExtensionState.Unknown) {
            var refusal = await PostgresRequiredExtension.TryCreateAsync(connection, extension.Name, cancellationToken).ConfigureAwait(false);
            if (refusal is not null) {
              return status with {
                State = PostgresCapabilityProbe.ToCapabilityState(PostgresRequiredExtension.StateOfRefusal(refusal.SqlState)),
                Detail = refusal.SqlState,
                Reason = refusal.MessageText,
              };
            }
            outcome = CapabilityProvisioningOutcome.Created;
          } else {
            return PostgresCapabilityProvider.Status(status.Descriptor, status.Scope, extension, row);
          }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
      }
      var after = await _readAsync(connection, extension, cancellationToken).ConfigureAwait(false);
      return PostgresCapabilityProvider.Status(status.Descriptor, status.Scope, extension, after) with { Provisioning = outcome };
    }
  }

  private static async Task<PostgresExtensionProbeRow> _readAsync(
      NpgsqlConnection connection, PostgresExtensionInfo extension, CancellationToken cancellationToken) =>
    (await PostgresCapabilityProbe.ReadAsync(connection, [extension], cancellationToken).ConfigureAwait(false))[extension.Name];

  /// <summary>
  /// Takes the schema lock of the schema <c>CREATE EXTENSION</c> installs into, in the transaction: at once when it is
  /// free, otherwise saying so and waiting for it with no client timeout, as schema initialization does.
  /// </summary>
  private async Task _lockSchemaAsync(
      NpgsqlConnection connection, NpgsqlTransaction transaction, CapabilityStatus status, CancellationToken cancellationToken) {
    string? schema;
    await using (var current = new NpgsqlCommand("SELECT current_schema()", connection, transaction)) {
      schema = await current.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }
    var key = SchemaInitializationLockKey.Compute(schema);
    await using (var attempt = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@key)", connection, transaction)) {
      attempt.Parameters.AddWithValue("key", key);
      if (await attempt.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true) {
        return;
      }
    }
    LogWaitingForSchemaLock(_logger, status.Descriptor.Id, status.Scope, schema);
    await using var wait = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", connection, transaction) { CommandTimeout = 0 };
    wait.Parameters.AddWithValue("key", key);
    await wait.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
  }

  /// <summary>Runs <c>ALTER EXTENSION ... UPDATE</c>; null once done, the server's answer when it refuses.</summary>
  private static async Task<PostgresException?> _updateAsync(NpgsqlConnection connection, string extension, CancellationToken cancellationToken) {
    try {
      await using var update = new NpgsqlCommand($"ALTER EXTENSION {PostgresRequiredExtension.Quote(extension)} UPDATE", connection);
      await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
      return null;
    } catch (PostgresException ex) {
      return ex;
    }
  }

  [LoggerMessage(EventId = WAITING_FOR_SCHEMA_LOCK_EVENT, Level = LogLevel.Information,
    Message = "Provisioning {Capability} on '{Scope}' waits for the schema lock of '{Schema}', which another session holds")]
  static partial void LogWaitingForSchemaLock(ILogger logger, CapabilityId capability, string scope, string? schema);
}
