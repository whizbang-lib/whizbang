using System.Linq.Expressions;

namespace Whizbang.Core.Perspectives.Hooks;

/// <summary>
/// Well-known store-managed column names an apply hook can target with
/// <see cref="IApplyHookBuilder{TMarker}.SetColumn"/>. The per-event object-mutation interpreter maps these to
/// physical <c>PerspectiveRow</c> properties; the collective SQL interpreter emits them as literal column
/// assignments. Arbitrary (developer-defined) column names are supported on the collective path only.
/// </summary>
/// <docs>fundamentals/messaging/apply-hooks</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Hooks/ApplyHookRegistryTests.cs:ApplyTimestamp_ReachesTheHookAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerEventApplyHooksTests.cs:OverrideTimestamps_SetsUpdatedAt_WithoutBumpAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerEventApplyHooksTests.cs:UnsupportedStoreColumn_ThrowsAsync</tests>
public static class ApplyHookColumns {
#pragma warning disable CA1707 // editorconfig requires all_upper constants; CA1707 forbids underscores — codebase convention is to suppress CA1707 (see CollectiveRouting.SINK_PERSPECTIVE_NAME).
  /// <summary>The <c>updated_at</c> store column (per-event: <c>PerspectiveRow.UpdatedAt</c>).</summary>
  public const string UPDATED_AT = "updated_at";

  /// <summary>The <c>created_at</c> store column (per-event: <c>PerspectiveRow.CreatedAt</c>).</summary>
  public const string CREATED_AT = "created_at";

  /// <summary>The <c>version</c> store column (per-event: <c>PerspectiveRow.Version</c>).</summary>
  public const string VERSION = "version";
#pragma warning restore CA1707
}

/// <summary>
/// One declarative mutation an apply hook recorded through its builder. A hook does not touch the database or
/// the loaded row directly; it records a list of these ops, and each path (collective SQL vs per-event object
/// mutation) interprets the SAME op list for its own mechanics. That is what lets one hook body run on both
/// paths.
/// </summary>
/// <docs>fundamentals/messaging/apply-hooks</docs>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Hooks/ApplyHookRegistryTests.cs:SetProperty_RecordsNameValueAndTypeAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Hooks/ApplyHookRegistryTests.cs:RemoveSetter_RecordsPropertyNameAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Perspectives/Hooks/ApplyHookRegistryTests.cs:CollectiveBuilder_RecordsAndWhereAndReplaceWhereAsync</tests>
public abstract record ApplyHookOp;

/// <summary>
/// Set a model data field. On the collective path it becomes a <c>jsonb_set(data, '{PropertyName}', …)</c>
/// assignment; on the per-event path it assigns the loaded row's <c>Data.&lt;PropertyName&gt;</c>.
/// </summary>
/// <param name="Selector">The original <c>TMarker</c> selector lambda (used by the per-event compiled setter).</param>
/// <param name="PropertyName">The top-level property name extracted from <paramref name="Selector"/>.</param>
/// <param name="Value">The value to assign (may be <c>null</c>).</param>
/// <param name="PropertyType">The declared property type, used to serialize the value on the collective path.</param>
public sealed record SetPropertyOp(LambdaExpression Selector, string PropertyName, object? Value, Type PropertyType) : ApplyHookOp;

/// <summary>
/// Set a physical store column. On the collective path it becomes <c>"Column" = @param</c>; on the per-event
/// path it assigns the corresponding <c>PerspectiveRow</c> property (see <see cref="ApplyHookColumns"/>).
/// </summary>
/// <param name="Column">The physical column name (e.g. <see cref="ApplyHookColumns.UpdatedAt"/>).</param>
/// <param name="Value">The value to assign (may be <c>null</c>).</param>
public sealed record SetColumnOp(string Column, object? Value) : ApplyHookOp;

/// <summary>
/// Bump the row's optimistic <c>version</c> by one — <c>version = version + 1</c> (collective) or
/// <c>row.Version++</c> (per-event). A first-class verb because it is not a constant assignment.
/// </summary>
public sealed record BumpVersionOp : ApplyHookOp;

/// <summary>
/// Declares that the applied event is NOT business activity: the row is written, but its business
/// time (<c>updated_at</c>) is left where it was.
/// </summary>
/// <remarks>
/// For integrity repairs, system backfills, reclassification passes and other maintenance-generated
/// events, which touch a row without a user or business process having acted on it. Advancing
/// business time for those would extend retention windows and lift the record to the top of recency
/// ordering. The default is opt-out — an event counts as activity unless a hook says otherwise —
/// because forgetting to declare should keep a record alive rather than silently expire it.
/// </remarks>
/// <docs>fundamentals/messaging/apply-hooks</docs>
public sealed record SuppressActivityOp : ApplyHookOp;

/// <summary>
/// Drop a model-field setter that an earlier stage (the perspective's <c>Apply</c> or a prior hook) added, by
/// property name. Collective-focused: the per-event path mutates the loaded row directly, so there is no setter
/// list to remove from and this op is a no-op there.
/// </summary>
/// <param name="PropertyName">The top-level property name whose setter should be removed.</param>
public sealed record RemoveSetterOp(string PropertyName) : ApplyHookOp;

/// <summary>
/// AND an extra predicate onto the collective cohort <c>WHERE</c>. Collective-only (a per-event apply is a
/// single known row). The predicate is over the model marker and is rebased onto the perspective row.
/// </summary>
/// <param name="Predicate">A <c>Func&lt;TMarker, bool&gt;</c> predicate over the model.</param>
public sealed record AndWhereOp(LambdaExpression Predicate) : ApplyHookOp;

/// <summary>
/// Replace the collective cohort <c>WHERE</c> entirely with this predicate — the framework still ANDs the
/// mandatory tenant scope envelope on top (D0 safety), so a hook can reshape the cohort but never escape its
/// scope. Collective-only.
/// </summary>
/// <param name="Predicate">A <c>Func&lt;TMarker, bool&gt;</c> predicate over the model.</param>
public sealed record ReplaceWhereOp(LambdaExpression Predicate) : ApplyHookOp;
