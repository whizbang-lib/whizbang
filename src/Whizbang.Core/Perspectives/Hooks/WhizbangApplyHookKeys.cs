namespace Whizbang.Core.Perspectives.Hooks;

/// <summary>
/// The public, documented keys under which the framework registers its own default apply hooks. A developer
/// overrides a default by re-registering a hook with the SAME key — the registration replaces the framework's
/// hook in place (keeping its order position) rather than adding a second one.
/// </summary>
/// <remarks>
/// These keys are stable API: renaming one is a breaking change because consumer overrides are matched by the
/// string value.
/// </remarks>
/// <docs>fundamentals/messaging/apply-hooks</docs>
public static class WhizbangApplyHookKeys {
#pragma warning disable CA1707 // editorconfig requires all_upper constants; CA1707 forbids underscores — codebase convention is to suppress CA1707 (see CollectiveRouting.SINK_PERSPECTIVE_NAME).
  /// <summary>
  /// Key of the built-in hook that stamps <c>updated_at = ctx.ApplyTimestamp</c> and bumps <c>version</c> on
  /// every apply, on both paths. Registered against marker <see cref="object"/> so it fires for every model.
  /// Re-register a hook with this key to change (or suppress) the store-managed stamping.
  /// </summary>
  public const string TIMESTAMPS = "whizbang.timestamps";
#pragma warning restore CA1707
}
