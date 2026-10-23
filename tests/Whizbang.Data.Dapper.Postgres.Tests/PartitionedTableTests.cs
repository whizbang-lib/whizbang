// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Whizbang.Core.Notifications;
using Whizbang.Core.Workers;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Issue #1611: a framework table declares itself to the partition manager with its column, interval and retention
/// source, and the Dapper driver registers <c>wh_log</c> and its maintenance step. Issue #1646: <c>wh_signals</c> and
/// <c>wh_lifecycle_completions</c> join it, daily, each kept for the window its row sweep honored.
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
    await Assert.That(() => services.AddPartitionedTable((PartitionedTable)null!)).ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task TheSignals_AreDailyByCreatedAt_KeptForTheDurableSignalWindowAsync() {
    var signals = PartitionedTables.Signals(TimeSpan.FromHours(36));

    await Assert.That(signals.Table).IsEqualTo("wh_signals");
    await Assert.That(signals.PartitionColumn).IsEqualTo("created_at");
    await Assert.That(signals.Interval).IsEqualTo(_day);
    await Assert.That(signals.Retention).IsEqualTo(TimeSpan.FromHours(36))
      .Because("the partition drop honors SignalRetention, the window the signal sweep honored");
    await Assert.That(signals.RetentionSettingKey).IsNull();
  }

  [Test]
  public async Task TheLifecycleCompletions_AreDailyByEventId_KeptForTheirRetentionDaysAsync() {
    var completions = PartitionedTables.LifecycleCompletions(9);

    await Assert.That(completions.Table).IsEqualTo("wh_lifecycle_completions");
    await Assert.That(completions.PartitionColumn).IsEqualTo("event_id")
      .Because("event ids are time-ordered, so a day maps to a range of ids");
    await Assert.That(completions.Interval).IsEqualTo(_day);
    await Assert.That(completions.Retention).IsEqualTo(TimeSpan.FromDays(9));
  }

  [Test]
  [Arguments(0)]
  [Arguments(-3)]
  public async Task LifecycleCompletionsWithNoWindow_AreKeptIndefinitelyAsync(int retentionDays) {
    var completions = PartitionedTables.LifecycleCompletions(retentionDays);

    await Assert.That(completions.Retention).IsNull()
      .Because("a window of zero turns the row sweep off, so the partition drop keeps everything too");
    await Assert.That(completions.RetentionSettingKey).IsNull();
    await Assert.That(completions).IsEqualTo(PartitionedTable.RetainedIndefinitely("wh_lifecycle_completions", "event_id", _day));
  }

  [Test]
  public async Task AnIndefiniteWindow_CarriesNoWindowAsync() {
    var table = PartitionedTable.RetainedIndefinitely("wh_trace", "at", _day);

    await Assert.That(table.Retention).IsNull();
    await Assert.That(table.RetentionSettingKey).IsNull();
    await Assert.That(table.Interval).IsEqualTo(_day);
  }

  [Test]
  public async Task AddPartitionedTable_FromTheContainer_BuildsTheDeclarationOnResolveAsync() {
    var services = new ServiceCollection();
    services.Configure<WhizbangNotificationOptions>(o => o.SignalRetention = TimeSpan.FromHours(5));

    services.AddPartitionedTable(sp => PartitionedTables.Signals(sp.GetRequiredService<IOptions<WhizbangNotificationOptions>>().Value.SignalRetention));

    await using var provider = services.BuildServiceProvider();
    await Assert.That(provider.GetServices<PartitionedTable>()).IsEquivalentTo([PartitionedTables.Signals(TimeSpan.FromHours(5))]);
    await Assert.That(() => ((IServiceCollection)null!).AddPartitionedTable(_ => PartitionedTables.Log)).ThrowsExactly<ArgumentNullException>();
    await Assert.That(() => services.AddPartitionedTable((Func<IServiceProvider, PartitionedTable>)null!)).ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task TheFrameworkTables_TakeTheirWindowsFromTheConfiguredOptionsAsync() {
    var services = new ServiceCollection();
    services.Configure<WhizbangNotificationOptions>(o => o.SignalRetention = TimeSpan.FromDays(2));
    services.Configure<MaintenanceWorkerOptions>(o => o.LifecycleCompletionRetentionDays = 4);

    services.AddFrameworkPartitionedTables();

    await using var provider = services.BuildServiceProvider();
    await Assert.That(provider.GetServices<PartitionedTable>()).IsEquivalentTo(
      [PartitionedTables.Log, PartitionedTables.Signals(TimeSpan.FromDays(2)), PartitionedTables.LifecycleCompletions(4)]);
    await Assert.That(() => ((IServiceCollection)null!).AddFrameworkPartitionedTables()).ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task TheFrameworkTables_WithNothingConfigured_UseTheOptionDefaultsAsync() {
    var services = new ServiceCollection();

    services.AddFrameworkPartitionedTables();

    await using var provider = services.BuildServiceProvider();
    await Assert.That(provider.GetServices<PartitionedTable>()).IsEquivalentTo(
      [PartitionedTables.Log, PartitionedTables.Signals(TimeSpan.FromDays(7)), PartitionedTables.LifecycleCompletions(7)])
      .Because("seven days is the default of both SignalRetention and LifecycleCompletionRetentionDays");
  }
}
