namespace Whizbang.Core.Perspectives.Hooks;

/// <summary>
/// Factory for apply-hook registries pre-seeded with the framework's built-in default hooks. Both the DI
/// registration and the "no registry supplied" fallback on the apply path build their registry through here, so
/// the default <see cref="WhizbangApplyHookKeys.TIMESTAMPS"/> stamping (<c>updated_at</c> + version bump) is
/// present everywhere — and a consumer overrides it by re-registering the same key on the returned registry.
/// </summary>
/// <docs>fundamentals/messaging/apply-hooks</docs>
public static class WhizbangApplyHooks {
  /// <summary>A fresh collective registry seeded with the default <see cref="TimestampsApplyHook"/>.</summary>
  public static CollectiveApplyHookRegistry CreateCollectiveWithDefaults() =>
    new CollectiveApplyHookRegistry().Register<object>(new TimestampsApplyHook(), WhizbangApplyHookKeys.TIMESTAMPS);

  /// <summary>A fresh per-event registry seeded with the default <see cref="TimestampsApplyHook"/>.</summary>
  public static ApplyHookRegistry CreatePerEventWithDefaults() =>
    new ApplyHookRegistry().Register<object>(new TimestampsApplyHook(), WhizbangApplyHookKeys.TIMESTAMPS);
}
