namespace Whizbang.Core.Attributes;

/// <summary>
/// Declares a row time-to-live directly on a perspective class: the perspective's rows expire after
/// the given idle window (sliding — every applied event re-anchors the window to that event's own
/// time), disappear from lens reads at the expiry instant, and are physically reaped by maintenance.
/// Row lifecycle is a property of the <em>read model</em>, so this works for Sourced perspectives
/// too — their event log stays durable, a rebuild reproduces the original windows (idle streams
/// rebuild born-expired), and a reaped row re-folds from the log if its stream wakes up.
/// </summary>
/// <remarks>
/// Resolution follows the uniform override ladder: no TTL (default) → derived from
/// <c>[Ephemeral(Storage = TransientStorage.TtlRow, TtlSeconds = …)]</c> events → an explicit
/// <c>[RowTtl]</c> here (wins over derived) → runtime configuration override. The perspective-runner
/// generator resolves this at compile time and registers the model in
/// <c>PerspectiveTtlRegistry</c> via a module initializer — zero reflection at runtime.
/// <see cref="Seconds"/> takes precedence over <see cref="Days"/> when both are set (tests,
/// sub-day tuning); leaving both unset makes the attribute a no-op.
/// </remarks>
/// <docs>fundamentals/perspectives/row-retention</docs>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class RowTtlAttribute : Attribute {
  /// <summary>Row TTL in whole days. Ignored when <see cref="Seconds"/> is set.</summary>
  public int Days { get; init; } = -1;

  /// <summary>Row TTL in seconds; takes precedence over <see cref="Days"/> when set.</summary>
  public int Seconds { get; init; } = -1;
}
