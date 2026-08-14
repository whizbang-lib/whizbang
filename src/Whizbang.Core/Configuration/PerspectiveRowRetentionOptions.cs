namespace Whizbang.Core.Configuration;

/// <summary>
/// Operator configuration for perspective row retention — the runtime rung of the override
/// ladder (framework default → derived from <c>[Ephemeral(TtlRow)]</c> events → explicit
/// <c>[RowTtl]</c> on the perspective → this). Applied to
/// <c>PerspectiveTtlRegistry.ApplyRuntimeConfiguration</c> at startup by the worker pipeline,
/// so a TTL can be retuned — or retention switched off — per environment without a redeploy.
/// </summary>
/// <docs>fundamentals/perspectives/row-retention</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/PerspectiveTtlRegistryTests.cs</tests>
public sealed class PerspectiveRowRetentionOptions {
  /// <summary>
  /// Global kill switch (default <c>true</c>). When <c>false</c>, every model resolves to
  /// no-TTL: stamping stops, the lens expiry filter stops hiding rows, and the resurrection
  /// probe stands down — one consult point keeps all seams coherent. Rows whose stamps were
  /// written before the switch may still physically reap until those stamps drain; Sourced
  /// rows remain recoverable via resurrection-on-wake once re-enabled.
  /// </summary>
  public bool Enabled { get; set; } = true;

  /// <summary>
  /// Per-model TTL overrides keyed by the read model's full CLR name (e.g.
  /// <c>"MyApp.Chat.ConversationModel"</c>). A value replaces the declared TTL (seconds);
  /// <c>null</c> disables retention for that model only. Overrides outrank both the
  /// generated registration and <c>[RowTtl]</c>.
  /// </summary>
  public Dictionary<string, int?> Overrides { get; } = new(StringComparer.Ordinal);
}
