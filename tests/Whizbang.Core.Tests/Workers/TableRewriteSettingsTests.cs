// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// #2319: the table-rewrite detector's settings are bound from <c>Whizbang:Workers:Maintenance</c>, validated, and
/// published on every start as the configured value or a default; the threshold's default is 2.0 for an online tool and
/// 3.0 for <c>VACUUM FULL</c> or detection only.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/TableRewriteSettings.cs</code-under-test>
/// <docs>operations/infrastructure/storage-maintenance#rewrite-thresholds</docs>
[Category("Core")]
public class TableRewriteSettingsTests {
  private static TableRewritePlan _plan(string? strategy) => new(strategy, "test");

  private static Dictionary<string, RetentionSetting> _byKey(IReadOnlyList<RetentionSetting> settings) =>
    settings.ToDictionary(s => s.Key, StringComparer.Ordinal);

  [Test]
  [Arguments(TableRewriteStrategies.PG_SQUEEZE, 2.0)]
  [Arguments(TableRewriteStrategies.PG_REPACK, 2.0)]
  [Arguments(TableRewriteStrategies.ONLINE, 2.0)]
  [Arguments(TableRewriteStrategies.VACUUM_FULL, 3.0)]
  [Arguments(null, 3.0)]
  public async Task TheThreshold_DefaultsByHowTheStartRewritesAsync(string? strategy, double expected) {
    var options = new MaintenanceWorkerOptions();

    await Assert.That(TableRewriteSettings.EffectiveThreshold(options, _plan(strategy))).IsEqualTo(expected);
    await Assert.That(TableRewriteSettings.IsOnline(_plan(strategy))).IsEqualTo(expected == 2.0);
  }

  [Test]
  public async Task EveryStart_PublishesAllThreeKeysAsync() {
    var settings = _byKey(TableRewriteSettings.SettingsFor(new MaintenanceWorkerOptions(), _plan(TableRewriteStrategies.PG_SQUEEZE)));

    await Assert.That(settings.Keys).IsEquivalentTo([
      TableRewriteSettings.THRESHOLD_KEY, TableRewriteSettings.MIN_ROWS_KEY, TableRewriteSettings.REGROWTH_FACTOR_KEY,
    ]).Because("configuration is the source of truth, so every start writes all three, defaults included");
    await Assert.That(settings["table_rewrite_bloat_threshold"].Value).IsEqualTo("2");
    await Assert.That(settings["table_rewrite_min_rows"].Value).IsEqualTo("1000");
    await Assert.That(settings["table_rewrite_min_rows"].ValueType).IsEqualTo("integer");
    await Assert.That(settings["table_rewrite_regrowth_factor"].Value).IsEqualTo("1.5");
    await Assert.That(settings["table_rewrite_bloat_threshold"].Source).IsEqualTo("Whizbang:Workers:Maintenance:TableRewriteBloatThreshold");
  }

  [Test]
  public async Task AConfiguredValue_WinsOverEveryDefaultAsync() {
    var options = new MaintenanceWorkerOptions {
      TableRewriteBloatThreshold = 2.75,
      TableRewriteMinRows = 50,
      TableRewriteRegrowthFactor = 1.25,
    };

    var settings = _byKey(TableRewriteSettings.SettingsFor(options, _plan(TableRewriteStrategies.PG_SQUEEZE)));

    await Assert.That(settings["table_rewrite_bloat_threshold"].Value).IsEqualTo("2.75");
    await Assert.That(settings["table_rewrite_min_rows"].Value).IsEqualTo("50");
    await Assert.That(settings["table_rewrite_regrowth_factor"].Value).IsEqualTo("1.25");
    await Assert.That(TableRewriteSettings.EffectiveThreshold(options, _plan(null))).IsEqualTo(2.75);
  }

  [Test]
  public async Task TheOptions_BindFromTheMaintenanceSectionAsync() {
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
      ["Whizbang:Workers:Maintenance:TableRewriteBloatThreshold"] = "2.5",
      ["Whizbang:Workers:Maintenance:TableRewriteMinRows"] = "250",
      ["Whizbang:Workers:Maintenance:TableRewriteRegrowthFactor"] = "1.75",
    }).Build();
    var services = new ServiceCollection();
    services.AddOptions<MaintenanceWorkerOptions>().Bind(configuration.GetSection("Whizbang:Workers:Maintenance"));

    var options = services.BuildServiceProvider().GetRequiredService<IOptions<MaintenanceWorkerOptions>>().Value;

    await Assert.That(options.TableRewriteBloatThreshold).IsEqualTo(2.5);
    await Assert.That(options.TableRewriteMinRows).IsEqualTo(250L);
    await Assert.That(options.TableRewriteRegrowthFactor).IsEqualTo(1.75);
  }

  [Test]
  public async Task Unset_IsNullAsync() {
    var options = new MaintenanceWorkerOptions();

    await Assert.That(options.TableRewriteBloatThreshold).IsNull();
    await Assert.That(options.TableRewriteMinRows).IsNull();
    await Assert.That(options.TableRewriteRegrowthFactor).IsNull();
  }

  [Test]
  [Arguments(1.0, null, null, "TableRewriteBloatThreshold")]
  [Arguments(double.NaN, null, null, "TableRewriteBloatThreshold")]
  [Arguments(double.PositiveInfinity, null, null, "TableRewriteBloatThreshold")]
  [Arguments(null, -1L, null, "TableRewriteMinRows")]
  [Arguments(null, null, 1.0, "TableRewriteRegrowthFactor")]
  [Arguments(null, null, double.NaN, "TableRewriteRegrowthFactor")]
  public async Task Validate_RejectsEachValueOutsideItsRangeByNameAsync(double? threshold, long? minRows, double? factor, string key) {
    var options = new MaintenanceWorkerOptions {
      TableRewriteBloatThreshold = threshold,
      TableRewriteMinRows = minRows,
      TableRewriteRegrowthFactor = factor,
    };

    var failures = TableRewriteSettings.Validate(options);

    await Assert.That(failures.Count).IsEqualTo(1);
    await Assert.That(failures[0]).Contains("Whizbang:Workers:Maintenance:" + key);
  }

  [Test]
  public async Task Validate_AcceptsUnsetAndInRangeValuesAsync() {
    await Assert.That(TableRewriteSettings.Validate(new MaintenanceWorkerOptions())).IsEmpty();
    await Assert.That(TableRewriteSettings.Validate(new MaintenanceWorkerOptions {
      TableRewriteBloatThreshold = 1.01,
      TableRewriteMinRows = 0,
      TableRewriteRegrowthFactor = 1.01,
    })).IsEmpty();
  }

  [Test]
  public async Task TheValidator_RejectsTheTableRewriteSettingsByNameAsync() {
    var result = new MaintenanceWorkerOptionsValidator().Validate(null, new MaintenanceWorkerOptions { TableRewriteBloatThreshold = 0.5 });

    await Assert.That(result.Failed).IsTrue();
    await Assert.That(result.FailureMessage).Contains("TableRewriteBloatThreshold");
  }
}
