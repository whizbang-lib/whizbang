// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// A PostgreSQL upsert strategy that never takes the atomic statement, for a test that has to exercise
/// the Entity Framework write path. It selects the path on its own instance, so it cannot change which
/// path any other strategy, in this test or a sibling, takes.
/// </summary>
public sealed class EntityFrameworkPathUpsertStrategy : PostgresUpsertStrategy {
  /// <inheritdoc/>
  protected override bool UsesAtomicUpsert => false;
}

/// <summary>Choosing a write path explicitly, per strategy instance.</summary>
public static class UpsertWritePath {
  /// <summary>
  /// The strategy for one side of a test that runs on both write paths: the atomic statement, or the
  /// Entity Framework path the strategy uses when the atomic statement declines a row.
  /// </summary>
  /// <param name="atomic">True for the atomic statement, false for the Entity Framework path.</param>
  /// <returns>A new strategy that takes the named path.</returns>
  public static PostgresUpsertStrategy Strategy(bool atomic) =>
    atomic ? new PostgresUpsertStrategy() : new EntityFrameworkPathUpsertStrategy();
}

/// <summary>
/// Counts the commands Entity Framework creates. The atomic upsert sends its statement on the
/// connection directly, so it creates none; the Entity Framework path reads the row and saves through
/// the context, so it creates at least one. Zero commands across an upsert is the proof that the atomic
/// statement wrote the row.
/// </summary>
public sealed class EntityFrameworkCommandCounter : DbCommandInterceptor {
  private int _created;

  /// <summary>The number of commands Entity Framework has created through this interceptor.</summary>
  public int Created => Volatile.Read(ref _created);

  /// <inheritdoc/>
  public override DbCommand CommandCreated(CommandEndEventData eventData, DbCommand result) {
    Interlocked.Increment(ref _created);
    return result;
  }
}
