// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// How a coordinator reads the warning the claim's statistics refresh raises when it could not analyze a stale queue
/// table (#2611): only the refresh-skipped message with a <c>table=reason</c> detail is one, and anything else is left
/// alone. The end-to-end behavior is in <see cref="ClaimWorkStaleStatisticsTests"/>.
/// </summary>
/// <docs>fundamentals/work-coordinator/claim-loop#the-statistics-refresh-never-waits</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/QueueStatisticsRefreshNotices.cs</code-under-test>
[Category("Shard4")]
[Category("Integration")]
public class QueueStatisticsRefreshNoticesTests : EFCoreTestBase {
  [Test]
  [Arguments(QueueStatisticsRefreshNotices.SKIPPED, "wh_inbox_state=lock_not_available", true, "wh_inbox_state", "lock_not_available")]
  [Arguments(QueueStatisticsRefreshNotices.SKIPPED, "wh_outbox=failed", true, "wh_outbox", "failed")]
  [Arguments("whizbang.fenced", "wh_inbox_state=lock_not_available", false, "", "")]
  [Arguments(null, "wh_inbox_state=lock_not_available", false, "", "")]
  [Arguments(QueueStatisticsRefreshNotices.SKIPPED, null, false, "", "")]
  [Arguments(QueueStatisticsRefreshNotices.SKIPPED, "wh_inbox_state", false, "", "")]
  [Arguments(QueueStatisticsRefreshNotices.SKIPPED, "=lock_not_available", false, "", "")]
  [Arguments(QueueStatisticsRefreshNotices.SKIPPED, "wh_inbox_state=", false, "", "")]
  public async Task ASkipNotice_IsReadAsItsTableAndReason_AndAnyOtherIsNotAsync(
      string? message, string? detail, bool read, string table, string reason) {
    var isSkip = QueueStatisticsRefreshNotices.TryRead(message, detail, out var readTable, out var readReason);

    await Assert.That(isSkip).IsEqualTo(read);
    await Assert.That(readTable).IsEqualTo(table);
    await Assert.That(readReason).IsEqualTo(reason);
  }

  [Test]
  public async Task Watch_WithoutAConnection_IsRefusedAsync() {
    await Assert.That(() => QueueStatisticsRefreshNotices.Watch(null!, null, null)).Throws<ArgumentNullException>();
  }

  /// <summary>
  /// A coordinator registered without metrics or a logger still reads the warning and lets it go: nothing to count on,
  /// nothing to write to, and nothing thrown. Other notices on the connection are ignored, and once the scope is disposed
  /// it no longer listens.
  /// </summary>
  [Test]
  public async Task Watch_WithNoMetricsOrLogger_ReadsTheSkip_AndStopsListeningOnDisposeAsync() {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    var scope = QueueStatisticsRefreshNotices.Watch(connection, null, null);

    await _raiseAsync(connection, QueueStatisticsRefreshNotices.SKIPPED, "wh_outbox=lock_not_available");
    await _raiseAsync(connection, "some.other.notice", "x=y");
    scope.Dispose();
    await _raiseAsync(connection, QueueStatisticsRefreshNotices.SKIPPED, "wh_outbox=lock_not_available");

    await Assert.That(scope).IsNotNull().Because("disposing with a skip read and nowhere to report it does not throw");
  }

  private static async Task _raiseAsync(NpgsqlConnection connection, string message, string detail) {
    await using var cmd = connection.CreateCommand();
    cmd.CommandText = $"DO $$ BEGIN RAISE WARNING USING MESSAGE = '{message}', DETAIL = '{detail}'; END $$";
    await cmd.ExecuteNonQueryAsync();
  }
}
