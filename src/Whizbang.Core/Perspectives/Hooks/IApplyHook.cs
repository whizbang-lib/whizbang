namespace Whizbang.Core.Perspectives.Hooks;

/// <summary>
/// A per-event apply hook: pluggable logic that modifies what a perspective's <c>Apply</c> produced for a
/// single loaded row, on the per-event upsert path. It is gated by <typeparamref name="TMarker"/> — it fires for
/// a model <c>TModel</c> exactly when <c>TModel</c> is assignable to <typeparamref name="TMarker"/> (so a
/// concrete model, a base class, or an interface like <c>IAuditable</c> all work).
/// </summary>
/// <typeparam name="TMarker">The concrete class, base class, or interface this hook is gated on.</typeparam>
/// <remarks>
/// The hook records mutations through the builder rather than performing them; the framework interprets the
/// recorded <see cref="ApplyHookOp"/> list as object mutation on the loaded row. Deliberately parallel to
/// <see cref="ICollectiveApplyHook{TMarker}"/> — same registration DSL, same builder verbs — but a separate
/// interface because the two execution mechanics (object mutation vs SQL compile) differ.
/// </remarks>
/// <docs>fundamentals/messaging/apply-hooks</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Hooks/ApplyHookRegistryTests.cs:PerEventRegistry_Marker_Order_And_KeyedOverrideAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerEventApplyHooksTests.cs:SetProperty_IsCarried_AndApplyModelSettersMutatesTheObjectAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerEventApplyHooksTests.cs:NonMatchingMarkerHook_DoesNotContributeSettersAsync</tests>
public interface IApplyHook<TMarker> {
  /// <summary>Record the mutations this hook wants applied to the row.</summary>
  /// <param name="builder">The op-recording builder, typed to <typeparamref name="TMarker"/>.</param>
  /// <param name="context">The per-apply context (model type, event, scope, timestamp).</param>
  void Configure(IApplyHookBuilder<TMarker> builder, ApplyHookContext context);
}

/// <summary>
/// A collective apply hook: pluggable logic that modifies the set-based collective <c>UPDATE</c> a perspective's
/// collective <c>Apply</c> produced, before it is compiled to SQL. Same marker-gated matching as
/// <see cref="IApplyHook{TMarker}"/>; fires for a model <c>TModel</c> when <c>TModel</c> is assignable to
/// <typeparamref name="TMarker"/>.
/// </summary>
/// <typeparam name="TMarker">The concrete class, base class, or interface this hook is gated on.</typeparam>
/// <remarks>
/// The hook records mutations (set model fields, set store columns, bump version, refine or replace the cohort
/// <c>WHERE</c>) through the collective builder; the executor rebases and compiles them into the single
/// set-based UPDATE, on both the EF Core and Dapper drivers.
/// </remarks>
/// <docs>fundamentals/messaging/apply-hooks</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Hooks/ApplyHookRegistryTests.cs:ConcreteMarker_FiresForThatModelAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Hooks/ApplyHookRegistryTests.cs:NonAssignableMarker_IsSkippedAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Collective/DapperCollectiveApplierIntegrationTests.cs:Hook_SetProperty_OverridesSpecField_AndLastRegisteredWinsAsync</tests>
public interface ICollectiveApplyHook<TMarker> {
  /// <summary>Record the mutations this hook wants applied to the collective UPDATE.</summary>
  /// <param name="builder">The op-recording collective builder, typed to <typeparamref name="TMarker"/>.</param>
  /// <param name="context">The per-apply context (model type, event, scope, timestamp).</param>
  void Configure(ICollectiveApplyHookBuilder<TMarker> builder, ApplyHookContext context);
}
