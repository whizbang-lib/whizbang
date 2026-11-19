// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Whizbang.Core.Startup;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The migrator duty this instance holds through schema initialization, and the keepalive that holds its lease from
/// the election until initialization returns (#2127).
/// </summary>
/// <remarks>
/// <para>
/// The schema initializer creates one before it elects and disposes it when it returns, however it returns, so a
/// grant this instance wins is renewed on its role's renew interval through every phase that follows (the stored-form
/// scans, the waits, the index rebuild, each migration step) and released at the end. No phase can run outside the
/// keepalive, because holding the grant here is what starts it: there is no other place a startup grant lives.
/// </para>
/// <para>
/// The keepalive renews only while the schema work reports progress (#2165): past <see cref="StallLimitFor"/> with
/// nothing reported, it stops renewing, so a migrator that is alive but stuck loses the role and another instance takes
/// the work over. Progress reaches the duty through <see cref="StartupProgress.ReportTo"/>, which the schema
/// initializer points at <see cref="NoteProgress"/> for as long as it holds the duty.
/// </para>
/// <para>
/// A takeover replaces the duty with <see cref="HoldAsync"/>, which stops the old keepalive and releases the old grant
/// before holding the new one, so at most one grant and one keepalive exist at a time.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations#which-instance-migrates</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/MigratorDutyTests.cs</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/MigratorStallLimitTests.cs</tests>
public sealed class MigratorDuty : IAsyncDisposable {
  private readonly TimeProvider _timeProvider;
  private readonly ILogger? _logger;
  private readonly TimeSpan? _stallLimit;
  private DutyGrantKeepalive? _keepalive;

  /// <summary>Creates the holder, holding nothing: the staging reads unstaged until an election is held.</summary>
  /// <param name="timeProvider">The clock the keepalive ticks on.</param>
  /// <param name="logger">Where the keepalive reports a verify that failed, and a stall.</param>
  /// <param name="stallLimit">How long the work may report no progress before the keepalive stops renewing; null never
  /// stops (<see cref="StallLimitFor"/> is what the schema initializer passes).</param>
  public MigratorDuty(TimeProvider timeProvider, ILogger? logger = null, TimeSpan? stallLimit = null) {
    ArgumentNullException.ThrowIfNull(timeProvider);
    _timeProvider = timeProvider;
    _logger = logger;
    _stallLimit = stallLimit;
  }

  /// <summary>
  /// The stall limit a migrator is held to (#2165): <see cref="RoleAssignmentOptions.MigratorStallLimit"/> when set,
  /// otherwise the schema command budget plus the migrator's progress window, since one schema statement may run for
  /// the whole budget before it fails.
  /// </summary>
  /// <param name="options">The role options; null reads the defaults.</param>
  /// <param name="schemaCommandTimeoutSeconds">The schema command budget, in seconds.</param>
  /// <returns>The limit.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/MigratorStallLimitTests.cs:TheStallLimit_IsTheSchemaCommandBudgetPlusTheMigratorsProgressWindow_ByDefaultAsync</tests>
  public static TimeSpan StallLimitFor(RoleAssignmentOptions? options, int schemaCommandTimeoutSeconds) {
    var roles = options ?? new RoleAssignmentOptions();
    return roles.MigratorStallLimit > TimeSpan.Zero
      ? roles.MigratorStallLimit
      : TimeSpan.FromSeconds(schemaCommandTimeoutSeconds) + roles.ProgressWindowFor(StartupDuties.MIGRATOR);
  }

  /// <summary>The schema work made progress: the held grant's stall limit is measured from now (#2165).</summary>
  public void NoteProgress() => _keepalive?.NoteProgress();

  /// <summary>What the last election decided; unstaged before one is held.</summary>
  public SchemaStaging Staging { get; private set; } = new(SchemaStage.Unstaged, null, "staged startup was not attempted");

  /// <summary>What this instance does: migrate, wait, or migrate under the lock alone.</summary>
  public SchemaStage Stage => Staging.Stage;

  /// <summary>The grant held, when this instance is the migrator.</summary>
  public IDutyGrant? Grant => Staging.Grant;

  /// <summary>Whether a grant is held and its lease is being renewed now.</summary>
  public bool IsKeptAlive => _keepalive?.IsRunning == true;

  /// <summary>
  /// Holds what an election decided: releases any grant held before, then keeps the new one alive until the next
  /// <see cref="HoldAsync"/>, <see cref="ReleaseAsync"/> or dispose.
  /// </summary>
  /// <param name="staging">The election's outcome.</param>
  /// <returns>A task that completes once the previous grant is released and the new one held.</returns>
  public async Task HoldAsync(SchemaStaging staging) {
    await ReleaseAsync().ConfigureAwait(false);
    Staging = staging;
    if (staging.Grant is { } grant) {
      _keepalive = DutyGrantKeepalive.Start(grant, _timeProvider, _logger, _stallLimit);
    }
  }

  /// <summary>Stops the keepalive and releases the grant, if one is held; the staging then reads unstaged.</summary>
  /// <returns>A task that completes once the grant is released.</returns>
  public async Task ReleaseAsync() {
    // The keepalive first: once the grant is given back, nothing may renew it.
    if (_keepalive is { } keepalive) {
      _keepalive = null;
      await keepalive.DisposeAsync().ConfigureAwait(false);
    }
    if (Staging.Grant is { } grant) {
      await grant.DisposeAsync().ConfigureAwait(false);
    }
    Staging = new SchemaStaging(SchemaStage.Unstaged, null, "the migrator duty was released");
  }

  /// <inheritdoc />
  public async ValueTask DisposeAsync() => await ReleaseAsync().ConfigureAwait(false);
}
