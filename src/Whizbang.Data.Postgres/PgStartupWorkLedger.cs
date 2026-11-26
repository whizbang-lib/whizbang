// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using Whizbang.Core.Startup;

namespace Whizbang.Data.Postgres;

#pragma warning disable S2325, CA1822, IDE0060, CS9113 // RED stub, replaced by the implementation

/// <summary>
/// The database's side of heavy startup work (#2596) for every Postgres driver: the once-per-build record
/// (<c>wh_startup_work</c>) and the fleet's heavy-start permits (<c>wh_startup_permits</c>), read and written only
/// through the functions migration 630 installs, so a mixed-version fleet runs one rule.
/// </summary>
/// <param name="connectionFactory">Opens a connection of its own for each call.</param>
/// <param name="schema">The schema, bare.</param>
/// <docs>operations/startup/startup-pipeline#heavy-startup-work</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupWorkLedgerSqlTests.cs</tests>
public sealed class PgStartupWorkLedger(Func<NpgsqlConnection> connectionFactory, string schema) : IStartupWorkLedger {
  /// <inheritdoc />
  public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => throw new NotImplementedException();

  /// <inheritdoc />
  public Task<StartupWorkClaim> ClaimAsync(
      string work, string fingerprint, Guid instanceId, bool force, CancellationToken cancellationToken) =>
    throw new NotImplementedException();

  /// <inheritdoc />
  public Task CompleteAsync(string work, string fingerprint, Guid instanceId, bool succeeded, CancellationToken cancellationToken) =>
    throw new NotImplementedException();

  /// <inheritdoc />
  public Task<StartupPermitAnswer> TryAcquirePermitAsync(Guid instanceId, int cap, CancellationToken cancellationToken) =>
    throw new NotImplementedException();

  /// <inheritdoc />
  public Task ReleasePermitAsync(Guid instanceId, CancellationToken cancellationToken) => throw new NotImplementedException();
}
