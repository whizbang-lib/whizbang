// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;

namespace Whizbang.Core.Workers;

/// <summary>
/// The table-rewrite detector's database-side settings (#2319): the bloat threshold, the row floor and the post-rewrite
/// regrowth factor, each the configured value or a default, published to <c>wh_settings</c> on every start.
/// </summary>
/// <remarks>
/// <para>
/// The SQL reads all three from <c>wh_settings</c> (<c>table_rewrite_bloat_threshold</c>, <c>table_rewrite_min_rows</c>,
/// <c>table_rewrite_regrowth_factor</c>). They used to be rows migration 089 seeded and nothing else wrote, so a
/// different value took a hand-written <c>UPDATE</c> in every service database. Configuration is now the source of
/// truth: the boot-time <c>Rewrite</c> step writes the effective value of each on every start, before it reads the
/// candidates, so a value edited by hand in <c>wh_settings</c> is put back on the next start.
/// </para>
/// <para>
/// The threshold's default depends on how the start rewrites. An online tool (pg_squeeze, pg_repack, the
/// <see cref="TableRewriteStrategy.Online"/> choice, or <see cref="TableRewriteStrategy.Auto"/> when it resolves to one)
/// takes no long lock, so it is worth running at <see cref="DEFAULT_ONLINE_BLOAT_THRESHOLD"/> (2.0); a blocking
/// <c>VACUUM FULL</c>, or detection only, keeps <see cref="DEFAULT_BLOAT_THRESHOLD"/> (3.0). A low line is safe online
/// because the regrowth floor stops a table being rewritten again while it reads no worse than its last rewrite left it.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/storage-maintenance#rewrite-thresholds</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/TableRewriteSettingsTests.cs</tests>
public static class TableRewriteSettings {
  /// <summary>The <c>wh_settings</c> key holding the bloat threshold.</summary>
  public const string THRESHOLD_KEY = "table_rewrite_bloat_threshold";

  /// <summary>The <c>wh_settings</c> key holding the fewest rows a table must hold to be measured.</summary>
  public const string MIN_ROWS_KEY = "table_rewrite_min_rows";

  /// <summary>The <c>wh_settings</c> key holding the post-rewrite regrowth factor.</summary>
  public const string REGROWTH_FACTOR_KEY = "table_rewrite_regrowth_factor";

  /// <summary>The bloat threshold when a start rewrites with <c>VACUUM FULL</c> or only detects: 3.0.</summary>
  public const double DEFAULT_BLOAT_THRESHOLD = 3.0;

  /// <summary>The bloat threshold when a start rewrites with an online tool: 2.0.</summary>
  public const double DEFAULT_ONLINE_BLOAT_THRESHOLD = 2.0;

  /// <summary>The fewest rows a table must hold to be measured: 1,000.</summary>
  public const long DEFAULT_MIN_ROWS = 1000;

  /// <summary>How far past its last post-rewrite ratio a table must regrow to be offered again: 1.5.</summary>
  public const double DEFAULT_REGROWTH_FACTOR = 1.5;

  private const string SECTION = "Whizbang:Workers:Maintenance:";
  private const string THRESHOLD_SOURCE = SECTION + nameof(MaintenanceWorkerOptions.TableRewriteBloatThreshold);
  private const string MIN_ROWS_SOURCE = SECTION + nameof(MaintenanceWorkerOptions.TableRewriteMinRows);
  private const string REGROWTH_SOURCE = SECTION + nameof(MaintenanceWorkerOptions.TableRewriteRegrowthFactor);

  /// <summary>Whether <paramref name="plan"/> rewrites with an online tool: pg_squeeze, pg_repack or the Online choice.</summary>
  /// <param name="plan">The start's resolved plan.</param>
  /// <returns>True for an online tool.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TableRewriteSettingsTests.cs:TheThreshold_DefaultsByHowTheStartRewritesAsync</tests>
  public static bool IsOnline(TableRewritePlan plan) =>
    plan.Strategy is TableRewriteStrategies.PG_SQUEEZE or TableRewriteStrategies.PG_REPACK or TableRewriteStrategies.ONLINE;

  /// <summary>The threshold in force: the configured value, else 2.0 for an online tool and 3.0 otherwise.</summary>
  /// <param name="options">The maintenance options.</param>
  /// <param name="plan">The start's resolved plan.</param>
  /// <returns>The effective threshold.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TableRewriteSettingsTests.cs:TheThreshold_DefaultsByHowTheStartRewritesAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TableRewriteSettingsTests.cs:AConfiguredValue_WinsOverEveryDefaultAsync</tests>
  public static double EffectiveThreshold(MaintenanceWorkerOptions options, TableRewritePlan plan) {
    ArgumentNullException.ThrowIfNull(options);
    return options.TableRewriteBloatThreshold ?? (IsOnline(plan) ? DEFAULT_ONLINE_BLOAT_THRESHOLD : DEFAULT_BLOAT_THRESHOLD);
  }

  /// <summary>The three settings a start publishes, each the configured value or its default.</summary>
  /// <param name="options">The maintenance options.</param>
  /// <param name="plan">The start's resolved plan, which decides the threshold's default.</param>
  /// <returns>One entry per key, in the literal form the SQL casts.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TableRewriteSettingsTests.cs:EveryStart_PublishesAllThreeKeysAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TableRewriteSettingsTests.cs:AConfiguredValue_WinsOverEveryDefaultAsync</tests>
  public static IReadOnlyList<RetentionSetting> SettingsFor(MaintenanceWorkerOptions options, TableRewritePlan plan) {
    ArgumentNullException.ThrowIfNull(options);
    return [
      new(THRESHOLD_KEY, _literal(EffectiveThreshold(options, plan)), "string", THRESHOLD_SOURCE),
      new(MIN_ROWS_KEY, (options.TableRewriteMinRows ?? DEFAULT_MIN_ROWS).ToString(CultureInfo.InvariantCulture), "integer", MIN_ROWS_SOURCE),
      new(REGROWTH_FACTOR_KEY, _literal(options.TableRewriteRegrowthFactor ?? DEFAULT_REGROWTH_FACTOR), "string", REGROWTH_SOURCE),
    ];
  }

  /// <summary>Configuration errors in the three options, by key.</summary>
  /// <param name="options">The bound options.</param>
  /// <returns>One message per invalid value; empty when they are valid.</returns>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TableRewriteSettingsTests.cs:Validate_RejectsEachValueOutsideItsRangeByNameAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Workers/TableRewriteSettingsTests.cs:Validate_AcceptsUnsetAndInRangeValuesAsync</tests>
  internal static List<string> Validate(MaintenanceWorkerOptions options) {
    var failures = new List<string>();
    if (options.TableRewriteBloatThreshold is double threshold && !_aboveOne(threshold)) {
      failures.Add($"{THRESHOLD_SOURCE} must be a finite value greater than 1 (was {_literal(threshold)}); "
        + $"it sets wh_settings.{THRESHOLD_KEY}, and a lean table reads about 1, so a lower line would rewrite every table.");
    }
    if (options.TableRewriteMinRows is long rows && rows < 0) {
      failures.Add($"{MIN_ROWS_SOURCE} must be zero or greater (was {rows.ToString(CultureInfo.InvariantCulture)}); "
        + $"it sets wh_settings.{MIN_ROWS_KEY}.");
    }
    if (options.TableRewriteRegrowthFactor is double factor && !_aboveOne(factor)) {
      failures.Add($"{REGROWTH_SOURCE} must be a finite value greater than 1 (was {_literal(factor)}); "
        + $"it sets wh_settings.{REGROWTH_FACTOR_KEY}, and at 1 or less a table its last rewrite left lean is offered again at once.");
    }
    return failures;
  }

  private static bool _aboveOne(double value) => double.IsFinite(value) && value > 1;

  private static string _literal(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
