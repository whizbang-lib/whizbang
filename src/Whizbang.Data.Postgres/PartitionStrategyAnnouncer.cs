// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Data.Postgres;

/// <summary>
/// Remembers the last partition-management choice the maintenance cycle reported, so the choice is said when it is
/// first made and whenever it changes, not every cycle. One per process, registered with the framework's partitioned
/// tables; the maintenance steps are scoped and cannot remember it themselves.
/// </summary>
/// <docs>operations/infrastructure/partitioned-ledgers#pg-partman</docs>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/PgPartmanPartitionMaintenanceTests.cs</tests>
internal sealed class PartitionStrategyAnnouncer {
  private string? _last;

  /// <summary>Whether <paramref name="reason"/> differs from the reason reported last, which it now becomes.</summary>
  /// <param name="reason">The cycle's choice, as it is reported.</param>
  /// <returns>True when it should be reported.</returns>
  public bool IsNew(string reason) =>
    !string.Equals(Interlocked.Exchange(ref _last, reason), reason, StringComparison.Ordinal);
}
