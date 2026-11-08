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
/// A takeover replaces the duty with <see cref="HoldAsync"/>, which stops the old keepalive and releases the old grant
/// before holding the new one, so at most one grant and one keepalive exist at a time.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/migrations#which-instance-migrates</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/Migrations/MigratorDutyTests.cs</tests>
public sealed class MigratorDuty : IAsyncDisposable {
  private readonly TimeProvider _timeProvider;
  private readonly ILogger? _logger;
  private DutyGrantKeepalive? _keepalive;

  /// <summary>Creates the holder, holding nothing: the staging reads unstaged until an election is held.</summary>
  /// <param name="timeProvider">The clock the keepalive ticks on.</param>
  /// <param name="logger">Where the keepalive reports a verify that failed.</param>
  public MigratorDuty(TimeProvider timeProvider, ILogger? logger = null) {
    ArgumentNullException.ThrowIfNull(timeProvider);
    _timeProvider = timeProvider;
    _logger = logger;
  }

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
      _keepalive = DutyGrantKeepalive.Start(grant, _timeProvider, _logger);
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
