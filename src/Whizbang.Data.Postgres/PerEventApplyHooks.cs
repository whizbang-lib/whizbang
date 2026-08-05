using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using Whizbang.Core.Perspectives.Hooks;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The folded per-event apply-hook contributions for one upsert: the effective <c>updated_at</c> stamp, whether
/// to bump <c>version</c>, and any model-field setters to apply to the loaded row object. Produced once per
/// upsert by <see cref="PerEventApplyHooks.Resolve"/>.
/// </summary>
/// <param name="UpdatedAt">The <c>updated_at</c> value a hook set, or <c>null</c> to use the write site's own now.</param>
/// <param name="BumpVersion">Whether a hook asked to bump the row version.</param>
/// <param name="ModelFieldSetters">Model-field setters (<c>SetProperty</c>) to apply to the row's data object.</param>
/// <docs>fundamentals/messaging/apply-hooks</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerEventApplyHooksTests.cs:DefaultTimestampsHook_YieldsUpdatedAtAndBumpAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerEventApplyHooksTests.cs:SetProperty_IsCarried_AndApplyModelSettersMutatesTheObjectAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerEventApplyHooksTests.cs:OverrideTimestamps_SetsUpdatedAt_WithoutBumpAsync</tests>
public sealed record PerEventApplyHookPlan(
  DateTimeOffset? UpdatedAt,
  bool BumpVersion,
  IReadOnlyList<SetPropertyOp> ModelFieldSetters);

/// <summary>
/// Process-wide accessor + interpreter for the per-event (loaded-row object mutation) apply hooks, used by the
/// EF Core upsert strategy and the Dapper perspective store. A process-wide static — mirroring
/// <c>BaseUpsertStrategy.PathOnePersistenceOptionsProvider</c> — because the per-event write sites are
/// constructed in many places (often <c>new</c>'d without DI), and the default (the <c>whizbang.timestamps</c>
/// hook) must apply everywhere with zero wiring so existing consumers keep their exact <c>updated_at</c>/version
/// stamping. A consumer registers custom per-event hooks by adding to <see cref="Registry"/> at startup.
/// </summary>
/// <docs>fundamentals/messaging/apply-hooks</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerEventApplyHooksTests.cs:OverrideTimestamps_SetsUpdatedAt_WithoutBumpAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PerEventApplyHooksTests.cs:SetProperty_IsCarried_AndApplyModelSettersMutatesTheObjectAsync</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/Perspectives/DapperPostgresPerspectiveStoreTests.cs:DapperUpsert_PerEventHook_SetsPropertyAndOverridesUpdatedAtAsync</tests>
[SuppressMessage("AOT", "IL2026:RequiresUnreferencedCode", Justification = "SetProperty compiles a setter from the hook's compile-time selector metadata, not runtime type scanning — the established data-layer tradeoff (CollectiveSettersRewriter).")]
[SuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "SetProperty compiles a setter from spec/hook selector metadata known at build time, not synthesized at runtime.")]
public static class PerEventApplyHooks {
  private static readonly ConcurrentDictionary<(Type Model, string Property), Action<object, object?>> _setterCache = new();

  /// <summary>
  /// The per-event hook registry, seeded with the framework defaults (<c>whizbang.timestamps</c>: stamp
  /// <c>updated_at</c> + bump version). Replace or extend it at startup to customize; the setter must not be null.
  /// </summary>
  public static ApplyHookRegistry Registry { get; set; } = WhizbangApplyHooks.CreatePerEventWithDefaults();

  /// <summary>
  /// Resolve the matching per-event hooks for the context's model and fold them into a
  /// <see cref="PerEventApplyHookPlan"/>. <c>SetColumn(updated_at)</c> sets the stamp; <c>BumpVersion</c> flags
  /// the bump; <c>SetProperty</c> is carried for object mutation; <c>RemoveSetter</c> is a no-op (single row).
  /// Any other <c>SetColumn</c> target throws — arbitrary physical store columns are collective-only.
  /// </summary>
  public static PerEventApplyHookPlan Resolve(ApplyHookContext context) => Resolve(Registry, context);

  /// <summary>
  /// Resolve against an explicit registry (the write sites use the static <see cref="Registry"/> overload; this
  /// overload keeps the fold logic unit-testable without touching process-wide state).
  /// </summary>
  public static PerEventApplyHookPlan Resolve(ApplyHookRegistry registry, ApplyHookContext context) {
    ArgumentNullException.ThrowIfNull(registry);
    ArgumentNullException.ThrowIfNull(context);
    DateTimeOffset? updatedAt = null;
    var bump = false;
    var setters = new List<SetPropertyOp>();

    foreach (var produce in registry.ResolveFor(context.ModelType)) {
      foreach (var op in produce(context)) {
        switch (op) {
          case SetColumnOp { Column: ApplyHookColumns.UPDATED_AT } column:
            updatedAt = _asDateTimeOffset(column.Value);
            break;
          case SetColumnOp column:
            throw new NotSupportedException(
              $"Per-event apply hooks can only SetColumn '{ApplyHookColumns.UPDATED_AT}' (got '{column.Column}'). " +
              "Arbitrary physical store columns are supported on the collective path only; a per-event apply mutates " +
              "the loaded row object, which has no arbitrary column.");
          case BumpVersionOp:
            bump = true;
            break;
          case SetPropertyOp setProperty:
            setters.Add(setProperty);
            break;
          default:
            // RemoveSetter / where-ops: no-op for a single-row per-event apply.
            break;
        }
      }
    }
    return new PerEventApplyHookPlan(updatedAt, bump, setters);
  }

  /// <summary>Apply the plan's <c>SetProperty</c> model-field setters to <paramref name="model"/> in place.</summary>
  public static void ApplyModelSetters<TModel>(TModel model, IReadOnlyList<SetPropertyOp> setters) where TModel : class {
    ArgumentNullException.ThrowIfNull(model);
    ArgumentNullException.ThrowIfNull(setters);
    foreach (var setter in setters) {
      var apply = _setterCache.GetOrAdd((typeof(TModel), setter.PropertyName), _ => _compileSetter(typeof(TModel), setter));
      apply(model, setter.Value);
    }
  }

  private static DateTimeOffset? _asDateTimeOffset(object? value) => value switch {
    null => null,
    DateTimeOffset dto => dto,
    DateTime dt => new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt),
    _ => throw new NotSupportedException(
      $"Per-event SetColumn('{ApplyHookColumns.UPDATED_AT}') expects a DateTimeOffset or DateTime value; got {value.GetType().Name}."),
  };

  // Compile a type-erased setter: (object target, object? value) => ((TModel)target).Prop = (TProp)value.
  // The property comes from the hook's compile-time selector metadata; cached per (model, property).
  private static Action<object, object?> _compileSetter(Type modelType, SetPropertyOp setter) {
    var body = setter.Selector.Body;
    while (body is UnaryExpression { NodeType: ExpressionType.Convert } convert) {
      body = convert.Operand;
    }
    if (body is not MemberExpression { Member: PropertyInfo property }) {
      throw new NotSupportedException(
        $"Per-event SetProperty only supports a top-level property selector; got {body.NodeType} for '{setter.PropertyName}'.");
    }
    var targetParam = Expression.Parameter(typeof(object), "target");
    var valueParam = Expression.Parameter(typeof(object), "value");
    var assign = Expression.Assign(
      Expression.Property(Expression.Convert(targetParam, modelType), property),
      Expression.Convert(valueParam, property.PropertyType));
    return Expression.Lambda<Action<object, object?>>(assign, targetParam, valueParam).Compile();
  }
}
