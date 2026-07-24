namespace Whizbang.Core.Perspectives.Hooks;

/// <summary>
/// The framework's built-in default apply hook, registered under
/// <see cref="WhizbangApplyHookKeys.Timestamps"/> against marker <see cref="object"/> so it fires for every
/// model on both paths. It stamps <c>updated_at = ctx.ApplyTimestamp</c> and bumps <c>version</c> — formalizing
/// the store-managed stamping that both the per-event upsert and (since the collective set-based UPDATE reached
/// parity) the collective path always did, and making it overridable: re-register a hook with the same key to
/// change or suppress it.
/// </summary>
/// <remarks>
/// Implements BOTH hook interfaces with a shared body so the exact same stamping runs on the collective and the
/// per-event path. The builder verbs it uses (<see cref="IApplyHookBuilder{TMarker}.SetColumn"/> +
/// <see cref="IApplyHookBuilder{TMarker}.BumpVersion"/>) are on the shared base surface.
/// </remarks>
/// <docs>fundamentals/messaging/apply-hooks</docs>
public sealed class TimestampsApplyHook : ICollectiveApplyHook<object>, IApplyHook<object> {
  /// <inheritdoc />
  public void Configure(ICollectiveApplyHookBuilder<object> builder, ApplyHookContext context)
    => _stamp(builder, context);

  /// <inheritdoc />
  public void Configure(IApplyHookBuilder<object> builder, ApplyHookContext context)
    => _stamp(builder, context);

  private static void _stamp(IApplyHookBuilder<object> builder, ApplyHookContext context) {
    ArgumentNullException.ThrowIfNull(builder);
    ArgumentNullException.ThrowIfNull(context);
    builder.SetColumn(ApplyHookColumns.UPDATED_AT, context.ApplyTimestamp).BumpVersion();
  }
}
