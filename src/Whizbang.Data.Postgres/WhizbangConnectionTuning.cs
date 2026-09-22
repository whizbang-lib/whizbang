using Npgsql;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Opt-in Npgsql tuning for Whizbang workloads.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why opt-in and not a default.</b> Automatic statement preparation is the right shape for the
/// framework's hot loop — a small, fixed set of statements executed continuously on long-lived
/// connections — but it is NOT safe to enable blindly, because transaction-pooling proxies break it.
/// Measured against a managed PgBouncer in transaction mode: the first execution after the pooler
/// swapped server connections failed with <c>08P01: prepared statement did not exist</c>, while the
/// same loop against the direct port ran clean. A hosted pooler also commonly issues
/// <c>DISCARD ALL</c> between assignments, which silently drops every prepared statement the driver
/// believes it still holds.
/// </para>
/// <para>
/// Enable this only when the connection string points at the database directly, or at a pooler
/// verified to support protocol-level prepared statements (PgBouncer 1.21+ with
/// <c>max_prepared_statements</c> configured).
/// </para>
/// <para>
/// <b>Expected magnitude, honestly.</b> Most of the framework's hot statements are single
/// function calls whose outer parse is trivial and whose inner plans plpgsql already caches per
/// session, so the win concentrates on the raw non-function statements. Worth having where it is
/// safe; not worth a broken pooler anywhere.
/// </para>
/// </remarks>
/// <docs>operations/configuration/connection-tuning</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WhizbangConnectionTuningTests.cs</tests>
public static class WhizbangConnectionTuning {

  private const int DEFAULT_MAX_AUTO_PREPARE = 16;
  private const int DEFAULT_AUTO_PREPARE_MIN_USAGES = 3;

  /// <summary>Default statements kept auto-prepared per connection.</summary>
  public static readonly int DefaultMaxAutoPrepare = DEFAULT_MAX_AUTO_PREPARE;

  /// <summary>Default executions of a statement before it is prepared.</summary>
  public static readonly int DefaultAutoPrepareMinUsages = DEFAULT_AUTO_PREPARE_MIN_USAGES;

  /// <summary>
  /// Enables automatic statement preparation on the builder, unless the connection string already
  /// configured it — an explicit consumer setting is never overridden.
  /// </summary>
  /// <param name="builder">The data-source builder to tune.</param>
  /// <param name="maxAutoPrepare">Statements kept prepared per connection.</param>
  /// <param name="autoPrepareMinUsages">Executions before a statement is prepared.</param>
  /// <returns>The same builder, for chaining.</returns>
  public static NpgsqlDataSourceBuilder EnableAutoPrepare(
      this NpgsqlDataSourceBuilder builder,
      int maxAutoPrepare = DEFAULT_MAX_AUTO_PREPARE,
      int autoPrepareMinUsages = DEFAULT_AUTO_PREPARE_MIN_USAGES) {
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentOutOfRangeException.ThrowIfLessThan(maxAutoPrepare, 1);
    ArgumentOutOfRangeException.ThrowIfLessThan(autoPrepareMinUsages, 1);

    // Zero is Npgsql's default (auto-prepare off). Non-zero means the consumer chose a value, and
    // their choice wins: this helper is a turnkey default, not an override.
    if (builder.ConnectionStringBuilder.MaxAutoPrepare == 0) {
      builder.ConnectionStringBuilder.MaxAutoPrepare = maxAutoPrepare;
      builder.ConnectionStringBuilder.AutoPrepareMinUsages = autoPrepareMinUsages;
    }
    return builder;
  }
}
