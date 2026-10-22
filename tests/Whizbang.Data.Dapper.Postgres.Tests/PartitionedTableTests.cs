// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Issue #1611: a framework table declares itself to the partition manager with its column, interval and retention
/// source, and the Dapper driver registers <c>wh_log</c> and its maintenance step.
/// </summary>
/// <docs>operations/infrastructure/partitioned-ledgers#how-a-table-registers</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/PartitionedTable.cs</code-under-test>
public class PartitionedTableTests {
  private static readonly TimeSpan _day = TimeSpan.FromDays(1);

  [Test]
  public async Task TheLog_IsWeeklyByLoggedAt_KeptForItsRetentionSettingAsync() {
    var log = PartitionedTables.Log;

    await Assert.That(log.Table).IsEqualTo("wh_log");
    await Assert.That(log.PartitionColumn).IsEqualTo("logged_at");
    await Assert.That(log.Interval).IsEqualTo(TimeSpan.FromDays(7));
    await Assert.That(log.Retention).IsNull();
    await Assert.That(log.RetentionSettingKey).IsEqualTo("log_retention_days")
      .Because("the partition drop honors the window the row sweep honored, set the same way");
    await Assert.That(log.RetentionDefaultDays).IsEqualTo(14);
  }

  [Test]
  public async Task AFixedWindow_IsCarriedAsIsAsync() {
    var table = PartitionedTable.RetainedFor("wh_trace", "at", _day, TimeSpan.FromDays(3));

    await Assert.That(table.Retention).IsEqualTo(TimeSpan.FromDays(3));
    await Assert.That(table.RetentionSettingKey).IsNull();
  }

  [Test]
  public async Task AnIntervalThatIsNotWholeHours_IsRejectedAsync() {
    await Assert.That(() => PartitionedTable.RetainedFor("wh_trace", "at", TimeSpan.FromMinutes(90), _day))
      .ThrowsExactly<ArgumentOutOfRangeException>();
    await Assert.That(() => PartitionedTable.RetainedFor("wh_trace", "at", TimeSpan.FromMinutes(30), _day))
      .ThrowsExactly<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task BlankNamesAndNegativeWindows_AreRejectedAsync() {
    await Assert.That(() => PartitionedTable.RetainedFor(" ", "at", _day, _day)).ThrowsExactly<ArgumentException>();
    await Assert.That(() => PartitionedTable.RetainedFor("wh_trace", " ", _day, _day)).ThrowsExactly<ArgumentException>();
    await Assert.That(() => PartitionedTable.RetainedFor("wh_trace", "at", _day, TimeSpan.FromDays(-1)))
      .ThrowsExactly<ArgumentOutOfRangeException>();
    await Assert.That(() => PartitionedTable.RetainedBySetting("wh_trace", "at", _day, " ", 1)).ThrowsExactly<ArgumentException>();
    await Assert.That(() => PartitionedTable.RetainedBySetting("wh_trace", "at", _day, "trace_days", -1))
      .ThrowsExactly<ArgumentOutOfRangeException>();
  }

  [Test]
  public async Task AddPartitionedTable_RegistersTheDeclaration_AndRejectsNullsAsync() {
    var services = new ServiceCollection();

    services.AddPartitionedTable(PartitionedTables.Log);

    await using var provider = services.BuildServiceProvider();
    await Assert.That(provider.GetServices<PartitionedTable>()).IsEquivalentTo([PartitionedTables.Log]);
    await Assert.That(() => ((IServiceCollection)null!).AddPartitionedTable(PartitionedTables.Log)).ThrowsExactly<ArgumentNullException>();
    await Assert.That(() => services.AddPartitionedTable(null!)).ThrowsExactly<ArgumentNullException>();
  }
}
