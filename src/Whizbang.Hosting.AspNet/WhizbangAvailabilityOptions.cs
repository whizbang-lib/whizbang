namespace Whizbang.Hosting.AspNet;

/// <summary>
/// Turnkey defaults for the schema-availability gate that <see cref="ServiceCollectionExtensions.AddWhizbangAspNet"/>
/// injects automatically. Out of the box the gate is <b>on</b> in <see cref="AvailabilityGateMode.MutationsOnly"/>
/// mode — during a startup migration reads pass through and only writes get 503, then it becomes a
/// pass-through once the schema is ready. Override to change the mode or turn it off.
/// </summary>
/// <docs>resilience/database-availability-middleware</docs>
/// <tests>tests/Whizbang.Hosting.AspNet.Tests/WhizbangAvailabilityStartupFilterTests.cs:Enabled_GatePresent_WriteIsGatedAsync</tests>
/// <tests>tests/Whizbang.Hosting.AspNet.Tests/WhizbangAvailabilityStartupFilterTests.cs:Disabled_NotGatedAsync</tests>
/// <tests>tests/Whizbang.Hosting.AspNet.Tests/AddWhizbangAspNetTurnkeyTests.cs:AvailabilityOptions_DefaultToMutationsOnlyAndEnabledAsync</tests>
public sealed class WhizbangAvailabilityOptions {
  /// <summary>Whether the availability gate is injected. Default <see langword="true"/>.</summary>
  public bool Enabled { get; set; } = true;

  /// <summary>
  /// How the gate treats requests while the schema is not ready. Default
  /// <see cref="AvailabilityGateMode.MutationsOnly"/> (serve reads, 503 writes).
  /// </summary>
  public AvailabilityGateMode Mode { get; set; } = AvailabilityGateMode.MutationsOnly;

  /// <summary>
  /// Path prefixes that always pass through even before the schema is ready. <see langword="null"/>
  /// uses <see cref="DatabaseAvailabilityMiddleware.DefaultExemptPaths"/> (<c>/alive</c>, <c>/health</c>, <c>/version</c>).
  /// </summary>
  public IReadOnlyList<string>? ExemptPaths { get; set; }
}
