namespace Whizbang.Core.Perspectives.Hooks;

/// <summary>
/// The immutable context handed to every apply hook when it runs, on both the collective (set-based SQL
/// <c>UPDATE</c>) and the per-event (loaded-row object mutation) path. It carries everything a hook needs to
/// decide what to mutate that isn't already expressible through the builder verbs — most importantly the single
/// <see cref="ApplyTimestamp"/> that the framework's own <c>whizbang.timestamps</c> default hook stamps into
/// <c>updated_at</c>.
/// </summary>
/// <remarks>
/// <para>
/// The context is deliberately path-neutral so the SAME hook body works on both paths. Fields that only one
/// path can populate are nullable: <see cref="Event"/> and <see cref="Scope"/> are always present on the
/// collective path (the <c>ICollectiveEvent</c> and its <c>ICollectiveScope</c>), and best-effort on the
/// per-event path.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/apply-hooks</docs>
public sealed record ApplyHookContext {
  /// <summary>The perspective model type (<c>TModel</c>) whose apply is being hooked.</summary>
  public required Type ModelType { get; init; }

  /// <summary>
  /// The event/message driving this apply — the <c>ICollectiveEvent</c> on the collective path; the applied
  /// event (or <c>null</c> when the write site doesn't thread one) on the per-event path.
  /// </summary>
  public object? Event { get; init; }

  /// <summary>
  /// The scope of this apply — the <c>ICollectiveScope</c> on the collective path; the <c>PerspectiveScope</c>
  /// (or <c>null</c>) on the per-event path.
  /// </summary>
  public object? Scope { get; init; }

  /// <summary>
  /// One timestamp for the whole apply. On the collective path it is shared across every keyset batch of a
  /// single event so the entire cohort stamps the same <c>updated_at</c>; on the per-event path it is the
  /// single row's write time. This is what the default <c>whizbang.timestamps</c> hook writes.
  /// </summary>
  public required DateTimeOffset ApplyTimestamp { get; init; }
}
